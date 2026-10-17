using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Measured behavior of the real ASP.NET Core rate limiter middleware:
///  - a named endpoint policy is applied IN ADDITION to the GlobalLimiter, so it cannot relax the global
///    30 requests/minute;
///  - DisableRateLimiting() removes every limit including the global one, which would leave polling unbounded.
/// Run polling and stop therefore get their own, larger, bounded budget by being classified inside the
/// global limiter itself.
/// </summary>
internal static class RateLimitStackingTests
{
    private const int GlobalPermits = 3;
    private const int ControlPermits = 10;

    private static async Task<List<HttpStatusCode>> Hit(string baseUrl, string path, int times)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < times; i++) codes.Add((await http.GetAsync(path)).StatusCode);
        return codes;
    }

    private static WebApplication Host(Action<RateLimiterOptions> configure, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRateLimiter(configure);
        var app = builder.Build();
        app.UseRateLimiter();
        map(app);
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
    };

    public static void NamedEndpointPolicyDoesNotRelaxTheGlobalLimiter()
    {
        using var scope = new HostScope(Host(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                _ => RateLimitPartition.GetFixedWindowLimiter("everyone", _ => Window(GlobalPermits)));
            options.AddFixedWindowLimiter("runs-control", o => { o.PermitLimit = ControlPermits; o.Window = TimeSpan.FromMinutes(1); });
        }, app => app.MapGet("/poll", () => "ok").RequireRateLimiting("runs-control")));

        var codes = Hit(scope.Url, "/poll", ControlPermits).GetAwaiter().GetResult();
        // The named policy would allow 10, but the global limiter still stops the 4th request.
        Assert.Equal(GlobalPermits, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(ControlPermits - GlobalPermits, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    public static void DisableRateLimitingRemovesEveryLimitIncludingTheGlobalOne()
    {
        using var scope = new HostScope(Host(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                _ => RateLimitPartition.GetFixedWindowLimiter("everyone", _ => Window(GlobalPermits)));
        }, app => app.MapGet("/poll", () => "ok").DisableRateLimiting()));

        var codes = Hit(scope.Url, "/poll", GlobalPermits + 2).GetAwaiter().GetResult();
        // Unbounded: this is why run-control routes are NOT exempted this way.
        Assert.Equal(GlobalPermits + 2, codes.Count(c => c == HttpStatusCode.OK));
    }

    public static void ClassifyingInsideTheGlobalLimiterGivesControlRoutesTheirOwnBudget()
    {
        using var scope = new HostScope(Host(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsRunControl(context)
                    ? RateLimitPartition.GetFixedWindowLimiter("control", _ => Window(ControlPermits))
                    : RateLimitPartition.GetFixedWindowLimiter("default", _ => Window(GlobalPermits)));
        }, app =>
        {
            app.MapGet("/api/v2/runs/{id}", (string id) => "snapshot");
            app.MapPost("/api/v2/runs", () => "created");
            app.MapGet("/api/conversations", () => "list");
        }));

        // Polling gets the larger budget...
        var poll = Hit(scope.Url, "/api/v2/runs/abc", ControlPermits).GetAwaiter().GetResult();
        Assert.Equal(ControlPermits, poll.Count(c => c == HttpStatusCode.OK));
        // ...and exhausting it does not touch the default budget used by everything else.
        var other = Hit(scope.Url, "/api/conversations", GlobalPermits + 1).GetAwaiter().GetResult();
        Assert.Equal(GlobalPermits, other.Count(c => c == HttpStatusCode.OK));
        // Creating a run is NOT a control route: it keeps the original limit.
        using var http = new HttpClient { BaseAddress = new Uri(scope.Url) };
        var creates = new List<HttpStatusCode>();
        for (var i = 0; i < GlobalPermits + 1; i++) creates.Add(http.PostAsync("/api/v2/runs", null).GetAwaiter().GetResult().StatusCode);
        // "default" is shared with /api/conversations above, so it is already spent.
        Assert.Equal(0, creates.Count(c => c == HttpStatusCode.OK));
    }

    /// <summary>The classification the production limiter will use: read/stop of an existing run only.</summary>
    private static bool IsRunControl(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v2/runs", out var rest) || !rest.HasValue || rest.Value == "/") return false;
        if (HttpMethods.IsGet(context.Request.Method)) return true;
        return HttpMethods.IsPost(context.Request.Method) && rest.Value!.EndsWith("/stop", StringComparison.Ordinal);
    }

    private sealed class HostScope(WebApplication app) : IDisposable
    {
        public string Url { get; } = app.Urls.First();
        public void Dispose() => app.StopAsync().GetAwaiter().GetResult();
    }
}
