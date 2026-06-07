using System.Collections.Concurrent;

namespace VertexAI.Services.Auth;

public interface IAuthRateLimiter
{
    bool IsLimited(HttpContext context);
    void RecordFailure(HttpContext context);
    void Reset(HttpContext context);
}

public sealed class AuthRateLimiter : IAuthRateLimiter
{
    private const int MaxAttemptsPerMinute = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, RateLimitEntry> _entries = new();

    public bool IsLimited(HttpContext context)
    {
        var clientIp = GetClientIp(context);

        if (!_entries.TryGetValue(clientIp, out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow - entry.WindowStart > Window)
        {
            _entries.TryRemove(clientIp, out _);
            return false;
        }

        return entry.Attempts >= MaxAttemptsPerMinute;
    }

    public void RecordFailure(HttpContext context)
    {
        var clientIp = GetClientIp(context);
        _entries.AddOrUpdate(
            clientIp,
            _ => new RateLimitEntry(1, DateTime.UtcNow),
            (_, existing) =>
            {
                if (DateTime.UtcNow - existing.WindowStart > Window)
                {
                    return new RateLimitEntry(1, DateTime.UtcNow);
                }

                return existing with { Attempts = existing.Attempts + 1 };
            });
    }

    public void Reset(HttpContext context)
    {
        _entries.TryRemove(GetClientIp(context), out _);
    }

    private static string GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private sealed record RateLimitEntry(int Attempts, DateTime WindowStart);
}
