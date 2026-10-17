using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Configuration;

public static class DevelopmentExtensions
{
    public const string FakeUserKey = "Dev:FakeUser";

    /// <summary>
    /// With <c>Dev:FakeUser=true</c>, replaces Firebase authentication with a header-based user and adds the mock
    /// provider. Turning it on anywhere but the Development environment stops the application from starting.
    /// </summary>
    public static IServiceCollection AddDevelopmentOverrides(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>(FakeUserKey))
        {
            return services;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{FakeUserKey} can only be enabled in the Development environment (current: {environment.EnvironmentName}).");
        }

        services.AddScoped<IUserContext, DevUserContext>();
        services.AddScoped<IChatModelProvider, MockChatModelProvider>();
        return services;
    }
}
