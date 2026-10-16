using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using System.Globalization;
using VertexAI.Configuration;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
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

    /// <summary>Site-wide totals live in their own collection so per-user reports are unaffected.</summary>
    public const string GlobalCollectionName = "usageQuotaGlobal";
    public const string GlobalUserId = "_global";

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

        var dateKey = request.DateKey ?? TodayDateKey();
        var userDoc = GetDailyDocument(request.User, dateKey);
        var globalDoc = GetGlobalDocument(dateKey);
        try
        {
            await ReserveAsync(request, dateKey, userDoc, globalDoc, cancellationToken);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode is
            Grpc.Core.StatusCode.Aborted or Grpc.Core.StatusCode.DeadlineExceeded or
            Grpc.Core.StatusCode.ResourceExhausted or Grpc.Core.StatusCode.Unavailable)
        {
            // Contention on the shared daily counter outlasted every retry. Fail closed (the request is
            // not admitted) but say what happened instead of a generic outage message.
            throw new UserFacingChatException("当前请求较多，请稍后重试。");
        }
    }

    private async Task ReserveAsync(
        ChatQuotaRequest request,
        string dateKey,
        DocumentReference userDoc,
        DocumentReference globalDoc,
        CancellationToken cancellationToken)
    {
        await _db.RunTransactionAsync(async transaction =>
        {
            // Firestore transactions require every read before any write.
            var userSnapshot = await transaction.GetSnapshotAsync(userDoc, cancellationToken);
            var globalSnapshot = _settings.GlobalLimitsEnabled
                ? await transaction.GetSnapshotAsync(globalDoc, cancellationToken)
                : null;

            var nextUser = ReadTotals(userSnapshot).Reserve(request);
            var violation = QuotaAccounting.FindUserViolation(nextUser, _settings);
            if (violation is not null)
            {
                throw new InvalidOperationException(violation);
            }

            transaction.Set(userDoc, ToDocument(nextUser, ResolveUserId(request.User), dateKey), SetOptions.MergeAll);

            if (globalSnapshot is not null)
            {
                var nextGlobal = ReadTotals(globalSnapshot).Reserve(request);
                var globalViolation = QuotaAccounting.FindGlobalViolation(nextGlobal, _settings);
                if (globalViolation is not null)
                {
                    throw new InvalidOperationException(globalViolation);
                }

                transaction.Set(globalDoc, ToDocument(nextGlobal, GlobalUserId, dateKey), SetOptions.MergeAll);
            }
        }, TransactionOptions.ForMaxAttempts(Math.Max(1, _settings.TransactionMaxAttempts)), cancellationToken);
    }

    /// <summary>
    /// Settles one reservation: releases the estimated tokens and records the actual usage, atomically
    /// for the user bucket and the site bucket. Must be called exactly once per successful reservation.
    /// </summary>
    public async Task RecordTokenUsageAsync(ChatQuotaRequest request, int actualTokens, CancellationToken cancellationToken = default)
    {
        if (ShouldBypass(request.User))
        {
            return;
        }

        // Settle against the day the reservation was made, even if the request ran past UTC midnight.
        var dateKey = request.DateKey ?? TodayDateKey();
        var (estimatedDelta, actualDelta) = QuotaAccounting.SettleDeltas(request.EstimatedTokens, actualTokens);
        var batch = _db.StartBatch();
        batch.Set(
            GetDailyDocument(request.User, dateKey),
            SettlementDocument(ResolveUserId(request.User), dateKey, estimatedDelta, actualDelta),
            SetOptions.MergeAll);

        if (_settings.GlobalLimitsEnabled)
        {
            batch.Set(
                GetGlobalDocument(dateKey),
                SettlementDocument(GlobalUserId, dateKey, estimatedDelta, actualDelta),
                SetOptions.MergeAll);
        }

        await batch.CommitAsync(cancellationToken);
    }

    private bool ShouldBypass(AuthenticatedUser user) =>
        !_settings.Enabled || (_settings.AdminBypassEnabled && user.IsAdmin);

    private DocumentReference GetDailyDocument(AuthenticatedUser user, string dateKey) =>
        _db.Collection(CollectionName).Document(DailyDocumentId(ResolveUserId(user), dateKey));

    private DocumentReference GetGlobalDocument(string dateKey) =>
        _db.Collection(GlobalCollectionName).Document(NormalizeDateKey(dateKey));

    private static Dictionary<string, object> SettlementDocument(string userId, string dateKey, long estimatedDelta, long actualDelta) => new()
    {
        [UserIdField] = userId,
        [DateField] = dateKey,
        [EstimatedTokens] = FieldValue.Increment(estimatedDelta),
        [ActualTokens] = FieldValue.Increment(actualDelta),
        [UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
    };

    private static UsageTotals ReadTotals(DocumentSnapshot snapshot) =>
        snapshot.Exists
            ? new UsageTotals(
                ReadInt(snapshot, Requests),
                ReadInt(snapshot, EstimatedTokens),
                ReadInt(snapshot, ActualTokens),
                ReadInt(snapshot, Searches),
                ReadLong(snapshot, AttachmentBytes))
            : default;

    private static Dictionary<string, object> ToDocument(UsageTotals usage, string userId, string dateKey) => new()
    {
        [UserIdField] = userId,
        [DateField] = dateKey,
        [Requests] = usage.Requests,
        [EstimatedTokens] = usage.EstimatedTokens,
        [ActualTokens] = usage.ActualTokens,
        [Searches] = usage.Searches,
        [AttachmentBytes] = usage.AttachmentBytes,
        [UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
    };

    public static string ResolveUserId(AuthenticatedUser user) =>
        FirestoreDocumentIds.UserDocumentId(user.LocalUserId, user.FirebaseUid);

    public static string DailyDocumentId(string userId, string date) =>
        $"{userId}_{NormalizeDateKey(date)}";

    public static string TodayDateKey() => QuotaAccounting.TodayKey();

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
