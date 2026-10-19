using VertexAI.Services.Search;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.RateLimiting;
using VertexAI.Services;
using VertexAI.Services.Attachments;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Health;
using VertexAI.Services.Quota;
using VertexAI.Services.Runs;
using Microsoft.Extensions.Options;

namespace VertexAI.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVertexApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GeminiSettings>(configuration.GetSection("VertexAI"));
        services.Configure<FirebaseSettings>(configuration.GetSection("Firebase"));
        services.Configure<PersistenceSettings>(configuration.GetSection("Persistence"));
        services.Configure<WorkspaceSettings>(configuration.GetSection("Workspace"));
        services.Configure<OpenAICompatibleSettings>(configuration.GetSection("OpenAICompatible"));
        services.Configure<QuotaSettings>(configuration.GetSection("Quota"));
        services.AddApplicationServices(configuration);
        services.AddVertexRateLimiting();

        services.AddHealthChecks()
            .AddCheck<FirestoreHealthCheck>("firestore", tags: ["ready"]);

        return services;
    }

    private static IServiceCollection AddVertexRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(RateLimitPolicies.Partition);
        });

        return services;
    }

    private static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddHttpClient(nameof(OpenAICompatibleChatModelClient));

        services.AddSingleton<GeminiSafetyPolicy>();
        services.AddScoped<GeminiService>();
        services.AddScoped<IChatModelProvider, GeminiProvider>();
        foreach (var providerSettings in LoadOpenAICompatibleProviders(configuration))
        {
            services.AddScoped<IChatModelProvider>(sp =>
                new OpenAICompatibleProvider(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    providerSettings));
        }
        services.AddScoped<IChatProviderCatalog, ChatProviderCatalog>();
        services.AddScoped<IChatModelClient>(sp => sp.GetRequiredService<IChatProviderCatalog>().CreateClient("gemini"));
        services.AddSingleton(_ => CreateFirebaseAuth(configuration));
        services.AddScoped<IUserContext, FirebaseUserContext>();
        services.AddSingleton(sp => new FirestoreDbBuilder
        {
            ProjectId = ResolveFirestoreProjectId(configuration),
            // Uses the emulator only when FIRESTORE_EMULATOR_HOST is set (local runs, end-to-end tests); otherwise production.
            EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction
        }.Build());
        services.AddSingleton(_ => StorageClient.Create());
        services.AddSingleton<IChatAttachmentStore>(sp =>
        {
            // Only build the Cloud Storage store (which needs credentials) when a bucket is configured.
            var bucket = Environment.GetEnvironmentVariable("ATTACHMENT_STORAGE_BUCKET")
                ?? sp.GetRequiredService<IOptions<PersistenceSettings>>().Value.AttachmentStorageBucket;
            return string.IsNullOrWhiteSpace(bucket)
                ? new NoOpChatAttachmentStore()
                : ActivatorUtilities.CreateInstance<CloudStorageChatAttachmentStore>(sp);
        });
        services.AddScoped<FirestoreConversationStore>();
        services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<FirestoreConversationStore>());
        services.AddScoped<FirestoreChatQuotaService>();
        services.AddScoped<IChatQuotaService>(sp => sp.GetRequiredService<FirestoreChatQuotaService>());
        services.AddScoped<IQuotaUsageReader, FirestoreQuotaUsageReader>();
        services.AddScoped<IChatRequestAugmenter, WebSearchInstructionAugmenter>();

        services.Configure<WebSearchSettings>(configuration.GetSection("WebSearch"));
        services.AddSingleton<IWebSearchBackend>(sp =>
        {
            var search = sp.GetRequiredService<IOptions<WebSearchSettings>>().Value;
            if (search.Backend == "fake")
            {
                // A stand-in that invents results must never run in production.
                if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
                {
                    throw new InvalidOperationException("WebSearch:Backend=fake is only allowed in Development.");
                }

                return new FakeWebSearchBackend();
            }

            return new GeminiWebSearchBackend(sp.GetRequiredService<IOptions<GeminiSettings>>(), sp.GetRequiredService<IOptions<WebSearchSettings>>());
        });
        services.AddSingleton<IWebSearchService>(sp =>
        {
            var search = sp.GetRequiredService<IOptions<WebSearchSettings>>().Value;
            if (!search.Enabled) return new UnavailableWebSearchService();
            return new WebSearchService(sp.GetRequiredService<IWebSearchBackend>(), TimeSpan.FromSeconds(Math.Max(5, search.TimeoutSeconds)));
        });
        services.AddScoped<ChatOrchestrator>();

        services.Configure<RunSettings>(configuration.GetSection("Runs"));
        services.AddSingleton<RunCancellationRegistry>();
        services.AddScoped<IRunStore, FirestoreRunStore>();
        services.AddScoped<ISettingsStore, FirestoreSettingsStore>();
        services.AddScoped<RunService>();

        return services;
    }

    private static FirebaseAuth CreateFirebaseAuth(IConfiguration configuration)
    {
        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.GetApplicationDefault(),
            ProjectId = ResolveFirebaseProjectId(configuration)
        });

        return FirebaseAuth.GetAuth(app);
    }

    private static string? ResolveFirebaseProjectId(IConfiguration configuration) =>
        Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
        ?? configuration["Firebase:ProjectId"]
        ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
        ?? Environment.GetEnvironmentVariable("PROJECT_ID");

    private static string ResolveFirestoreProjectId(IConfiguration configuration)
    {
        var projectId = Environment.GetEnvironmentVariable("FIRESTORE_PROJECT_ID")
            ?? configuration["Persistence:FirestoreProjectId"]
            ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
            ?? configuration["Firebase:ProjectId"]
            ?? Environment.GetEnvironmentVariable("PROJECT_ID")
            ?? configuration["VertexAI:ProjectId"];

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("Firestore project id is not configured.");
        }

        return projectId;
    }

    private static IReadOnlyList<OpenAICompatibleProviderSettings> LoadOpenAICompatibleProviders(
        IConfiguration configuration)
    {
        var settings = configuration.GetSection("OpenAICompatible").Get<OpenAICompatibleSettings>()
            ?? new OpenAICompatibleSettings();

        return OpenAICompatibleCatalog.CreateEnabledProviderSettings(settings);
    }

}
