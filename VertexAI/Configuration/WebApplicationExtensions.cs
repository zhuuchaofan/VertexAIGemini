using Microsoft.AspNetCore.HttpOverrides;
using VertexAI.Api;

namespace VertexAI.Configuration;

public static class WebApplicationExtensions
{
    public static WebApplication UseVertexPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
        app.UseRateLimiter();

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            // There is no "/Error" endpoint; answer unexpected failures with a plain JSON 500 instead of
            // a second exception from the handler itself.
            app.UseExceptionHandler(errorApp => errorApp.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return context.Response.WriteAsJsonAsync(new { error = "服务器内部错误，请稍后重试" });
            }));
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

        app.UseMiddleware<AccessPolicyMiddleware>();

        app.MapAuthEndpoints();
        app.MapChatEndpoints();
        app.MapConversationEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapExportEndpoints();
        app.MapAdminEndpoints();

        return app;
    }

    private static bool ShouldUseHttpsRedirection(IConfiguration configuration)
    {
        var httpsPort = configuration["HTTPS_PORT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT");

        return !string.IsNullOrWhiteSpace(httpsPort);
    }
}
