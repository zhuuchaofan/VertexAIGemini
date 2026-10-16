using VertexAI.Configuration;

namespace VertexAI.Services.Quota;

/// <summary>Running totals for one quota bucket (one user-day, or the whole site for a day).</summary>
internal readonly record struct UsageTotals(
    int Requests,
    int EstimatedTokens,
    int ActualTokens,
    int Searches,
    long AttachmentBytes)
{
    /// <summary>
    /// Tokens counted against the daily limit. EstimatedTokens holds only reservations that are still
    /// in flight (they are released on settlement), ActualTokens the settled usage, so one request is
    /// never counted twice.
    /// </summary>
    public long TokensInUse => Math.Max(0L, (long)EstimatedTokens + ActualTokens);

    public UsageTotals Reserve(ChatQuotaRequest request) => this with
    {
        Requests = Requests + request.RequestCount,
        EstimatedTokens = EstimatedTokens + request.EstimatedTokens,
        Searches = Searches + request.SearchCount,
        AttachmentBytes = AttachmentBytes + request.AttachmentBytes
    };
}

internal static class QuotaAccounting
{
    public static string? FindUserViolation(UsageTotals usage, QuotaSettings settings)
    {
        if (usage.Requests > settings.DailyRequestLimit)
        {
            return "Daily request quota exceeded.";
        }

        if (usage.TokensInUse > settings.DailyTokenLimit)
        {
            return "Daily token quota exceeded.";
        }

        if (usage.Searches > settings.DailySearchLimit)
        {
            return "Daily search quota exceeded.";
        }

        if (usage.AttachmentBytes > settings.DailyAttachmentBytesLimit)
        {
            return "Daily attachment quota exceeded.";
        }

        return null;
    }

    public static string? FindGlobalViolation(UsageTotals usage, QuotaSettings settings)
    {
        if (settings.GlobalDailyRequestLimit > 0 && usage.Requests > settings.GlobalDailyRequestLimit)
        {
            return "Global budget exceeded (requests).";
        }

        if (settings.GlobalDailyTokenLimit > 0 && usage.TokensInUse > settings.GlobalDailyTokenLimit)
        {
            return "Global budget exceeded (tokens).";
        }

        if (settings.GlobalDailySearchLimit > 0 && usage.Searches > settings.GlobalDailySearchLimit)
        {
            return "Global budget exceeded (searches).";
        }

        return null;
    }

    /// <summary>
    /// Settlement replaces the reservation by what was really used: release the estimate, add the
    /// actual tokens. A request that failed before reaching the model therefore costs no tokens.
    /// </summary>
    public static (long EstimatedDelta, long ActualDelta) SettleDeltas(int reservedEstimate, int actualTokens) =>
        (-(long)Math.Max(0, reservedEstimate), Math.Max(0, actualTokens));
}

/// <summary>
/// Server-side token estimate used only for the pre-flight reservation. It is deliberately cheap and
/// does not look at client-supplied sizes. Real usage replaces it at settlement.
/// </summary>
internal static class QuotaEstimator
{
    private const int TokensPerImage = 1500;
    private const int PdfBytesPerToken = 64;

    public static int EstimateTokens(string message, IReadOnlyCollection<Chat.ChatAttachment> attachments)
    {
        long tokens = Math.Max(1, (message ?? "").Length / 4);
        foreach (var attachment in attachments)
        {
            tokens += EstimateAttachmentTokens(attachment);
        }

        return (int)Math.Min(int.MaxValue, tokens);
    }

    public static long EstimateAttachmentBytes(Chat.ChatAttachment attachment) =>
        DecodedLength(attachment.Base64Data);

    private static long EstimateAttachmentTokens(Chat.ChatAttachment attachment)
    {
        var bytes = DecodedLength(attachment.Base64Data);
        var mime = attachment.MimeType ?? "";
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return TokensPerImage;
        }

        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1, bytes / PdfBytesPerToken);
        }

        return Math.Max(1, bytes / 4);
    }

    /// <summary>Decoded byte length derived from the Base64 text itself (never from a client field).</summary>
    private static long DecodedLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
            : base64.EndsWith('=') ? 1
            : 0;
        return Math.Max(0, base64.Length * 3L / 4L - padding);
    }
}
