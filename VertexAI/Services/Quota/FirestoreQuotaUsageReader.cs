using Google.Cloud.Firestore;

namespace VertexAI.Services.Quota;

public sealed class FirestoreQuotaUsageReader : IQuotaUsageReader
{
    private readonly FirestoreDb _db;

    public FirestoreQuotaUsageReader(FirestoreDb db)
    {
        _db = db;
    }

    public async Task<QuotaUsageReport> GetDailyUsageAsync(
        string date,
        int limit,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedDate = FirestoreChatQuotaService.NormalizeDateKey(date);
        var entries = string.IsNullOrWhiteSpace(userId)
            ? await QueryDailyUsageAsync(normalizedDate, cancellationToken)
            : await GetUserDailyUsageAsync(userId, normalizedDate, cancellationToken);

        var page = entries
            .OrderByDescending(entry => entry.UpdatedAt ?? DateTime.MinValue)
            .ThenBy(entry => entry.UserId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return new QuotaUsageReport(
            normalizedDate,
            page,
            QuotaUsageTotals.From(entries));
    }

    private async Task<IReadOnlyList<QuotaUsageEntry>> QueryDailyUsageAsync(
        string date,
        CancellationToken cancellationToken)
    {
        var snapshot = await _db.Collection(FirestoreChatQuotaService.CollectionName)
            .WhereEqualTo(FirestoreChatQuotaService.DateField, date)
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Where(document => document.Exists)
            .Select(FirestoreChatQuotaService.ToUsageEntry)
            .ToList();
    }

    private async Task<IReadOnlyList<QuotaUsageEntry>> GetUserDailyUsageAsync(
        string userId,
        string date,
        CancellationToken cancellationToken)
    {
        var snapshot = await _db.Collection(FirestoreChatQuotaService.CollectionName)
            .Document(FirestoreChatQuotaService.DailyDocumentId(userId, date))
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Exists
            ? [FirestoreChatQuotaService.ToUsageEntry(snapshot)]
            : [];
    }
}
