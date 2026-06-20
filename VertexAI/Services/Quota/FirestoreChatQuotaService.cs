using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Services.Auth;
using VertexAI.Services.Firestore;

namespace VertexAI.Services.Quota;

public sealed class FirestoreChatQuotaService : IChatQuotaService
{
    private const string CollectionName = "usageQuotas";
    private const string Requests = "requests";
    private const string EstimatedTokens = "estimatedTokens";
    private const string ActualTokens = "actualTokens";
    private const string Searches = "searches";
    private const string AttachmentBytes = "attachmentBytes";
    private const string UpdatedAt = "updatedAt";

    private readonly FirestoreDb _db;
    private readonly QuotaSettings _settings;

    public FirestoreChatQuotaService(FirestoreDb db, IOptions<QuotaSettings> settings)
    {
        _db = db;
        _settings = settings.Value;
    }

    public async Task CheckAndReserveAsync(ChatQuotaRequest request, CancellationToken cancellationToken = default)
    {
        if (ShouldBypass(request.User))
        {
            return;
        }

        var doc = GetDailyDocument(request.User);
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(doc, cancellationToken);
            var usage = DailyUsage.From(snapshot);
            var next = usage.Add(request);
            Validate(next);
            transaction.Set(doc, next.ToDocument(), SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    public async Task RecordTokenUsageAsync(ChatQuotaRequest request, int actualTokens, CancellationToken cancellationToken = default)
    {
        if (ShouldBypass(request.User) || actualTokens <= 0)
        {
            return;
        }

        await GetDailyDocument(request.User).SetAsync(
            new Dictionary<string, object>
            {
                [ActualTokens] = FieldValue.Increment(actualTokens),
                [UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
            },
            SetOptions.MergeAll,
            cancellationToken);
    }

    private bool ShouldBypass(AuthenticatedUser user) =>
        !_settings.Enabled || (_settings.AdminBypassEnabled && user.IsAdmin);

    private DocumentReference GetDailyDocument(AuthenticatedUser user)
    {
        var uid = FirestoreDocumentIds.UserDocumentId(user.LocalUserId, user.FirebaseUid);
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        return _db.Collection(CollectionName).Document($"{uid}_{date}");
    }

    private void Validate(DailyUsage usage)
    {
        if (usage.Requests > _settings.DailyRequestLimit)
        {
            throw new InvalidOperationException("Daily request quota exceeded.");
        }

        if (usage.EstimatedTokens + usage.ActualTokens > _settings.DailyTokenLimit)
        {
            throw new InvalidOperationException("Daily token quota exceeded.");
        }

        if (usage.Searches > _settings.DailySearchLimit)
        {
            throw new InvalidOperationException("Daily search quota exceeded.");
        }

        if (usage.AttachmentBytes > _settings.DailyAttachmentBytesLimit)
        {
            throw new InvalidOperationException("Daily attachment quota exceeded.");
        }
    }

    private sealed record DailyUsage(
        int Requests,
        int EstimatedTokens,
        int ActualTokens,
        int Searches,
        long AttachmentBytes)
    {
        public static DailyUsage From(DocumentSnapshot snapshot) =>
            snapshot.Exists
                ? new DailyUsage(
                    ReadInt(snapshot, FirestoreChatQuotaService.Requests),
                    ReadInt(snapshot, FirestoreChatQuotaService.EstimatedTokens),
                    ReadInt(snapshot, FirestoreChatQuotaService.ActualTokens),
                    ReadInt(snapshot, FirestoreChatQuotaService.Searches),
                    ReadLong(snapshot, FirestoreChatQuotaService.AttachmentBytes))
                : new DailyUsage(0, 0, 0, 0, 0);

        public DailyUsage Add(ChatQuotaRequest request) =>
            this with
            {
                Requests = Requests + request.RequestCount,
                EstimatedTokens = EstimatedTokens + request.EstimatedTokens,
                Searches = Searches + request.SearchCount,
                AttachmentBytes = AttachmentBytes + request.AttachmentBytes
            };

        public Dictionary<string, object> ToDocument() => new()
        {
            [FirestoreChatQuotaService.Requests] = Requests,
            [FirestoreChatQuotaService.EstimatedTokens] = EstimatedTokens,
            [FirestoreChatQuotaService.ActualTokens] = ActualTokens,
            [FirestoreChatQuotaService.Searches] = Searches,
            [FirestoreChatQuotaService.AttachmentBytes] = AttachmentBytes,
            [UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        private static int ReadInt(DocumentSnapshot snapshot, string field) =>
            snapshot.TryGetValue<long>(field, out var longValue)
                ? checked((int)longValue)
                : snapshot.TryGetValue<int>(field, out var intValue)
                    ? intValue
                    : 0;

        private static long ReadLong(DocumentSnapshot snapshot, string field) =>
            snapshot.TryGetValue<long>(field, out var longValue)
                ? longValue
                : snapshot.TryGetValue<int>(field, out var intValue)
                    ? intValue
                    : 0;
    }
}
