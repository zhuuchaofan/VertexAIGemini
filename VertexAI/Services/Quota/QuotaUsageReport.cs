namespace VertexAI.Services.Quota;

public sealed record QuotaUsageEntry(
    string UserId,
    string Date,
    int Requests,
    int EstimatedTokens,
    int ActualTokens,
    int Searches,
    long AttachmentBytes,
    DateTime? UpdatedAt);

public sealed record QuotaUsageTotals(
    int Requests,
    int EstimatedTokens,
    int ActualTokens,
    int Searches,
    long AttachmentBytes)
{
    public static QuotaUsageTotals From(IEnumerable<QuotaUsageEntry> entries) =>
        new(
            entries.Sum(entry => entry.Requests),
            entries.Sum(entry => entry.EstimatedTokens),
            entries.Sum(entry => entry.ActualTokens),
            entries.Sum(entry => entry.Searches),
            entries.Sum(entry => entry.AttachmentBytes));
}

public sealed record QuotaUsageReport(
    string Date,
    IReadOnlyList<QuotaUsageEntry> Entries,
    QuotaUsageTotals Totals);
