using VertexAI.Configuration;
using VertexAI.Services.Auth;

namespace VertexAI.Services.Quota;

/// <summary>One kind of daily allowance: what has been used and the most that may be.</summary>
public sealed record QuotaMeter(long Used, long Limit);

/// <summary>
/// What one person may still do today. Daily limits reset at midnight UTC, the day the quota documents are keyed by.
/// <see cref="Limited"/> is false for people the quota does not apply to (an admin, or quotas switched off).
/// </summary>
public sealed record QuotaStatus(
    bool Limited,
    string Date,
    DateTime ResetsAtUtc,
    QuotaMeter Requests,
    QuotaMeter Tokens,
    QuotaMeter Searches,
    QuotaMeter AttachmentBytes)
{
    public static QuotaStatus Unlimited(DateTime nowUtc) =>
        new(false, DateKey(nowUtc), NextReset(nowUtc), new(0, 0), new(0, 0), new(0, 0), new(0, 0));

    /// <summary>The status for a day's recorded usage (null: nothing used yet). Tokens count estimated and actual, as the check does.</summary>
    public static QuotaStatus From(QuotaUsageEntry? usage, QuotaSettings settings, DateTime nowUtc) =>
        new(
            true,
            DateKey(nowUtc),
            NextReset(nowUtc),
            new(usage?.Requests ?? 0, settings.DailyRequestLimit),
            new((long)(usage?.EstimatedTokens ?? 0) + (usage?.ActualTokens ?? 0), settings.DailyTokenLimit),
            new(usage?.Searches ?? 0, settings.DailySearchLimit),
            new(usage?.AttachmentBytes ?? 0, settings.DailyAttachmentBytesLimit));

    private static string DateKey(DateTime nowUtc) => nowUtc.ToString("yyyyMMdd");

    private static DateTime NextReset(DateTime nowUtc) =>
        DateTime.SpecifyKind(nowUtc.Date.AddDays(1), DateTimeKind.Utc);
}

/// <summary>Reads the signed-in person's own allowance for today.</summary>
public interface IQuotaStatusReader
{
    Task<QuotaStatus> GetAsync(AuthenticatedUser user, CancellationToken cancellationToken = default);
}
