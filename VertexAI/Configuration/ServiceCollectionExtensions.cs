using Microsoft.EntityFrameworkCore;
using Google.Cloud.Firestore;
using VertexAI.Data;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Health;
using VertexAI.Services.UserSettings;

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
        services.AddDatabase(configuration);
        services.AddApplicationServices(configuration);

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        return services;
    }

    private static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? configuration.GetConnectionString("Default");

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
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
        services.AddSingleton<IAuthRateLimiter, AuthRateLimiter>();
        services.AddSingleton<IAuthTokenGenerator, AuthTokenGenerator>();
        services.AddScoped<IAuthCookieService, AuthCookieService>();
        services.AddScoped<SessionUserContext>();
        services.AddScoped<IUserContext, FirebaseUserContext>();
        services.AddScoped<IAuthSessionStore, AuthSessionStore>();
        services.AddScoped<AuthWorkflowService>();
        services.AddHostedService<SessionCleanupService>();
        services.AddScoped<ConversationService>();
        services.AddSingleton(sp => FirestoreDb.Create(ResolveFirestoreProjectId(configuration)));
        services.AddScoped<FirestoreConversationStore>();
        services.AddScoped<IConversationStore>(sp =>
            UseFirestoreConversations(configuration)
                ? sp.GetRequiredService<FirestoreConversationStore>()
                : sp.GetRequiredService<ConversationService>());
        services.AddScoped<PostgresUserSettingsStore>();
        services.AddScoped<FirestoreUserSettingsStore>();
        services.AddScoped<IUserSettingsStore>(sp =>
            UseFirestoreUserSettings(configuration)
                ? sp.GetRequiredService<FirestoreUserSettingsStore>()
                : sp.GetRequiredService<PostgresUserSettingsStore>());
        services.AddScoped<ChatOrchestrator>();

        services.AddSingleton(CreateSmtpSettings(configuration));
        services.AddSingleton<EmailService>();

        return services;
    }

    private static bool UseFirestoreUserSettings(IConfiguration configuration) =>
        string.Equals(
            Environment.GetEnvironmentVariable("USER_SETTINGS_PROVIDER")
                ?? configuration["Persistence:UserSettingsProvider"],
            "firestore",
            StringComparison.OrdinalIgnoreCase);

    private static bool UseFirestoreConversations(IConfiguration configuration) =>
        string.Equals(
            Environment.GetEnvironmentVariable("CONVERSATION_PROVIDER")
                ?? configuration["Persistence:ConversationProvider"],
            "firestore",
            StringComparison.OrdinalIgnoreCase);

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

    private static SmtpSettings CreateSmtpSettings(IConfiguration configuration)
    {
        var smtpSettings = configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new SmtpSettings();

        smtpSettings.User = Environment.GetEnvironmentVariable("SMTP_USER") ?? smtpSettings.User;
        smtpSettings.Password = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? smtpSettings.Password;
        smtpSettings.BaseUrl = Environment.GetEnvironmentVariable("APP_BASE_URL") ?? smtpSettings.BaseUrl;

        return smtpSettings;
    }
}
