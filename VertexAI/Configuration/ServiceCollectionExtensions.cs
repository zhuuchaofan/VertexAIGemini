using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Services;
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
        services.Configure<WorkspaceSettings>(configuration.GetSection("Workspace"));
        services.Configure<OpenAICompatibleSettings>(configuration.GetSection("OpenAICompatible"));
        services.AddDatabase(configuration);
        services.AddApplicationServices(configuration);

        if (ShouldEnableLegacyBlazor(configuration))
        {
            services.AddBlazorExperience();
        }

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
        services.AddHttpClient<OpenAICompatibleChatModelClient>();

        services.AddScoped<GeminiService>();
        services.AddScoped<IChatModelProvider, GeminiProvider>();
        if (configuration.GetValue("OpenAICompatible:Enabled", false))
        {
            services.AddScoped<IChatModelProvider, OpenAICompatibleProvider>();
        }
        services.AddScoped<IChatProviderCatalog, ChatProviderCatalog>();
        services.AddScoped<IChatModelClient>(sp => sp.GetRequiredService<IChatProviderCatalog>().CreateClient("gemini"));
        services.AddScoped<AuthService>();
        services.AddSingleton<IAuthRateLimiter, AuthRateLimiter>();
        services.AddSingleton<IAuthTokenGenerator, AuthTokenGenerator>();
        services.AddScoped<IAuthCookieService, AuthCookieService>();
        services.AddScoped<IAuthSessionStore, AuthSessionStore>();
        services.AddScoped<AuthWorkflowService>();
        services.AddHostedService<SessionCleanupService>();
        services.AddScoped<ConversationService>();
        services.AddScoped<IConversationStore>(sp => sp.GetRequiredService<ConversationService>());
        services.AddScoped<ChatOrchestrator>();
        services.AddSingleton<MarkdownService>();
        services.AddScoped<ImageService>();

        services.AddSingleton(CreateSmtpSettings(configuration));
        services.AddSingleton<EmailService>();

        if (ShouldEnableLegacyBlazor(configuration))
        {
            services.AddScoped(sp =>
            {
                var navigationManager = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
                return new HttpClient { BaseAddress = new Uri(navigationManager.BaseUri) };
            });
        }

        return services;
    }

    private static IServiceCollection AddBlazorExperience(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 5 * 1024 * 1024;
        });

        return services;
    }

    private static bool ShouldEnableLegacyBlazor(IConfiguration configuration) =>
        configuration.GetValue("Workspace:EnableLegacyBlazor", true);

    private static SmtpSettings CreateSmtpSettings(IConfiguration configuration)
    {
        var smtpSettings = configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new SmtpSettings();

        smtpSettings.User = Environment.GetEnvironmentVariable("SMTP_USER") ?? smtpSettings.User;
        smtpSettings.Password = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? smtpSettings.Password;
        smtpSettings.BaseUrl = Environment.GetEnvironmentVariable("APP_BASE_URL") ?? smtpSettings.BaseUrl;

        return smtpSettings;
    }
}
