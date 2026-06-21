using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using System.Globalization;
using VertexAI.Configuration;
using VertexAI.Services.Auth;
using VertexAI.Services.Firestore;

namespace VertexAI.Services.Quota;

public sealed class FirestoreChatQuotaService : IChatQuotaService
{
    public const string CollectionName = "usageQuotas";
    public const string UserIdField = "userId";
    public const string DateField = "date";
    public const string Requests = "requests";
    public const string EstimatedTokens = "estimatedTokens";
    public const string ActualTokens = "actualTokens";
    public const string Searches = "searches";
    public const string AttachmentBytes = "attachmentBytes";
    public const string UpdatedAt = "updatedAt";

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
            transaction.Set(doc, next.ToDocument(request), SetOptions.MergeAll);
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
                [UserIdField] = ResolveUserId(request.User),
                [DateField] = TodayDateKey(),
                [ActualTokens] = FieldValue.Increment(actualTokens),
                [UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
            },
            SetOptions.MergeAll,
            cancellationToken);
    }

    private bool ShouldBypass(AuthenticatedUser user) =>
        !_settings.Enabled || (_settings.AdminBypassEnabled && user.IsAdmin);

    private DocumentReference GetDailyDocument(AuthenticatedUser user) =>
        _db.Collection(CollectionName).Document(DailyDocumentId(ResolveUserId(user), TodayDateKey()));

    public static string ResolveUserId(AuthenticatedUser user) =>
        FirestoreDocumentIds.UserDocumentId(user.LocalUserId, user.FirebaseUid);

    public static string DailyDocumentId(string userId, string date) =>
        $"{userId}_{NormalizeDateKey(date)}";

    public static string TodayDateKey() =>
        DateTime.UtcNow.ToString("yyyyMMdd");

    public static string NormalizeDateKey(string date)
    {
        var value = date.Trim();
        if (DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dashed))
        {
            return dashed.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        if (value.Length == 8 && value.All(char.IsDigit))
        {
            return value;
        }

        throw new ArgumentException("Date must be yyyyMMdd or yyyy-MM-dd.", nameof(date));
    }

    public static QuotaUsageEntry ToUsageEntry(DocumentSnapshot snapshot) =>
        new(
            ReadString(snapshot, UserIdField) ?? ResolveUserIdFromDocumentId(snapshot.Id),
            ReadString(snapshot, DateField) ?? ResolveDateFromDocumentId(snapshot.Id),
            ReadInt(snapshot, Requests),
            ReadInt(snapshot, EstimatedTokens),
            ReadInt(snapshot, ActualTokens),
            ReadInt(snapshot, Searches),
            ReadLong(snapshot, AttachmentBytes),
            ReadTimestamp(snapshot, UpdatedAt)?.ToDateTime());

    private static string ResolveUserIdFromDocumentId(string documentId)
    {
        var separator = documentId.LastIndexOf('_');
        return separator > 0 ? documentId[..separator] : documentId;
    }

    private static string ResolveDateFromDocumentId(string documentId)
    {
        var separator = documentId.LastIndexOf('_');
        return separator >= 0 && separator < documentId.Length - 1
            ? documentId[(separator + 1)..]
            : "";
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

        public Dictionary<string, object> ToDocument(ChatQuotaRequest request) => new()
        {
            [FirestoreChatQuotaService.UserIdField] = ResolveUserId(request.User),
            [FirestoreChatQuotaService.DateField] = TodayDateKey(),
            [FirestoreChatQuotaService.Requests] = Requests,
            [FirestoreChatQuotaService.EstimatedTokens] = EstimatedTokens,
            [FirestoreChatQuotaService.ActualTokens] = ActualTokens,
            [FirestoreChatQuotaService.Searches] = Searches,
            [FirestoreChatQuotaService.AttachmentBytes] = AttachmentBytes,
            [UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
        };

    }

    private static string? ReadString(DocumentSnapshot snapshot, string field) =>
        snapshot.TryGetValue<string>(field, out var value) ? value : null;

    private static Timestamp? ReadTimestamp(DocumentSnapshot snapshot, string field) =>
        snapshot.TryGetValue<Timestamp>(field, out var value) ? value : null;

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
