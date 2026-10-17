using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Firestore;
using VertexAI.Services.Quota;

namespace VertexAI.Services.Runs;

public sealed class FirestoreRunStore : IRunStore
{
    public const string RunsCollection = "runs";

    private readonly FirestoreDb _db;
    private readonly FirestoreChatQuotaService _quota;
    private readonly RunSettings _settings;

    public FirestoreRunStore(FirestoreDb db, FirestoreChatQuotaService quota, IOptions<RunSettings> settings)
    {
        _db = db;
        _quota = quota;
        _settings = settings.Value;
    }

    private CollectionReference Runs => _db.Collection(RunsCollection);
    private CollectionReference Conversations => _db.Collection(FirestoreConversationSchema.ConversationsCollection);
    private CollectionReference Messages => _db.Collection(FirestoreConversationSchema.MessagesCollection);

    private static string Uid(AuthenticatedUser user) => FirestoreConversationSchema.UserDocumentId(user);

    // ------------------------------------------------------------------------------------------------ admission

    public async Task<AdmitResult> AdmitAsync(AdmitRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var decision = await _db.RunTransactionAsync(
                transaction => DecideAsync(transaction, request, cancellationToken),
                cancellationToken: cancellationToken);
            return decision;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The transaction can fail (lock timeout, aborted retries, deadline) without telling us whether it
            // committed. "Failed" is never "lost": read the run back and see whose executor id it carries.
            return await ReadBackAsync(request, ex);
        }
    }

    private async Task<AdmitResult> DecideAsync(Transaction transaction, AdmitRequest request, CancellationToken ct)
    {
        // The callback can be re-run by the SDK on contention, so it only reads and buffers writes.
        var uid = Uid(request.User);
        var runRef = Runs.Document(request.RunId);
        var conversationRef = Conversations.Document(request.ConversationId.ToString("D"));
        var usageRef = _quota.DailyDocumentFor(request.User);

        var runSnapshot = await transaction.GetSnapshotAsync(runRef, ct);
        if (runSnapshot.Exists)
        {
            return ClassifyExisting(runSnapshot, request, uid);
        }

        var conversationSnapshot = await transaction.GetSnapshotAsync(conversationRef, ct);
        if (conversationSnapshot.Exists && !FirestoreConversationSchema.OwnsConversation(conversationSnapshot, request.User))
        {
            return new AdmitResult.Conflict("conflict");
        }

        if (conversationSnapshot.Exists
            && conversationSnapshot.TryGetValue<string>(FirestoreConversationSchema.ActiveRunId, out var activeRunId)
            && !string.IsNullOrWhiteSpace(activeRunId))
        {
            var activeSnapshot = await transaction.GetSnapshotAsync(Runs.Document(activeRunId), ct);
            if (activeSnapshot.Exists)
            {
                var active = ToRecord(activeSnapshot);
                if (active.Status == RunStatus.Running)
                {
                    return IsStale(active)
                        ? new AdmitResult.StaleActive(activeRunId)
                        : new AdmitResult.Busy(activeRunId);
                }
            }
        }

        var usageSnapshot = await transaction.GetSnapshotAsync(usageRef, ct);
        var quota = _quota.Evaluate(usageSnapshot, request.Quota);

        // ---- all reads are done; everything below only buffers writes ----
        var now = DateTime.UtcNow;
        var userMessageId = RunIds.UserMessageId(request.RunId);
        var assistantMessageId = RunIds.AssistantMessageId(request.RunId);
        var admitted = quota.Allowed;

        var runDocument = new Dictionary<string, object>
        {
            [Fields.Uid] = uid,
            [Fields.LocalUserId] = request.User.LocalUserId.ToString("D"),
            [Fields.ConversationId] = request.ConversationId.ToString("D"),
            [Fields.UserMessageId] = userMessageId.ToString("D"),
            [Fields.AssistantMessageId] = assistantMessageId.ToString("D"),
            [Fields.Attempt] = 1,
            [Fields.Trigger] = "send",
            [Fields.Status] = (admitted ? RunStatus.Running : RunStatus.Failed).ToWire(),
            [Fields.Fingerprint] = request.Fingerprint,
            [Fields.ProviderCall] = "not_started",
            [Fields.Requested] = RequestedToMap(request.Requested),
            [Fields.CreatedAt] = FieldValue.ServerTimestamp,
            [Fields.UpdatedAt] = FieldValue.ServerTimestamp,
            [Fields.OutputChars] = 0,
            [Fields.HasAssistantMessage] = false,
            [Fields.SchemaVersion] = 1
        };

        if (admitted)
        {
            runDocument[Fields.LeaseOwner] = request.ExecutorId;
            runDocument[Fields.AdmittedAt] = FieldValue.ServerTimestamp;
            runDocument[Fields.HeartbeatAt] = FieldValue.ServerTimestamp;
            if (quota.Reserved) runDocument[Fields.QuotaReservation] = QuotaToMap(request.Quota);
        }
        else
        {
            runDocument[Fields.StatusReason] = RunReasons.Quota;
            runDocument[Fields.EndedAt] = FieldValue.ServerTimestamp;
            runDocument[Fields.Error] = ErrorToMap(new RunError(RunReasons.Quota, quota.Error ?? "Quota exceeded.", false));
        }

        transaction.Create(runRef, runDocument);

        var userMessage = new Message
        {
            Id = userMessageId,
            ConversationId = request.ConversationId,
            Role = "user",
            Content = request.Message,
            CreatedAt = now,
            RunId = request.RunId
        };
        transaction.Create(
            Messages.Document(userMessageId.ToString("D")),
            FirestoreConversationMapper.ToDocument(userMessage, uid, request.User.LocalUserId));

        if (conversationSnapshot.Exists)
        {
            var update = new Dictionary<string, object>
            {
                [FirestoreConversationSchema.LastRunId] = request.RunId,
                [FirestoreConversationSchema.UpdatedAt] = Timestamp.FromDateTime(now)
            };
            if (admitted) update[FirestoreConversationSchema.ActiveRunId] = request.RunId;
            if (!conversationSnapshot.TryGetValue<string>(FirestoreConversationSchema.Title, out var title)
                || string.IsNullOrWhiteSpace(title))
            {
                update[FirestoreConversationSchema.Title] = CreateTitle(request.Message);
            }

            transaction.Update(conversationRef, update);
        }
        else
        {
            var conversation = new Conversation
            {
                Id = request.ConversationId,
                UserId = request.User.LocalUserId,
                Title = CreateTitle(request.Message),
                ProviderId = request.Requested.ProviderId,
                ModelName = request.ModelName,
                PresetId = request.PresetId,
                CustomPrompt = request.CustomPrompt,
                CreatedAt = now,
                UpdatedAt = now
            };
            var document = FirestoreConversationMapper.ToDocument(conversation, uid);
            document[FirestoreConversationSchema.LastRunId] = request.RunId;
            if (admitted) document[FirestoreConversationSchema.ActiveRunId] = request.RunId;
            transaction.Create(conversationRef, document);
        }

        if (admitted && quota.Reservation != null)
        {
            transaction.Set(usageRef, quota.Reservation, SetOptions.MergeAll);
        }

        var record = new RunRecord
        {
            RunId = request.RunId,
            Uid = uid,
            ConversationId = request.ConversationId,
            UserMessageId = userMessageId,
            AssistantMessageId = assistantMessageId,
            Status = admitted ? RunStatus.Running : RunStatus.Failed,
            StatusReason = admitted ? null : RunReasons.Quota,
            Requested = request.Requested,
            CreatedAt = now,
            Fingerprint = request.Fingerprint,
            LeaseOwner = admitted ? request.ExecutorId : null,
            AdmittedAt = admitted ? now : null,
            HeartbeatAt = admitted ? now : null,
            EndedAt = admitted ? null : now,
            Error = admitted ? null : new RunError(RunReasons.Quota, quota.Error ?? "Quota exceeded.", false),
            QuotaReservation = admitted && quota.Reserved ? request.Quota : null
        };

        return admitted
            ? new AdmitResult.Admitted(new AdmittedRun(request.User, record, request.ExecutorId))
            : new AdmitResult.Rejected(record);
    }

    private AdmitResult ClassifyExisting(DocumentSnapshot snapshot, AdmitRequest request, string uid)
    {
        var existing = ToRecord(snapshot);
        if (existing.Uid != uid)
        {
            return new AdmitResult.Conflict("conflict");
        }

        // Same executor id means an earlier attempt of THIS request committed and only the answer was lost.
        if (existing.LeaseOwner == request.ExecutorId)
        {
            return new AdmitResult.Admitted(new AdmittedRun(request.User, existing, request.ExecutorId));
        }

        return existing.Fingerprint == request.Fingerprint
            ? new AdmitResult.Replay(existing)
            : new AdmitResult.Conflict("idempotency_mismatch");
    }

    private async Task<AdmitResult> ReadBackAsync(AdmitRequest request, Exception cause)
    {
        DocumentSnapshot snapshot;
        try
        {
            snapshot = await Runs.Document(request.RunId).GetSnapshotAsync();
        }
        catch (Exception readFailure)
        {
            throw new RunAdmissionUnavailableException(
                $"Could not determine whether the run was admitted (transaction: {cause.GetType().Name}: {Short(cause)}; read-back: {readFailure.GetType().Name}: {Short(readFailure)}).",
                readFailure);
        }

        if (!snapshot.Exists)
        {
            throw new RunAdmissionUnavailableException($"The run was not admitted; retry with the same run id (transaction: {cause.GetType().Name}: {Short(cause)}).", cause);
        }

        return ClassifyExisting(snapshot, request, Uid(request.User));
    }

    private static string Short(Exception exception)
    {
        var text = exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
            ? aggregate.InnerExceptions[0].Message
            : exception.Message;
        return text.Length > 160 ? text[..160] : text;
    }

    // ------------------------------------------------------------------------------------------------ lease

    public async Task<bool> BeginProviderCallAsync(AdmittedRun run, CancellationToken cancellationToken, RunEffective? effective = null)
    {
        var runRef = Runs.Document(run.RunId);
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(runRef, cancellationToken);
            if (!snapshot.Exists) return false;
            var record = ToRecord(snapshot);
            if (record.Status != RunStatus.Running
                || record.LeaseOwner != run.ExecutorId
                || record.ProviderCallStarted)
            {
                return false;
            }

            var update = new Dictionary<string, object>
            {
                [Fields.ProviderCall] = "started",
                [Fields.ProviderCallStartedAt] = FieldValue.ServerTimestamp,
                [Fields.UpdatedAt] = FieldValue.ServerTimestamp
            };
            if (effective != null) update[Fields.Effective] = EffectiveToMap(effective);
            transaction.Update(runRef, update);
            return true;
        }, cancellationToken: cancellationToken);
    }

    public async Task<RunLeaseState> PollAsync(AdmittedRun run, CancellationToken cancellationToken)
    {
        var snapshot = await Runs.Document(run.RunId).GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists) return new RunLeaseState(RunStatus.Interrupted, false, false);
        var record = ToRecord(snapshot);
        return new RunLeaseState(
            record.Status,
            record.Status == RunStatus.Running && record.LeaseOwner == run.ExecutorId,
            record.StopRequestedAt != null);
    }

    public async Task<bool> HeartbeatAsync(AdmittedRun run, RunCheckpoint? checkpoint, CancellationToken cancellationToken)
    {
        var runRef = Runs.Document(run.RunId);
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(runRef, cancellationToken);
            if (!snapshot.Exists) return false;
            var record = ToRecord(snapshot);
            // A heartbeat never touches a terminal run, and never from anyone but the lease holder.
            if (record.Status != RunStatus.Running || record.LeaseOwner != run.ExecutorId) return false;

            var update = new Dictionary<string, object>
            {
                [Fields.HeartbeatAt] = FieldValue.ServerTimestamp,
                [Fields.UpdatedAt] = FieldValue.ServerTimestamp
            };
            if (checkpoint != null)
            {
                update[Fields.PartialContent] = Truncate(checkpoint.Content);
                if (!string.IsNullOrEmpty(checkpoint.Thinking)) update[Fields.PartialThinking] = Truncate(checkpoint.Thinking);
            }

            transaction.Update(runRef, update);
            return true;
        }, cancellationToken: cancellationToken);
    }

    public async Task<RunRecord?> RequestStopAsync(AuthenticatedUser user, string runId, CancellationToken cancellationToken)
    {
        var runRef = Runs.Document(runId);
        var uid = Uid(user);
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(runRef, cancellationToken);
            if (!snapshot.Exists) return null;
            var record = ToRecord(snapshot);
            if (record.Uid != uid) return null;
            if (record.Status == RunStatus.Running && record.StopRequestedAt == null)
            {
                transaction.Update(runRef, new Dictionary<string, object>
                {
                    [Fields.StopRequestedAt] = FieldValue.ServerTimestamp,
                    [Fields.UpdatedAt] = FieldValue.ServerTimestamp
                });
                return record with { StopRequestedAt = DateTime.UtcNow };
            }

            return record;
        }, cancellationToken: cancellationToken);
    }

    public async Task<RunRecord?> GetAsync(AuthenticatedUser user, string runId, CancellationToken cancellationToken)
    {
        var snapshot = await Runs.Document(runId).GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists) return null;
        var record = ToRecord(snapshot);
        return record.Uid == Uid(user) ? record : null;
    }

    // ------------------------------------------------------------------------------------------------ finalize

    public async Task<FinalizeResult> FinalizeAsync(FinalizeRequest request, CancellationToken cancellationToken)
    {
        var runRef = Runs.Document(request.RunId);
        var uid = Uid(request.User);
        var usageRef = _quota.DailyDocumentFor(request.User);

        return await _db.RunTransactionAsync(async transaction =>
        {
            var runSnapshot = await transaction.GetSnapshotAsync(runRef, cancellationToken);
            if (!runSnapshot.Exists) throw new InvalidOperationException($"Run {request.RunId} does not exist.");
            var record = ToRecord(runSnapshot);
            if (record.Uid != uid) throw new InvalidOperationException("Run belongs to another user.");

            // Terminal states are immutable: whoever finalized first has won.
            if (record.Status != RunStatus.Running) return new FinalizeResult(false, record);
            if (request.Origin == FinalizeOrigin.Executor && record.LeaseOwner != request.ExecutorId)
                return new FinalizeResult(false, record);
            if (request.Origin == FinalizeOrigin.Reconciler && !IsStale(record))
                return new FinalizeResult(false, record);

            var conversationRef = Conversations.Document(record.ConversationId.ToString("D"));
            var conversationSnapshot = await transaction.GetSnapshotAsync(conversationRef, cancellationToken);

            // ---- reads done ----
            var content = request.Origin == FinalizeOrigin.Reconciler ? record.PartialContent : request.Content;
            var thinking = request.Origin == FinalizeOrigin.Reconciler ? record.PartialThinking : request.Thinking;
            var hasContent = !string.IsNullOrWhiteSpace(content);
            var now = DateTime.UtcNow;

            if (hasContent)
            {
                var assistant = new Message
                {
                    Id = record.AssistantMessageId,
                    ConversationId = record.ConversationId,
                    Role = "model",
                    Content = content!,
                    ThinkingContent = string.IsNullOrWhiteSpace(thinking) ? null : thinking,
                    CreatedAt = now,
                    RunId = record.RunId,
                    CitationsJson = request.Citations is { Count: > 0 } ? JsonSerializer.Serialize(request.Citations) : null,
                    Status = request.Status == RunStatus.Completed ? "final" : "partial"
                };
                transaction.Create(
                    Messages.Document(record.AssistantMessageId.ToString("D")),
                    FirestoreConversationMapper.ToDocument(assistant, uid, request.User.LocalUserId));
            }

            var runUpdate = new Dictionary<string, object>
            {
                [Fields.Status] = request.Status.ToWire(),
                [Fields.StatusReason] = request.Reason,
                [Fields.EndedAt] = FieldValue.ServerTimestamp,
                [Fields.UpdatedAt] = FieldValue.ServerTimestamp,
                [Fields.OutputChars] = content?.Length ?? 0,
                [Fields.HasAssistantMessage] = hasContent,
                [Fields.PartialContent] = FieldValue.Delete,
                [Fields.PartialThinking] = FieldValue.Delete
            };
            if (request.Error != null) runUpdate[Fields.Error] = ErrorToMap(request.Error);
            if (request.Meta is { } meta)
            {
                // Only what is known is written: a missing field means unknown, never zero.
                if (meta.Usage != null) runUpdate[Fields.Usage] = UsageToMap(meta.Usage);
                if (meta.FinishReason != null) runUpdate[Fields.FinishReason] = meta.FinishReason;
                if (meta.ModelVersion != null) runUpdate[Fields.ModelVersion] = meta.ModelVersion;
                if (meta.ResponseId != null) runUpdate[Fields.ResponseId] = meta.ResponseId;
                if (meta.ProviderCalls != null)
                {
                    runUpdate[Fields.ProviderCalls] = new Dictionary<string, object>
                    {
                        ["generate"] = meta.ProviderCalls.Generate,
                        ["summarize"] = meta.ProviderCalls.Summarize,
                        ["countTokens"] = meta.ProviderCalls.CountTokens
                    };
                }

                if (meta.FirstOutputAt != null) runUpdate[Fields.FirstOutputAt] = Timestamp.FromDateTime(meta.FirstOutputAt.Value.ToUniversalTime());
                runUpdate[Fields.ThinkingChars] = meta.ThinkingChars;
                runUpdate[Fields.CitationCount] = meta.CitationCount;
            }

            transaction.Update(runRef, runUpdate);

            if (conversationSnapshot.Exists)
            {
                var conversationUpdate = new Dictionary<string, object>
                {
                    [FirestoreConversationSchema.UpdatedAt] = Timestamp.FromDateTime(now)
                };
                if (request.TokenCount > 0) conversationUpdate[FirestoreConversationSchema.TokenCount] = request.TokenCount;
                if (conversationSnapshot.TryGetValue<string>(FirestoreConversationSchema.ActiveRunId, out var active)
                    && active == record.RunId)
                {
                    conversationUpdate[FirestoreConversationSchema.ActiveRunId] = FieldValue.Delete;
                }

                transaction.Update(conversationRef, conversationUpdate);
            }

            // Quota: a provider that was provably never called gives the reservation back; otherwise real usage is added.
            var refund = !record.ProviderCallStarted ? record.QuotaReservation : null;
            var tokens = record.ProviderCallStarted ? request.TokenCount : 0;
            if (refund != null || tokens > 0)
            {
                transaction.Set(usageRef, _quota.EndOfRunDelta(request.User, tokens, refund), SetOptions.MergeAll);
            }

            var finalRecord = record with
            {
                Status = request.Status,
                StatusReason = request.Reason,
                EndedAt = now,
                Error = request.Error,
                OutputChars = content?.Length ?? 0,
                HasAssistantMessage = hasContent,
                PartialContent = null,
                PartialThinking = null
            };
            return new FinalizeResult(true, finalRecord);
        }, cancellationToken: cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<RunRecord>> ListAsync(
        AuthenticatedUser user, Guid conversationId, CancellationToken cancellationToken)
    {
        var snapshot = await Runs
            .WhereEqualTo(Fields.Uid, Uid(user))
            .WhereEqualTo(Fields.ConversationId, conversationId.ToString("D"))
            .OrderBy(Fields.CreatedAt)
            .GetSnapshotAsync(cancellationToken);
        return snapshot.Documents.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        AuthenticatedUser user, Guid conversationId, int take, CancellationToken cancellationToken)
    {
        var snapshot = await Messages
            .WhereEqualTo(FirestoreConversationSchema.Uid, Uid(user))
            .WhereEqualTo(FirestoreConversationSchema.ConversationId, conversationId.ToString("D"))
            .OrderByDescending(FirestoreConversationSchema.CreatedAt)
            .Limit(Math.Max(1, take))
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Select(FirestoreConversationMapper.ToMessage)
            .Where(message => message != null)
            .Cast<Message>()
            .OrderBy(message => message.CreatedAt)
            .ToList();
    }

    // ------------------------------------------------------------------------------------------------ mapping

    private bool IsStale(RunRecord record)
    {
        var last = record.HeartbeatAt ?? record.AdmittedAt;
        return last == null || DateTime.UtcNow - last.Value > TimeSpan.FromSeconds(_settings.LeaseSeconds);
    }

    private string Truncate(string value) =>
        value.Length <= _settings.MaxCheckpointChars ? value : value[.._settings.MaxCheckpointChars];

    private static string CreateTitle(string content)
    {
        var source = string.IsNullOrWhiteSpace(content) ? "Untitled" : content;
        return source.Length > 50 ? source[..50] + "..." : source;
    }

    private static Dictionary<string, object> RequestedToMap(RunRequested requested)
    {
        var map = new Dictionary<string, object>
        {
            ["providerId"] = requested.ProviderId,
            ["modelName"] = requested.ModelName,
            ["presetId"] = requested.PresetId,
            ["searchMode"] = requested.SearchMode
        };
        if (requested.CustomPrompt != null) map["customPrompt"] = requested.CustomPrompt;
        if (requested.ThinkingEnabled is { } enabled) map["thinkingEnabled"] = enabled;
        if (requested.ThinkingLevel != null) map["thinkingLevel"] = requested.ThinkingLevel;
        if (requested.ThinkingBudget is { } budget) map["thinkingBudget"] = budget;
        if (requested.Advanced is { } advanced)
        {
            var advancedMap = new Dictionary<string, object>();
            if (advanced.SafetyThreshold != null) advancedMap["safetyThreshold"] = advanced.SafetyThreshold;
            if (advanced.Temperature is { } temperature) advancedMap["temperature"] = temperature;
            if (advanced.TopP is { } topP) advancedMap["topP"] = topP;
            if (advanced.MaxOutputTokens is { } maxOutput) advancedMap["maxOutputTokens"] = maxOutput;
            map["advanced"] = advancedMap;
        }

        return map;
    }

    private static Dictionary<string, object> EffectiveToMap(RunEffective effective)
    {
        var map = new Dictionary<string, object>
        {
            ["providerId"] = effective.ProviderId,
            ["modelName"] = effective.ModelName,
            ["presetId"] = effective.PresetId,
            ["searchMode"] = effective.SearchMode
        };
        if (effective.ThinkingEnabled is { } enabled) map["thinkingEnabled"] = enabled;
        if (effective.ThinkingLevel != null) map["thinkingLevel"] = effective.ThinkingLevel;
        if (effective.ThinkingBudget is { } budget) map["thinkingBudget"] = budget;
        if (effective.Temperature is { } temperature) map["temperature"] = temperature;
        if (effective.TopP is { } topP) map["topP"] = topP;
        if (effective.MaxOutputTokens is { } maxOutput) map["maxOutputTokens"] = maxOutput;
        if (effective.SafetyThreshold != null) map["safetyThreshold"] = effective.SafetyThreshold;
        return map;
    }

    private static Dictionary<string, object> UsageToMap(RunUsage usage)
    {
        var map = new Dictionary<string, object> { ["source"] = usage.Source };
        if (usage.InputTokens is { } input) map["inputTokens"] = input;
        if (usage.OutputTokens is { } output) map["outputTokens"] = output;
        if (usage.ThoughtsTokens is { } thoughts) map["thoughtsTokens"] = thoughts;
        if (usage.TotalTokens is { } total) map["totalTokens"] = total;
        return map;
    }

    private static double? Num(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? Convert.ToDouble(value) : null;

    private static int? Int(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? Convert.ToInt32(value) : null;

    private static RunEffective? ReadEffective(Dictionary<string, object>? map) => map == null ? null : new RunEffective(
        map.GetValueOrDefault("providerId") as string ?? "",
        map.GetValueOrDefault("modelName") as string ?? "",
        map.GetValueOrDefault("presetId") as string ?? "",
        map.GetValueOrDefault("searchMode") as string ?? "auto",
        map.GetValueOrDefault("thinkingEnabled") as bool?,
        map.GetValueOrDefault("thinkingLevel") as string,
        Int(map, "thinkingBudget"),
        Num(map, "temperature"),
        Num(map, "topP"),
        Int(map, "maxOutputTokens"),
        map.GetValueOrDefault("safetyThreshold") as string);

    private static RunRequested? ReadRequested(Dictionary<string, object>? map)
    {
        if (map == null) return null;
        ChatAdvancedOptions? advanced = null;
        if (map.GetValueOrDefault("advanced") is Dictionary<string, object> a)
            advanced = new ChatAdvancedOptions(a.GetValueOrDefault("safetyThreshold") as string, Num(a, "temperature"), Num(a, "topP"), Int(a, "maxOutputTokens"));
        return new RunRequested(
            map.GetValueOrDefault("providerId") as string ?? "",
            map.GetValueOrDefault("modelName") as string ?? "",
            map.GetValueOrDefault("presetId") as string ?? "",
            map.GetValueOrDefault("customPrompt") as string,
            map.GetValueOrDefault("searchMode") as string ?? "auto",
            map.GetValueOrDefault("thinkingEnabled") as bool?,
            map.GetValueOrDefault("thinkingLevel") as string,
            Int(map, "thinkingBudget"),
            advanced);
    }

    private static Dictionary<string, object> QuotaToMap(ChatQuotaRequest quota) => new()
    {
        ["requests"] = quota.RequestCount,
        ["estimatedTokens"] = quota.EstimatedTokens,
        ["searches"] = quota.SearchCount,
        ["attachmentBytes"] = quota.AttachmentBytes
    };

    private static Dictionary<string, object> ErrorToMap(RunError error) => new()
    {
        ["code"] = error.Code,
        ["message"] = error.Message,
        ["retryable"] = error.Retryable
    };

    internal RunRecord ToRecord(DocumentSnapshot snapshot)
    {
        T? Read<T>(string field) => snapshot.TryGetValue<T>(field, out var value) ? value : default;
        DateTime? Time(string field) => snapshot.TryGetValue<Timestamp>(field, out var value) ? value.ToDateTime() : null;
        Guid GuidOf(string field) => Guid.TryParse(Read<string>(field), out var value) ? value : Guid.Empty;

        RunError? error = null;
        if (snapshot.TryGetValue<Dictionary<string, object>>(Fields.Error, out var errorMap))
        {
            error = new RunError(
                errorMap.GetValueOrDefault("code") as string ?? RunReasons.Internal,
                errorMap.GetValueOrDefault("message") as string ?? "",
                errorMap.GetValueOrDefault("retryable") as bool? ?? false);
        }

        ChatQuotaRequest? reservation = null;
        if (snapshot.TryGetValue<Dictionary<string, object>>(Fields.QuotaReservation, out var quotaMap))
        {
            reservation = new ChatQuotaRequest(
                new AuthenticatedUser(Guid.TryParse(Read<string>(Fields.LocalUserId), out var local) ? local : Guid.Empty, Read<string>(Fields.Uid), null),
                Convert.ToInt32(quotaMap.GetValueOrDefault("requests") ?? 0),
                Convert.ToInt32(quotaMap.GetValueOrDefault("estimatedTokens") ?? 0),
                Convert.ToInt32(quotaMap.GetValueOrDefault("searches") ?? 0),
                Convert.ToInt64(quotaMap.GetValueOrDefault("attachmentBytes") ?? 0L));
        }

        return new RunRecord
        {
            RunId = snapshot.Id,
            Uid = Read<string>(Fields.Uid) ?? "",
            ConversationId = GuidOf(Fields.ConversationId),
            UserMessageId = GuidOf(Fields.UserMessageId),
            AssistantMessageId = GuidOf(Fields.AssistantMessageId),
            Attempt = (int)(Read<long?>(Fields.Attempt) ?? 1),
            Trigger = Read<string>(Fields.Trigger) ?? "send",
            Status = RunStatuses.Parse(Read<string>(Fields.Status)),
            StatusReason = Read<string>(Fields.StatusReason),
            Fingerprint = Read<string>(Fields.Fingerprint) ?? "",
            ProviderCallStarted = Read<string>(Fields.ProviderCall) == "started",
            LeaseOwner = Read<string>(Fields.LeaseOwner),
            AdmittedAt = Time(Fields.AdmittedAt),
            HeartbeatAt = Time(Fields.HeartbeatAt),
            EndedAt = Time(Fields.EndedAt),
            StopRequestedAt = Time(Fields.StopRequestedAt),
            Error = error,
            OutputChars = (int)(Read<long?>(Fields.OutputChars) ?? 0),
            HasAssistantMessage = Read<bool?>(Fields.HasAssistantMessage) ?? false,
            PartialContent = Read<string>(Fields.PartialContent),
            PartialThinking = Read<string>(Fields.PartialThinking),
            QuotaReservation = reservation,
            CreatedAt = Time(Fields.CreatedAt),
            ProviderCallStartedAt = Time(Fields.ProviderCallStartedAt),
            Requested = ReadRequested(Read<Dictionary<string, object>>(Fields.Requested)),
            Effective = ReadEffective(Read<Dictionary<string, object>>(Fields.Effective)),
            Usage = ReadUsage(Read<Dictionary<string, object>>(Fields.Usage)),
            FinishReason = Read<string>(Fields.FinishReason),
            ModelVersion = Read<string>(Fields.ModelVersion),
            ResponseId = Read<string>(Fields.ResponseId),
            ProviderCalls = ReadProviderCalls(Read<Dictionary<string, object>>(Fields.ProviderCalls)),
            ThinkingChars = (int)(Read<long?>(Fields.ThinkingChars) ?? 0),
            CitationCount = (int)(Read<long?>(Fields.CitationCount) ?? 0),
            FirstOutputAt = Time(Fields.FirstOutputAt)
        };
    }

    private static RunUsage? ReadUsage(Dictionary<string, object>? map) => map == null ? null : new RunUsage(
        Int(map, "inputTokens"), Int(map, "outputTokens"), Int(map, "thoughtsTokens"), Int(map, "totalTokens"),
        map.GetValueOrDefault("source") as string ?? "estimate");

    private static RunProviderCalls? ReadProviderCalls(Dictionary<string, object>? map) => map == null ? null : new RunProviderCalls(
        Int(map, "generate") ?? 0, Int(map, "summarize") ?? 0, Int(map, "countTokens") ?? 0);

    private static class Fields
    {
        public const string Effective = "effective";
        public const string Usage = "usage";
        public const string FinishReason = "finishReason";
        public const string ModelVersion = "modelVersion";
        public const string ResponseId = "responseId";
        public const string ProviderCalls = "providerCalls";
        public const string ThinkingChars = "thinkingChars";
        public const string CitationCount = "citationCount";
        public const string FirstOutputAt = "firstOutputAt";
        public const string Uid = "uid";
        public const string LocalUserId = "localUserId";
        public const string ConversationId = "conversationId";
        public const string UserMessageId = "userMessageId";
        public const string AssistantMessageId = "assistantMessageId";
        public const string Attempt = "attempt";
        public const string Trigger = "trigger";
        public const string Status = "status";
        public const string StatusReason = "statusReason";
        public const string Fingerprint = "fingerprint";
        public const string ProviderCall = "providerCall";
        public const string ProviderCallStartedAt = "providerCallStartedAt";
        public const string Requested = "requested";
        public const string LeaseOwner = "leaseOwner";
        public const string AdmittedAt = "admittedAt";
        public const string HeartbeatAt = "heartbeatAt";
        public const string EndedAt = "endedAt";
        public const string StopRequestedAt = "stopRequestedAt";
        public const string CreatedAt = "createdAt";
        public const string UpdatedAt = "updatedAt";
        public const string Error = "error";
        public const string OutputChars = "outputChars";
        public const string HasAssistantMessage = "hasAssistantMessage";
        public const string PartialContent = "partialContent";
        public const string PartialThinking = "partialThinking";
        public const string QuotaReservation = "quotaReservation";
        public const string SchemaVersion = "schemaVersion";
    }
}
