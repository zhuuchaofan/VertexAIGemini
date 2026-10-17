using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace VertexAI.Configuration;

/// <summary>
/// The API's rate limits. ASP.NET applies a named endpoint policy on top of the global limiter and
/// <c>DisableRateLimiting()</c> removes every limit, so reading or stopping an existing run gets its own, larger,
/// bounded budget by being classified inside the global limiter itself.
/// </summary>
public static class RateLimitPolicies
{
    public const int DefaultPermitsPerMinute = 30;
    public const int RunControlPermitsPerMinute = 120;

    /// <summary>Reading a run and stopping a run. Creating a run is deliberately not control traffic.</summary>
    public static bool IsRunControl(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v2/runs", out var rest) || !rest.HasValue || rest.Value == "/")
            return false;

        if (HttpMethods.IsGet(context.Request.Method)) return true;
        return HttpMethods.IsPost(context.Request.Method)
            && rest.Value!.EndsWith("/stop", StringComparison.Ordinal);
    }

    public static RateLimitPartition<string> Partition(HttpContext context)
    {
        var caller = ResolveCaller(context);
        return IsRunControl(context)
            ? RateLimitPartition.GetFixedWindowLimiter("control:" + caller, _ => Window(RunControlPermitsPerMinute))
            : RateLimitPartition.GetFixedWindowLimiter(caller, _ => Window(DefaultPermitsPerMinute));
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    };

    internal static string ResolveCaller(HttpContext context)
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
}
