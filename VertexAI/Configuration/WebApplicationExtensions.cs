using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VertexAI.Api;
using VertexAI.Data;

namespace VertexAI.Configuration;

public static class WebApplicationExtensions
{
    public static async Task InitializeVertexApplicationAsync(this WebApplication app)
    {
        await app.Services.InitializeDatabaseAsync();
    }

    public static WebApplication UseVertexPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();

            if (ShouldUseHttpsRedirection(app.Configuration))
            {
                app.UseHttpsRedirection();
            }
        }

        app.MapHealthChecks("/health/live", new()
        {
            Predicate = _ => false
        });
        app.MapHealthChecks("/health/ready", new()
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        app.MapAuthEndpoints();
        app.MapChatEndpoints();
        app.MapConversationEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapExportEndpoints();

        return app;
    }

    private static bool ShouldUseHttpsRedirection(IConfiguration configuration)
    {
        var httpsPort = configuration["HTTPS_PORT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT");

        return !string.IsNullOrWhiteSpace(httpsPort);
    }
}
