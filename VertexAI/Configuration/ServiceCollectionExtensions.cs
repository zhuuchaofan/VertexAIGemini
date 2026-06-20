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
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ResolveRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });

        return services;
    }

    private static string ResolveRateLimitPartitionKey(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization));
            return "auth:" + Convert.ToHexString(hash.AsSpan(0, 16));
        }

        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ip = !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.Split(',')[0].Trim()
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return "ip:" + ip;
    }

    private static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddHttpClient(nameof(OpenAICompatibleChatModelClient));

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
        services.AddSingleton(CreateFirebaseAuth(configuration));
        services.AddScoped<IUserContext, FirebaseUserContext>();
        services.AddSingleton(sp => FirestoreDb.Create(ResolveFirestoreProjectId(configuration)));
        services.AddSingleton(StorageClient.Create());
        services.AddSingleton<IChatAttachmentStore>(sp =>
        {
            var store = ActivatorUtilities.CreateInstance<CloudStorageChatAttachmentStore>(sp);
            return store.IsEnabled ? store : new NoOpChatAttachmentStore();
        });
        services.AddScoped<FirestoreConversationStore>();
        services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<FirestoreConversationStore>());
        services.AddScoped<IChatRequestAugmenter, WebSearchInstructionAugmenter>();
        services.AddScoped<ChatOrchestrator>();

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
