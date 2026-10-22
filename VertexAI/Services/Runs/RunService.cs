using VertexAI.Services.Search;
using Grpc.Core;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAI.Services.Attachments;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Quota;

namespace VertexAI.Services.Runs;

/// <summary>Switches that only tests register. Production never provides this service.</summary>
public sealed class RunTestHooks
{
    /// <summary>Stops the executor from renewing its lease, as a frozen process would.</summary>
    public volatile bool PauseHeartbeat;
}

public sealed record RunStartRequest(
    AuthenticatedUser User,
    string RunId,
    Guid ConversationId,
    string Message,
    string? SearchMode,
    ChatSessionOptions Options,
    IReadOnlyCollection<ChatAttachment>? Attachments = null,
    string? RetryOfRunId = null);

/// <summary>The outcome of admission plus what execution needs. Only an <c>Admitted</c> result can be executed.</summary>
public sealed record RunAdmission(
    AdmitResult Result,
    RunStartRequest Request,
    IChatModelClient? Model,
    IReadOnlyList<PlannedAttachment>? Attachments = null);

/// <summary>
/// The run lifecycle: admit once, check the right to call the provider, stream, and finish in exactly one terminal
/// state, whatever happens (stop, dropped connection, provider error, timeout, lost lease).
/// </summary>
public sealed class RunService
{
    private readonly IChatProviderCatalog _providers;
    private readonly IRunStore _store;
    private readonly RunSettings _settings;
    private readonly int _maxHistoryTurns;
    private readonly RunCancellationRegistry _registry;
    private readonly IReadOnlyList<IChatRequestAugmenter> _augmenters;
    private readonly ILogger<RunService> _logger;
    private readonly RunTestHooks? _hooks;
    private readonly IChatAttachmentStore _attachments;
    private readonly KeyedLock _admissions = new();
    private readonly IWebSearchService? _webSearch;
    private readonly TimeProvider _clock;

    public RunService(
        IChatProviderCatalog providers,
        IRunStore store,
        IOptions<RunSettings> settings,
        RunCancellationRegistry registry,
        ILogger<RunService> logger,
        IOptions<GeminiSettings>? gemini = null,
        IEnumerable<IChatRequestAugmenter>? augmenters = null,
        RunTestHooks? hooks = null,
        IChatAttachmentStore? attachments = null,
        IWebSearchService? webSearch = null,
        TimeProvider? clock = null)
    {
        _hooks = hooks;
        _webSearch = webSearch;
        _clock = clock ?? TimeProvider.System;
        _attachments = attachments ?? new NoOpChatAttachmentStore();
        _providers = providers;
        _store = store;
        _settings = settings.Value;
        _registry = registry;
        _logger = logger;
        _augmenters = (augmenters ?? [new WebSearchInstructionAugmenter()]).ToArray();
        _maxHistoryTurns = gemini?.Value.MaxHistoryRounds is > 0
            ? gemini.Value.MaxHistoryRounds
            : _settings.MaxHistoryTurns;
    }

    // ------------------------------------------------------------------------------------------------ admission

    public async Task<RunAdmission> AdmitAsync(RunStartRequest request, CancellationToken cancellationToken)
    {
        // A retry carries no message of its own: it answers the stored question of the run it retries.
        IReadOnlyList<ChatAttachment>? retryAttachments = null;
        if (request.RetryOfRunId != null)
        {
            var source = await _store.GetAsync(request.User, request.RetryOfRunId, cancellationToken);
            var sourceMessage = source == null || source.ConversationId != request.ConversationId
                ? null
                : await _store.GetMessageAsync(request.User, request.ConversationId, source.UserMessageId, cancellationToken);
            if (source == null || sourceMessage == null)
            {
                return new RunAdmission(new AdmitResult.Conflict("conflict"), request, null);
            }

            request = request with { Message = sourceMessage.Content, Attachments = null };
            retryAttachments = ChatAttachmentSerializer.Deserialize(sourceMessage.AttachmentsJson);
        }

        var providerId = _providers.ResolveProviderId(request.Options.ProviderId);
        var model = _providers.CreateClient(providerId);
        if (model is IAuthenticatedUserAwareChatModelClient userAware)
        {
            userAware.SetAuthenticatedUser(request.User);
        }

        // Configuring only resolves names and validates options; it makes no provider request.
        await model.ConfigureAsync(request.Options);

        var requested = new RunRequested(
            providerId,
            model.CurrentModelName,
            model.CurrentPresetId,
            string.IsNullOrEmpty(model.CurrentCustomPrompt) ? null : model.CurrentCustomPrompt,
            SearchModes.Normalize(request.SearchMode),
            request.Options.ThinkingEnabled,
            request.Options.ThinkingLevel,
            request.Options.ThinkingBudget,
            request.Options.Advanced);

        // Planning only reads the request: nothing is written to storage before admission has chosen a winner.
        var planned = AttachmentPlanner.Plan(request.RunId, request.ConversationId, request.Attachments, _attachments.IsEnabled);
        var retryOf = request.RetryOfRunId;

        var admit = new AdmitRequest(
            request.User,
            request.RunId,
            request.ConversationId,
            request.Message,
            requested,
            model.CurrentModelName,
            model.CurrentPresetId,
            requested.CustomPrompt,
            ExecutorId: Guid.NewGuid().ToString("N"),
            RunFingerprint.Compute(
                request.ConversationId, retryOf == null ? request.Message : $"retry:{retryOf}", requested,
                AttachmentPlanner.FingerprintPart(planned)),
            new ChatQuotaRequest(
                request.User,
                RequestCount: 1,
                EstimatedTokens: Math.Max(1, request.Message.Length / 4) + planned.Sum(p => (int)Math.Min(int.MaxValue / 8, p.Recorded.SizeBytes ?? 0) / 4),
                SearchCount: SearchModes.EnablesWebSearch(requested.SearchMode) ? 1 : 0,
                AttachmentBytes: planned.Sum(p => p.Recorded.SizeBytes ?? 0)),
            retryAttachments ?? planned.Select(p => p.Recorded).ToList(),
            retryOf);

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await AdmitWithJitterAsync(admit, cancellationToken);
                if (result is AdmitResult.StaleActive stale)
                {
                    // The conversation's previous run lost its lease: close it out, then try again.
                    await GetAsync(request.User, stale.RunId, cancellationToken);
                    continue;
                }

                return new RunAdmission(result, request, model, planned);
            }

            return new RunAdmission(new AdmitResult.Busy("unknown"), request, model, planned);
        }
        catch (OperationCanceledException)
        {
            // The browser went away while we were admitting. The commit may have happened: if so this request
            // owns the run and must close it, otherwise it would sit running until its lease expires.
            await CloseIfOwnedAsync(admit);
            throw;
        }
    }

    /// <summary>
    /// Admission can fail without telling us whether it committed (the store reads back and says "not admitted").
    /// Retrying with the same executor id is safe: if a late commit lands, the retry recognizes it as this request's own.
    /// The pause is random so contenders that failed together do not collide again together.
    /// </summary>
    private async Task<AdmitResult> AdmitWithJitterAsync(AdmitRequest admit, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var turn = await _admissions.AcquireAsync(admit.RunId, cancellationToken);
                return await _store.AdmitAsync(admit, cancellationToken);
            }
            catch (RunAdmissionUnavailableException) when (attempt < 4)
            {
                await Task.Delay(Random.Shared.Next(40, 120 * attempt + 40), cancellationToken);
            }
        }
    }

    private async Task CloseIfOwnedAsync(AdmitRequest admit)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.FinalizeTimeoutSeconds));
            var record = await _store.GetAsync(admit.User, admit.RunId, timeout.Token);
            if (record is { Status: RunStatus.Running } && record.LeaseOwner == admit.ExecutorId)
            {
                await _store.FinalizeAsync(
                    new FinalizeRequest(admit.User, admit.RunId, FinalizeOrigin.Executor, admit.ExecutorId,
                        RunStatus.Interrupted, RunReasons.Connection, null, null, null, 0),
                    timeout.Token);
            }
        }
        catch (Exception ex)
        {
            // The reconciler will close it when the lease expires.
            _logger.LogWarning(ex, "Could not close run {RunId} after the request was cancelled during admission", admit.RunId);
        }
    }

    // ------------------------------------------------------------------------------------------------ execution

    public async Task ExecuteAsync(RunAdmission admission, Func<RunEvent, Task> emit, CancellationToken requestAborted)
    {
        if (admission.Result is not AdmitResult.Admitted admitted || admission.Model is null)
        {
            throw new InvalidOperationException("Only an admitted run can be executed.");
        }

        var run = admitted.Run;
        var request = admission.Request;
        var model = admission.Model;
        var send = new SafeEmitter(emit);

        using var stop = _registry.Register(run.RunId);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.MaxDurationSeconds));
        using var lost = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted, stop.Token, limit.Token, lost.Token);
        using var monitorStop = new CancellationTokenSource();

        var state = new StreamState();
        var monitor = MonitorAsync(run, state, stop, lost, monitorStop.Token);

        RunRecord? final = null;
        try
        {
            await send.Emit(new RunEvent.Started(run.Record, Replayed: false));
            final = await RunToTerminalAsync(run, request, model, admission.Attachments ?? [], state, stop, limit, lost, linked, requestAborted, send);
        }
        finally
        {
            monitorStop.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }

        await send.Emit(new RunEvent.Done(final ?? run.Record));
    }

    private async Task<RunRecord> RunToTerminalAsync(
        AdmittedRun run,
        RunStartRequest request,
        IChatModelClient model,
        IReadOnlyList<PlannedAttachment> planned,
        StreamState state,
        RunCancellationRegistry.Registration stop,
        CancellationTokenSource limit,
        CancellationTokenSource lost,
        CancellationTokenSource linked,
        CancellationToken requestAborted,
        SafeEmitter send)
    {
        try
        {
            // Everything that only reads our own storage happens before the provider-call marker.
            var history = Array.Empty<ChatHistoryEntry>() as IReadOnlyList<ChatHistoryEntry>;
            if (!linked.IsCancellationRequested)
            {
                var recent = await _store.GetRecentMessagesAsync(
                    request.User, run.ConversationId, _maxHistoryTurns * 4 + 2, CancellationToken.None);
                // The current question is the trailing user message and is excluded by the selector.
                history = RunContextSelector.Select(recent, _maxHistoryTurns);
            }

            if (linked.IsCancellationRequested)
            {
                // Cancelled before any provider contact: nothing may be called.
                return await FinishCancelledAsync(run, state, stop, limit, lost, requestAborted, providerStarted: false);
            }

            // Attachments reach storage only now, after admission chose this run's owner and before any provider contact.
            // A failure here ends the run without ever calling the model, and gives the quota reservation back.
            if (planned.Any(p => p.ObjectName != null))
            {
                var failure = await StoreAttachmentsAsync(run, planned, linked.Token);
                if (failure != null)
                {
                    return await FinalizeAsync(run, state, RunStatus.Failed, RunReasons.Storage,
                        new RunError(RunReasons.Storage, failure, true), model);
                }
            }

            history = await WithAttachmentContentAsync(history, linked.Token);

            // The execution-right check. After this returns true the provider may be contacted; before it, never.
            var searchModeForRun = SearchModes.Normalize(request.SearchMode);
            var effective = DescribeEffective(model, request.Options.ProviderId, searchModeForRun);
            switch (await BeginProviderCallAsync(run, effective))
            {
                case BeginOutcome.Refused:
                    return await CurrentRecordAsync(run);
                case BeginOutcome.Unavailable:
                    return await FinalizeAsync(run, state, RunStatus.Failed, RunReasons.Internal,
                        new RunError(RunReasons.Internal, "无法确认运行状态，已放弃本次调用。", true), model);
            }

            await model.LoadHistoryAsync(history);

            var searchMode = SearchModes.Normalize(request.SearchMode);
            var augmentation = new ChatRequestAugmentation(request.Message);
            var context = new ChatRequestContext(
                run.ConversationId, request.User, request.Message, [], searchMode, history, request.Options);
            foreach (var augmenter in _augmenters)
            {
                augmentation = await augmenter.AugmentAsync(context, augmentation, linked.Token);
            }

            var outgoing = AttachmentPlanner.ForModel(planned, request.Attachments);
            if (request.RetryOfRunId != null)
            {
                // The retried question's attachments are already stored (or inline); read them back for the model.
                var again = await WithAttachmentContentAsync(
                    [new ChatHistoryEntry("user", request.Message, null, run.Record.Attachments)], linked.Token);
                outgoing = again[0].Attachments?.ToList() ?? [];
            }

            var messageForModel = augmentation.Message;
            if (model is ITimeAwareChatModelClient)
            {
                // The model has no clock. A provider with no search of its own gets the web through us: search first,
                // then answer from what was found. Every outcome is recorded, including "did not search" and "failed".
                var now = _clock.GetUtcNow();
                WebSearchOutcome? outcome = null;
                if (model is IExternalSearchChatModelClient)
                {
                    // The "please search the web" nudge is for models that can; this one cannot, so it must not be told to.
                    messageForModel = request.Message;
                    if (SearchModes.EnablesWebSearch(searchMode))
                    {
                        outcome = _webSearch == null
                            ? WebSearchOutcome.None(WebSearchStatus.Unavailable, "这个部署没有启用联网检索")
                            : await _webSearch.SearchAsync(new WebSearchRequest(request.Message, history, searchMode, now), linked.Token);
                        state.RecordSearch(outcome);
                        if (outcome.Status == WebSearchStatus.Used && outcome.Citations.Count > 0)
                        {
                            state.AddCitations(outcome.Citations);
                            await send.Emit(new RunEvent.Citations(outcome.Citations));
                        }
                    }
                }

                messageForModel = WebSearchPrompt.Compose(messageForModel, now, outcome);
            }

            var modelRequest = new ChatModelRequest(messageForModel, outgoing, searchMode);
            await foreach (var chunk in model.StreamChatAsync(modelRequest, linked.Token))
            {
                var offset = state.Append(chunk.Text, chunk.IsThinking);
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    await send.Emit(chunk.IsThinking
                        ? new RunEvent.Delta(offset, null, chunk.Text)
                        : new RunEvent.Delta(offset, chunk.Text, null));
                }

                if (chunk.Citations is { Count: > 0 })
                {
                    state.AddCitations(chunk.Citations);
                    await send.Emit(new RunEvent.Citations(chunk.Citations));
                }
            }

            linked.Token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(state.Content))
            {
                var empty = RunErrors.ForEmptyResponse(ErrorContext(model, run));
                return await FinalizeAsync(run, state, RunStatus.Failed, empty.Code, empty, model);
            }

            return await FinalizeAsync(run, state, RunStatus.Completed, "completed", null, model);
        }
        catch (Exception ex) when (linked.IsCancellationRequested || ex is OperationCanceledException)
        {
            return await FinishCancelledAsync(run, state, stop, limit, lost, requestAborted, providerStarted: true, model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId} failed", run.RunId);
            var error = RunErrors.Classify(ex, ErrorContext(model, run));
            return await FinalizeAsync(run, state, RunStatus.Failed, error.Code, error, model);
        }
    }

    private static RunErrorContext ErrorContext(IChatModelClient? model, AdmittedRun run) => new(
        run.Record.Requested?.ProviderId, model?.CurrentModelName ?? run.Record.Requested?.ModelName,
        (model as IChatRunTelemetrySource)?.Telemetry?.FinishReason);

    private async Task<RunRecord> FinishCancelledAsync(
        AdmittedRun run,
        StreamState state,
        RunCancellationRegistry.Registration stop,
        CancellationTokenSource limit,
        CancellationTokenSource lost,
        CancellationToken requestAborted,
        bool providerStarted,
        IChatModelClient? model = null)
    {
        if (lost.IsCancellationRequested || state.LeaseLost)
        {
            // Someone else already closed this run; our output is discarded and we report what is true.
            return await CurrentRecordAsync(run);
        }

        if (stop.StopRequested || state.StopRequestedSeen)
        {
            return await FinalizeAsync(run, state, RunStatus.Stopped, RunReasons.User, null, model);
        }

        if (limit.IsCancellationRequested && !requestAborted.IsCancellationRequested)
        {
            return await FinalizeAsync(run, state, RunStatus.Failed, RunReasons.Timeout,
                new RunError(RunReasons.Timeout, "已达到单次生成的时长上限，已保存已生成的部分。", true), model);
        }

        // The stream dropped. The browser sends an explicit stop first and then closes the connection, so give
        // that stop a moment to arrive before calling this a lost connection.
        if (await StopArrivesWithinGraceAsync(run))
        {
            return await FinalizeAsync(run, state, RunStatus.Stopped, RunReasons.User, null, model);
        }

        return await FinalizeAsync(run, state, RunStatus.Interrupted, RunReasons.Connection, null, model);
    }

    private async Task<bool> StopArrivesWithinGraceAsync(AdmittedRun run)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_settings.DisconnectGraceMilliseconds);
        var pause = Math.Max(10, Math.Min(100, _settings.DisconnectGraceMilliseconds / 10));
        while (true)
        {
            try
            {
                if ((await _store.PollAsync(run, CancellationToken.None)).StopRequested) return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not poll run {RunId} during the disconnect grace period", run.RunId);
            }

            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(pause);
        }
    }

    private enum BeginOutcome { Allowed, Refused, Unavailable }

    /// <summary>
    /// Writes each attachment under its content-hash name, create-only. Returns null when all are stored (and recorded as
    /// such), or a message when one could not be: then the objects this run created are removed again.
    /// </summary>
    private async Task<string?> StoreAttachmentsAsync(AdmittedRun run, IReadOnlyList<PlannedAttachment> planned, CancellationToken cancellationToken)
    {
        var created = new List<ChatAttachment>();
        var states = new Dictionary<int, string>();
        string? failure = null;
        try
        {
            for (var index = 0; index < planned.Count; index++)
            {
                var item = planned[index];
                if (item.ObjectName == null || item.Bytes == null) continue;
                if (await _attachments.SaveCreateOnlyAsync(item.ObjectName, item.Recorded.MimeType, item.Bytes, cancellationToken))
                {
                    created.Add(item.Recorded);
                }

                states[index] = AttachmentStates.Stored;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} could not store its attachments", run.RunId);
            failure = "附件保存失败，没有调用模型，你的消息已保存，可以重试。";
        }

        if (failure != null)
        {
            // Only what this run wrote is removed; an object that was already there was never ours.
            foreach (var attachment in created)
            {
                try { await _attachments.DeleteAsync(attachment, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove attachment {Object}", attachment.StorageObjectName); }
            }

            for (var index = 0; index < planned.Count; index++) states[index] = AttachmentStates.Failed;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.FinalizeTimeoutSeconds));
            await _store.SetAttachmentStatesAsync(run, states, timeout.Token);
        }
        catch (Exception ex)
        {
            // Unreadable state is shown as unavailable and never sent to a model; the run's own outcome stands.
            _logger.LogWarning(ex, "Could not record attachment states for run {RunId}", run.RunId);
            if (failure == null) failure = "附件保存状态无法确认，没有调用模型，你的消息已保存，可以重试。";
        }

        return failure;
    }

    /// <summary>Earlier turns keep their attachments as context. One that cannot be read is left out, not fatal.</summary>
    private async Task<IReadOnlyList<ChatHistoryEntry>> WithAttachmentContentAsync(
        IReadOnlyList<ChatHistoryEntry> history, CancellationToken cancellationToken)
    {
        if (history.All(entry => entry.Attachments is not { Count: > 0 })) return history;

        var result = new List<ChatHistoryEntry>(history.Count);
        foreach (var entry in history)
        {
            if (entry.Attachments is not { Count: > 0 })
            {
                result.Add(entry);
                continue;
            }

            var loaded = new List<ChatAttachment>();
            foreach (var attachment in entry.Attachments)
            {
                try
                {
                    loaded.Add(string.IsNullOrWhiteSpace(attachment.Base64Data) ? await _attachments.LoadAsync(attachment, cancellationToken) : attachment);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping an earlier attachment that could not be read ({Object})", attachment.StorageObjectName);
                }
            }

            result.Add(entry with { Attachments = loaded });
        }

        return result;
    }

    private RunEffective? DescribeEffective(IChatModelClient model, string? providerId, string searchMode)
    {
        if (model is not IChatRunTelemetrySource source) return null;
        var applied = source.Effective;
        return new RunEffective(
            _providers.ResolveProviderId(providerId), applied.ModelName, applied.PresetId, searchMode,
            applied.ThinkingEnabled, applied.ThinkingLevel, applied.ThinkingBudget,
            applied.Temperature, applied.TopP, applied.MaxOutputTokens, applied.SafetyThreshold);
    }

    private async Task<BeginOutcome> BeginProviderCallAsync(AdmittedRun run, RunEffective? effective)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.FinalizeTimeoutSeconds));
                return await _store.BeginProviderCallAsync(run, timeout.Token, effective)
                    ? BeginOutcome.Allowed
                    : BeginOutcome.Refused;
            }
            catch (Exception ex)
            {
                // Unknown whether the marker was written. Read it back instead of assuming.
                _logger.LogWarning(ex, "Provider-call marker for run {RunId} is uncertain (attempt {Attempt})", run.RunId, attempt + 1);
                try
                {
                    var record = await _store.GetAsync(run.User, run.RunId, CancellationToken.None);
                    if (record is { Status: RunStatus.Running, ProviderCallStarted: true } && record.LeaseOwner == run.ExecutorId)
                        return BeginOutcome.Allowed;
                    if (record is { Status: not RunStatus.Running })
                        return BeginOutcome.Refused;
                }
                catch (Exception readFailure)
                {
                    _logger.LogWarning(readFailure, "Could not read run {RunId} back", run.RunId);
                }

                await Task.Delay(100 * (attempt + 1));
            }
        }

        return BeginOutcome.Unavailable;
    }

    /// <summary>
    /// What this run learned. Provider figures are used as reported; when the provider reported no usage but text was
    /// produced, an estimate is stored and labelled as one. Nothing is made up for a run that never reached the provider.
    /// </summary>
    private static RunMeta? BuildMeta(IChatModelClient? model, StreamState state, string? content, string? thinking)
    {
        var telemetry = (model as IChatRunTelemetrySource)?.Telemetry;
        RunUsage? usage = null;
        if (telemetry?.Usage is { } reported)
        {
            usage = new RunUsage(reported.InputTokens, reported.OutputTokens, reported.ThoughtsTokens, reported.TotalTokens, "provider");
        }
        else if (!string.IsNullOrEmpty(content))
        {
            usage = new RunUsage(null, Math.Max(1, content.Length / 4), null, null, "estimate");
        }

        var calls = telemetry is { GenerateCalls: > 0 }
            ? new RunProviderCalls(telemetry.GenerateCalls, telemetry.SummarizeCalls, telemetry.CountTokensCalls, state.SearchCalls)
            : null;
        return new RunMeta(
            usage, telemetry?.FinishReason, telemetry?.ModelVersion, telemetry?.ResponseId, calls,
            thinking?.Length ?? 0, state.Citations().Count, state.FirstOutputAt, state.Search);
    }

    private async Task<RunRecord> FinalizeAsync(
        AdmittedRun run,
        StreamState state,
        RunStatus status,
        string reason,
        RunError? error,
        IChatModelClient? model)
    {
        var (content, thinking) = state.Snapshot();
        var tokens = model is { CurrentTokenCount: > 0 }
            ? model.CurrentTokenCount
            : Math.Max(content?.Length ?? 0, 0) / 4;
        var request = new FinalizeRequest(
            run.User, run.RunId, FinalizeOrigin.Executor, run.ExecutorId,
            status, reason, content, thinking, error, tokens, state.Citations(),
            BuildMeta(model, state, content, thinking));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Never the request's own token: it is usually the very thing that was cancelled.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.FinalizeTimeoutSeconds));
                return (await _store.FinalizeAsync(request, timeout.Token)).Record;
            }
            catch (Exception ex) when (attempt < 2)
            {
                _logger.LogWarning(ex, "Finalizing run {RunId} failed (attempt {Attempt})", run.RunId, attempt + 1);
                await Task.Delay(200 * (attempt + 1));
            }
            catch (Exception ex)
            {
                // The reconciler will close the run from its last checkpoint once the lease expires.
                _logger.LogError(ex, "Run {RunId} could not be finalized; leaving it for reconciliation", run.RunId);
                return run.Record;
            }
        }
    }

    private async Task<RunRecord> CurrentRecordAsync(AdmittedRun run)
    {
        try
        {
            return await _store.GetAsync(run.User, run.RunId, CancellationToken.None) ?? run.Record;
        }
        catch (Exception)
        {
            return run.Record;
        }
    }

    // ------------------------------------------------------------------------------------------------ monitor

    /// <summary>
    /// Runs beside the stream: notices a stop that arrived on another instance, renews the lease with a checkpoint,
    /// and cancels the provider call itself when it can no longer prove it holds the lease.
    /// </summary>
    private async Task MonitorAsync(
        AdmittedRun run,
        StreamState state,
        RunCancellationRegistry.Registration stop,
        CancellationTokenSource lost,
        CancellationToken monitorStop)
    {
        var heartbeatEvery = TimeSpan.FromSeconds(_settings.HeartbeatSeconds);
        var selfFenceAfter = TimeSpan.FromSeconds(_settings.LeaseSeconds * 2.0 / 3.0);
        var lastHeartbeat = DateTime.UtcNow;
        var lastGood = DateTime.UtcNow;
        var checkpointVersion = -1;

        try
        {
            while (!monitorStop.IsCancellationRequested)
            {
                await Task.Delay(_settings.StopPollMilliseconds, monitorStop);

                try
                {
                    var lease = await _store.PollAsync(run, monitorStop);
                    lastGood = DateTime.UtcNow;
                    if (!lease.OwnedByCaller)
                    {
                        state.LeaseLost = true;
                        lost.Cancel();
                        return;
                    }

                    if (lease.StopRequested && !state.StopRequestedSeen)
                    {
                        state.StopRequestedSeen = true;
                        _registry.Cancel(run.RunId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Polling run {RunId} failed", run.RunId);
                }

                if (DateTime.UtcNow - lastHeartbeat >= heartbeatEvery && _hooks?.PauseHeartbeat != true)
                {
                    lastHeartbeat = DateTime.UtcNow;
                    try
                    {
                        var (version, content, thinking) = state.Versioned();
                        var checkpoint = version != checkpointVersion && !string.IsNullOrEmpty(content)
                            ? new RunCheckpoint(content, thinking)
                            : null;
                        if (!await _store.HeartbeatAsync(run, checkpoint, monitorStop))
                        {
                            state.LeaseLost = true;
                            lost.Cancel();
                            return;
                        }

                        checkpointVersion = checkpoint != null ? version : checkpointVersion;
                        lastGood = DateTime.UtcNow;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Heartbeat for run {RunId} failed", run.RunId);
                    }
                }

                // Self-fence: if we cannot show that we still hold the lease, stop talking to the provider
                // before another instance reconciles the run.
                if (DateTime.UtcNow - lastGood > selfFenceAfter)
                {
                    _logger.LogError("Run {RunId} could not renew its lease; cancelling the provider call", run.RunId);
                    state.LeaseLost = true;
                    lost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown of the monitor
        }
    }

    // ------------------------------------------------------------------------------------------------ queries

    public async Task<RunRecord?> StopAsync(AuthenticatedUser user, string runId, CancellationToken cancellationToken)
    {
        var record = await _store.RequestStopAsync(user, runId, cancellationToken);
        if (record is { Status: RunStatus.Running })
        {
            // If this instance is running it, cancel right away; otherwise the executor sees the flag within a poll.
            _registry.Cancel(runId);
        }

        return record;
    }

    /// <summary>Reads a run, closing it out first if it is running but its lease has expired.</summary>
    public async Task<RunRecord?> GetAsync(AuthenticatedUser user, string runId, CancellationToken cancellationToken)
    {
        var record = await _store.GetAsync(user, runId, cancellationToken);
        return record is null ? null : await ReconcileAsync(user, record, cancellationToken);
    }

    public async Task<IReadOnlyList<RunRecord>> ListAsync(
        AuthenticatedUser user, Guid conversationId, CancellationToken cancellationToken)
    {
        var records = await _store.ListAsync(user, conversationId, cancellationToken);
        var reconciled = new List<RunRecord>(records.Count);
        foreach (var record in records)
        {
            reconciled.Add(await ReconcileAsync(user, record, cancellationToken));
        }

        return reconciled;
    }

    private async Task<RunRecord> ReconcileAsync(AuthenticatedUser user, RunRecord record, CancellationToken cancellationToken)
    {
        if (record.Status != RunStatus.Running) return record;
        var last = record.HeartbeatAt ?? record.AdmittedAt;
        if (last != null && DateTime.UtcNow - last.Value <= TimeSpan.FromSeconds(_settings.LeaseSeconds)) return record;

        // Never calls the model: it only writes a terminal state, using the last checkpoint as the content.
        // Several pollers can reach this point together and Firestore aborts the transactions that lose the race.
        // A lost race is not an error: whoever won wrote the terminal state, so read it back (a few times), and if
        // it is still not terminal the next poll reconciles.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await _store.FinalizeAsync(
                    new FinalizeRequest(user, record.RunId, FinalizeOrigin.Reconciler, null,
                        RunStatus.Interrupted, RunReasons.ServerLost, null, null,
                        new RunError(RunReasons.ServerLost, "服务端在生成过程中中断，已保存最近一次检查点的内容。", false), 0),
                    cancellationToken);
                return result.Record;
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Aborted)
            {
                var current = await _store.GetAsync(user, record.RunId, cancellationToken) ?? record;
                if (current.Status != RunStatus.Running || attempt >= 5) return current;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ reads

    public Task<int> DeleteRunsOfConversationAsync(AuthenticatedUser user, Guid conversationId, CancellationToken cancellationToken) =>
        _store.DeleteRunsOfConversationAsync(user, conversationId, cancellationToken);

    public Task<Conversation?> GetConversationViewAsync(AuthenticatedUser user, Guid conversationId, CancellationToken cancellationToken) =>
        _store.GetConversationForViewAsync(user, conversationId, cancellationToken);

    /// <summary>
    /// The bytes of one attachment, or null when there is nothing to serve: not yours, not there, or not (yet) in storage.
    /// Stored objects are checked against the hash recorded when they were written.
    /// </summary>
    public async Task<(ChatAttachment Attachment, byte[] Bytes)?> GetAttachmentAsync(
        AuthenticatedUser user, Guid conversationId, Guid messageId, int index, CancellationToken cancellationToken)
    {
        var message = await _store.GetMessageAsync(user, conversationId, messageId, cancellationToken);
        if (message == null) return null;
        var attachments = ChatAttachmentSerializer.Deserialize(message.AttachmentsJson);
        if (index < 0 || index >= attachments.Count) return null;

        var attachment = attachments[index];
        if (attachment.State == AttachmentStates.Pending && message.RunId != null)
        {
            // A pending attachment of a finished run never made it; it is not served.
            return null;
        }

        if (!AttachmentStates.IsUsable(attachment.State)) return null;
        var loaded = string.IsNullOrWhiteSpace(attachment.Base64Data) ? await _attachments.LoadAsync(attachment, cancellationToken) : attachment;
        if (string.IsNullOrWhiteSpace(loaded.Base64Data)) return null;
        return (attachment, Convert.FromBase64String(loaded.Base64Data));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>Emitting to a browser that has gone away must never fail the run.</summary>
    private sealed class SafeEmitter(Func<RunEvent, Task> inner)
    {
        private bool _broken;

        public async Task Emit(RunEvent @event)
        {
            if (_broken) return;
            try
            {
                await inner(@event);
            }
            catch (Exception)
            {
                _broken = true;
            }
        }
    }

    private sealed class StreamState
    {
        private readonly object _gate = new();
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _thinking = new();
        private int _version;

        public volatile bool LeaseLost;
        public volatile bool StopRequestedSeen;

        public string Content { get { lock (_gate) return _content.ToString(); } }

        private DateTime? _firstOutputAt;
        public DateTime? FirstOutputAt { get { lock (_gate) return _firstOutputAt; } }

        public int Append(string text, bool thinking)
        {
            lock (_gate)
            {
                if (_firstOutputAt == null && text.Length > 0) _firstOutputAt = DateTime.UtcNow;
                var builder = thinking ? _thinking : _content;
                var offset = builder.Length;
                builder.Append(text);
                _version++;
                return offset;
            }
        }

        private readonly List<SearchCitation> _citations = [];

        private RunSearch? _search;
        private int _searchCalls;
        public RunSearch? Search { get { lock (_gate) return _search; } }
        public int SearchCalls { get { lock (_gate) return _searchCalls; } }

        public void RecordSearch(WebSearchOutcome outcome)
        {
            lock (_gate)
            {
                _searchCalls = outcome.ProviderCalls;
                _search = new RunSearch(outcome.Status, outcome.Query, outcome.Citations.Count, outcome.Reason);
            }
        }

        public void AddCitations(IEnumerable<SearchCitation> items)
        {
            lock (_gate)
            {
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.Uri) && !_citations.Any(c => c.Uri == item.Uri)) _citations.Add(item);
                }
            }
        }

        public IReadOnlyList<SearchCitation> Citations() { lock (_gate) return _citations.ToList(); }

        public (string? Content, string? Thinking) Snapshot()
        {
            lock (_gate)
            {
                return (_content.Length > 0 ? _content.ToString() : null, _thinking.Length > 0 ? _thinking.ToString() : null);
            }
        }

        public (int Version, string Content, string? Thinking) Versioned()
        {
            lock (_gate)
            {
                return (_version, _content.ToString(), _thinking.Length > 0 ? _thinking.ToString() : null);
            }
        }
    }
}
