using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    ChatSessionOptions Options);

/// <summary>The outcome of admission plus what execution needs. Only an <c>Admitted</c> result can be executed.</summary>
public sealed record RunAdmission(AdmitResult Result, RunStartRequest Request, IChatModelClient? Model);

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

    public RunService(
        IChatProviderCatalog providers,
        IRunStore store,
        IOptions<RunSettings> settings,
        RunCancellationRegistry registry,
        ILogger<RunService> logger,
        IOptions<GeminiSettings>? gemini = null,
        IEnumerable<IChatRequestAugmenter>? augmenters = null,
        RunTestHooks? hooks = null)
    {
        _hooks = hooks;
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
            RunFingerprint.Compute(request.ConversationId, request.Message, requested),
            new ChatQuotaRequest(
                request.User,
                RequestCount: 1,
                EstimatedTokens: Math.Max(1, request.Message.Length / 4),
                SearchCount: SearchModes.EnablesWebSearch(requested.SearchMode) ? 1 : 0,
                AttachmentBytes: 0));

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

                return new RunAdmission(result, request, model);
            }

            return new RunAdmission(new AdmitResult.Busy("unknown"), request, model);
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
            final = await RunToTerminalAsync(run, request, model, state, stop, limit, lost, linked, requestAborted, send);
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

            // The execution-right check. After this returns true the provider may be contacted; before it, never.
            switch (await BeginProviderCallAsync(run))
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

            var modelRequest = new ChatModelRequest(augmentation.Message, [], searchMode);
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
                return await FinalizeAsync(run, state, RunStatus.Failed, RunReasons.EmptyResponse,
                    RunErrors.Classify(new InvalidOperationException("The response was empty.")), model);
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
            var error = RunErrors.Classify(ex);
            return await FinalizeAsync(run, state, RunStatus.Failed, error.Code, error, model);
        }
    }

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

    private async Task<BeginOutcome> BeginProviderCallAsync(AdmittedRun run)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.FinalizeTimeoutSeconds));
                return await _store.BeginProviderCallAsync(run, timeout.Token)
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
            status, reason, content, thinking, error, tokens, state.Citations());

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
        var result = await _store.FinalizeAsync(
            new FinalizeRequest(user, record.RunId, FinalizeOrigin.Reconciler, null,
                RunStatus.Interrupted, RunReasons.ServerLost, null, null,
                new RunError(RunReasons.ServerLost, "服务端在生成过程中中断，已保存最近一次检查点的内容。", false), 0),
            cancellationToken);
        return result.Record;
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

        public int Append(string text, bool thinking)
        {
            lock (_gate)
            {
                var builder = thinking ? _thinking : _content;
                var offset = builder.Length;
                builder.Append(text);
                _version++;
                return offset;
            }
        }

        private readonly List<SearchCitation> _citations = [];

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
