using System.Diagnostics;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>
/// Protocol tests for the run lifecycle. They exercise the real RunService, FirestoreRunStore, quota service and
/// conversation store against the Firestore emulator; only the model is scripted. Without an emulator they are skipped.
/// </summary>
internal static class RunLifecycleTests
{
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static List<RunEvent> Collector(out Func<RunEvent, Task> emit)
    {
        var events = new List<RunEvent>();
        emit = e => { lock (events) events.Add(e); return Task.CompletedTask; };
        return events;
    }

    private static bool HasDelta(List<RunEvent> events) { lock (events) return events.OfType<RunEvent.Delta>().Any(); }

    private static RunRecord? Final(List<RunEvent> events) { lock (events) return events.OfType<RunEvent.Done>().LastOrDefault()?.Run; }

    private static AdmittedRun Admitted(RunAdmission admission) =>
        ((AdmitResult.Admitted)admission.Result).Run;

    // ---------------------------------------------------------------------------------------- idempotency

    public static void ConcurrentSendsOfOneRunExecuteExactlyOnce() => Wait(Async());
    private static async Task Async()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var conversation = Guid.NewGuid();

        // A browser whose request failed before any answer retries with the same run id.
        async Task<RunOutcome> SendWithRetry()
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await h.SendAsync("/ok hello", runId, conversation); }
                catch (RunAdmissionUnavailableException) when (attempt < 10) { await Task.Delay(Random.Shared.Next(20, 250)); }
            }
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(SendWithRetry)));

        Assert.Equal(1, outcomes.Count(o => o.Result is AdmitResult.Admitted));
        Assert.Equal(19, outcomes.Count(o => o.Result is AdmitResult.Replay));
        Assert.Equal(1, h.Probe.StreamCalls);
        Assert.Equal(RunStatus.Completed, outcomes.Single(o => o.Result is AdmitResult.Admitted).Final!.Status);

        var messages = await h.MessagesAsync(conversation);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", (string)messages[0]["role"]);
        Assert.Equal("model", (string)messages[1]["role"]);
        Assert.Equal("Hello world", (string)messages[1]["content"]);
        Assert.Equal(1L, await h.UsageAsync("requests"));
    }

    public static void SendsFromSeveralInstancesStillExecuteExactlyOnce() => Wait(AsyncAcross());
    private static async Task AsyncAcross()
    {
        // Separate service objects share no in-process lock: only the storage transaction decides between them.
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var conversation = Guid.NewGuid();
        var instances = Enumerable.Range(0, 3).Select(_ => h.NewService(new RunCancellationRegistry())).ToArray();

        async Task<RunOutcome> SendWithRetry(RunService service)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await h.SendAsync("/ok hello", runId, conversation, service: service); }
                catch (RunAdmissionUnavailableException) when (attempt < 10) { await Task.Delay(Random.Shared.Next(20, 250)); }
            }
        }

        var outcomes = await Task.WhenAll(instances.SelectMany(service => Enumerable.Range(0, 2).Select(_ => Task.Run(() => SendWithRetry(service)))));

        Assert.Equal(1, outcomes.Count(o => o.Result is AdmitResult.Admitted));
        Assert.Equal(5, outcomes.Count(o => o.Result is AdmitResult.Replay));
        Assert.Equal(1, h.Probe.StreamCalls);
        Assert.Equal(2, (await h.MessagesAsync(conversation)).Count);
        Assert.Equal(1L, await h.UsageAsync("requests"));
    }

    public static void ReplayOfAFinishedRunReportsItWithoutExecuting() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var conversation = Guid.NewGuid();
        var first = await h.SendAsync("/ok hello", runId, conversation);
        var again = await h.SendAsync("/ok hello", runId, conversation);

        var replay = (AdmitResult.Replay)again.Result;
        Assert.Equal(RunStatus.Completed, replay.Run.Status);
        Assert.Equal(first.Final!.UserMessageId, replay.Run.UserMessageId);
        Assert.Equal(1, h.Probe.StreamCalls);
        Assert.Equal(2, (await h.MessagesAsync(conversation)).Count);
    }

    public static void ConflictingOrForeignRunIdsAreRefused() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var conversation = Guid.NewGuid();
        await h.SendAsync("/ok original", runId, conversation);

        var changed = await h.SendAsync("/ok changed", runId, conversation);
        Assert.Equal("idempotency_mismatch", ((AdmitResult.Conflict)changed.Result).Code);

        var stranger = new AuthenticatedUser(Guid.NewGuid(), "uid-stranger", "x@example.test");
        var foreign = await h.SendAsync("/ok original", runId, conversation, user: stranger);
        Assert.Equal("conflict", ((AdmitResult.Conflict)foreign.Result).Code);

        // A stranger cannot write into someone else's conversation either, whatever the run id.
        var intrusion = await h.SendAsync("/ok hi", RunHarness.NewRunId(), conversation, user: stranger);
        Assert.Equal("conflict", ((AdmitResult.Conflict)intrusion.Result).Code);

        Assert.Equal(1, h.Probe.StreamCalls);
        Assert.Equal(2, (await h.MessagesAsync(conversation)).Count);
    }

    // ---------------------------------------------------------------------------------------- stop

    public static void StopEndsTheRunAndKeepsWhatWasGenerated() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var conversation = Guid.NewGuid();
        var admission = await h.Service.AdmitAsync(h.Request("/slow go", runId, conversation), default);
        var events = Collector(out var emit);
        var execution = Task.Run(() => h.Service.ExecuteAsync(admission, emit, CancellationToken.None));

        Assert.True(await RunHarness.EventuallyAsync(() => HasDelta(events)));
        var stoppedAt = Stopwatch.GetTimestamp();
        var snapshot = await h.Service.StopAsync(h.User, runId, default);
        Assert.NotNull(snapshot);
        await execution;

        var final = Final(events)!;
        Assert.Equal(RunStatus.Stopped, final.Status);
        Assert.Equal("user", final.StatusReason!);
        Assert.True(h.Probe.CancelObservedTicks != 0);
        Assert.True(Stopwatch.GetElapsedTime(stoppedAt, h.Probe.CancelObservedTicks).TotalSeconds < 2);

        var messages = await h.MessagesAsync(conversation);
        Assert.Equal(2, messages.Count);
        Assert.Equal("partial", (string)messages[1]["status"]);
        Assert.True(((string)messages[1]["content"]).StartsWith("line 1"));

        var conversationDocument = await h.ConversationAsync(conversation);
        Assert.False(conversationDocument!.ContainsKey("activeRunId"));

        // Stopping again, or a stranger / unknown run, changes nothing.
        Assert.Equal(RunStatus.Stopped, (await h.Service.StopAsync(h.User, runId, default))!.Status);
        Assert.Null(await h.Service.StopAsync(h.User, RunHarness.NewRunId(), default));
        Assert.Null(await h.Service.StopAsync(new AuthenticatedUser(Guid.NewGuid(), "uid-x", null), runId, default));
        Assert.Equal(2, (await h.MessagesAsync(conversation)).Count);
    }

    public static void StopFromAnotherInstanceTravelsThroughStorage() => Wait(Async5());
    private static async Task Async5()
    {
        var h = new RunHarness();
        var other = h.NewService(new RunCancellationRegistry()); // knows nothing about the running generation
        var runId = RunHarness.NewRunId();
        var admission = await h.Service.AdmitAsync(h.Request("/slow go", runId), default);
        var events = Collector(out var emit);
        var execution = Task.Run(() => h.Service.ExecuteAsync(admission, emit, CancellationToken.None));

        Assert.True(await RunHarness.EventuallyAsync(() => HasDelta(events)));
        Assert.NotNull(await other.StopAsync(h.User, runId, default));
        await execution;

        Assert.Equal(RunStatus.Stopped, Final(events)!.Status);
    }

    // ---------------------------------------------------------------------------------------- disconnect

    public static void DroppedConnectionInterruptsAndKeepsPartialOutput() => Wait(Async6());
    private static async Task Async6()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        using var connection = new CancellationTokenSource();
        var runId = RunHarness.NewRunId();
        var admission = await h.Service.AdmitAsync(h.Request("/slow go", runId, conversation), connection.Token);
        var events = Collector(out var emit);
        var execution = Task.Run(() => h.Service.ExecuteAsync(admission, emit, connection.Token));

        Assert.True(await RunHarness.EventuallyAsync(() => HasDelta(events)));
        connection.Cancel(); // the browser vanished without sending a stop
        await execution;

        var final = Final(events)!;
        Assert.Equal(RunStatus.Interrupted, final.Status);
        Assert.Equal("connection", final.StatusReason!);
        Assert.True(h.Probe.CancelObservedTicks != 0);
        var messages = await h.MessagesAsync(conversation);
        Assert.Equal("partial", (string)messages[1]["status"]);
    }

    public static void AStopInsideTheGracePeriodCountsAsAStop() => Wait(Async7());
    private static async Task Async7()
    {
        var h = new RunHarness();
        using var connection = new CancellationTokenSource();
        var runId = RunHarness.NewRunId();
        var admission = await h.Service.AdmitAsync(h.Request("/slow go", runId), connection.Token);
        var events = Collector(out var emit);
        var execution = Task.Run(() => h.Service.ExecuteAsync(admission, emit, connection.Token));

        Assert.True(await RunHarness.EventuallyAsync(() => HasDelta(events)));
        connection.Cancel();
        await Task.Delay(100); // inside the 400 ms grace period
        await h.Store.RequestStopAsync(h.User, runId, default);
        await execution;

        Assert.Equal(RunStatus.Stopped, Final(events)!.Status);
    }

    // ---------------------------------------------------------------------------------------- lost executors

    public static void ALostExecutorIsReconciledOnceAndNeverReachesTheModel() => Wait(Async8());
    private static async Task Async8()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        var admitted = Admitted(await h.Service.AdmitAsync(h.Request("/slow go", runId, conversation), default));

        // The process "dies" after it started talking to the provider and stored one checkpoint.
        Assert.True(await h.Store.BeginProviderCallAsync(admitted, default));
        Assert.True(await h.Store.HeartbeatAsync(admitted, new RunCheckpoint("partial text", null), default));
        await Task.Delay((RunHarness.LeaseSeconds + 1) * 1000);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => h.Service.GetAsync(h.User, runId, default)));
        Assert.True(results.All(r => r!.Status == RunStatus.Interrupted && r.StatusReason == "server_lost"));

        var messages = await h.MessagesAsync(conversation);
        Assert.Equal(2, messages.Count);
        Assert.Equal("partial text", (string)messages[1]["content"]);
        Assert.Equal("partial", (string)messages[1]["status"]);
        Assert.Equal(1L, await h.UsageAsync("requests")); // the provider was contacted: no refund
        Assert.Equal(0, h.Probe.StreamCalls);              // reconciling never calls the model
        Assert.False((await h.ConversationAsync(conversation))!.ContainsKey("activeRunId"));
    }

    public static void ARunLostBeforeTheProviderCallRefundsItsReservation() => Wait(Async9());
    private static async Task Async9()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        Admitted(await h.Service.AdmitAsync(h.Request("/ok hi", runId, conversation), default));
        Assert.Equal(1L, await h.UsageAsync("requests"));

        await Task.Delay((RunHarness.LeaseSeconds + 1) * 1000);
        var record = (await h.Service.GetAsync(h.User, runId, default))!;

        Assert.Equal(RunStatus.Interrupted, record.Status);
        Assert.False(record.ProviderCallStarted); // durable proof that the provider was never contacted
        Assert.Equal(0L, await h.UsageAsync("requests"));
        Assert.Equal(0L, await h.UsageAsync("estimatedTokens"));
        Assert.Equal(1, (await h.MessagesAsync(conversation)).Count); // the question stays, there is no reply
        Assert.Equal(0, h.Probe.StreamCalls);
    }

    public static void AFrozenExecutorIsFencedOnceItsRunWasReconciled() => Wait(Async10());
    private static async Task Async10()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        h.Hooks.PauseHeartbeat = true; // the executor "freezes": it stops renewing its lease
        var admission = await h.Service.AdmitAsync(h.Request("/gate go", runId, conversation), default);
        var events = Collector(out var emit);
        var execution = Task.Run(() => h.Service.ExecuteAsync(admission, emit, CancellationToken.None));
        Assert.True(await RunHarness.EventuallyAsync(() => HasDelta(events)));

        await Task.Delay((RunHarness.LeaseSeconds + 1) * 1000);
        var reconciled = (await h.Service.GetAsync(h.User, runId, default))!;
        Assert.Equal(RunStatus.Interrupted, reconciled.Status);

        // The frozen executor wakes up, sees it no longer holds the run and stops talking to the model.
        Assert.True(await RunHarness.EventuallyAsync(() => h.Probe.CancelObservedTicks != 0, 3000));
        await execution;
        h.Hooks.PauseHeartbeat = false;

        Assert.Equal(RunStatus.Interrupted, Final(events)!.Status);
        var stored = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal("server_lost", stored.StatusReason!);
        // Its output was discarded: nothing but the question exists.
        Assert.Equal(1, (await h.MessagesAsync(conversation)).Count);
    }

    public static void TheProviderCallMarkerIsWrittenBeforeTheModelIsCalled() => Wait(Async11());
    private static async Task Async11()
    {
        var h = new RunHarness();
        var outcome = await h.SendAsync("/ok hi");
        Assert.Equal(RunStatus.Completed, outcome.Final!.Status);
        Assert.Equal(1, h.Probe.ProviderMarkersAtCall.Single()); // the marker was already "started" when the model ran
        Assert.True(outcome.Final.ProviderCallStarted);
    }

    // ---------------------------------------------------------------------------------------- failures & quota

    public static void FailuresKeepTheQuestionAndWriteNoReply() => Wait(Async12());
    private static async Task Async12()
    {
        var h = new RunHarness();
        foreach (var (script, reason) in new[] { ("/fail", "provider_error"), ("/blocked", "safety"), ("/empty", "empty_response") })
        {
            var conversation = Guid.NewGuid();
            var outcome = await h.SendAsync($"{script} x", conversationId: conversation);
            Assert.Equal(RunStatus.Failed, outcome.Final!.Status);
            Assert.Equal(reason, outcome.Final.Error!.Code);
            Assert.Equal(1, (await h.MessagesAsync(conversation)).Count); // question only
        }
    }

    public static void QuotaRejectionStoresTheQuestionAndNeverCallsTheModel() => Wait(Async13());
    private static async Task Async13()
    {
        var h = new RunHarness(dailyRequestLimit: 1);
        await h.SendAsync("/ok first");
        var conversation = Guid.NewGuid();
        var rejected = await h.SendAsync("/ok second", conversationId: conversation);

        var run = ((AdmitResult.Rejected)rejected.Result).Run;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("quota", run.Error!.Code);
        Assert.Equal(1, h.Probe.StreamCalls);
        Assert.Equal(1L, await h.UsageAsync("requests"));
        Assert.Equal(1, (await h.MessagesAsync(conversation)).Count);
        Assert.False((await h.ConversationAsync(conversation))!.ContainsKey("activeRunId"));
    }

    // ---------------------------------------------------------------------------------------- concurrency

    public static void ASecondRunInABusyConversationIsRefusedUntilTheFirstEnds() => Wait(Async14());
    private static async Task Async14()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        var firstId = RunHarness.NewRunId();
        var first = await h.Service.AdmitAsync(h.Request("/slow go", firstId, conversation), default);
        var events = Collector(out var emit);
        var execution = Task.Run(() => h.Service.ExecuteAsync(first, emit, CancellationToken.None));
        Assert.True(await RunHarness.EventuallyAsync(() => HasDelta(events)));

        var busy = await h.SendAsync("/ok second", conversationId: conversation);
        Assert.Equal(firstId, ((AdmitResult.Busy)busy.Result).ActiveRunId);
        Assert.Equal(1L, await h.UsageAsync("requests")); // the refused request reserved nothing

        await h.Service.StopAsync(h.User, firstId, default);
        await execution;

        var next = await h.SendAsync("/ok second", conversationId: conversation);
        Assert.Equal(RunStatus.Completed, next.Final!.Status);
    }

    public static void AStaleActiveRunIsClosedBeforeTheNextOneIsAdmitted() => Wait(Async15());
    private static async Task Async15()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        var staleId = RunHarness.NewRunId();
        Admitted(await h.Service.AdmitAsync(h.Request("/ok first", staleId, conversation), default));
        await Task.Delay((RunHarness.LeaseSeconds + 1) * 1000);

        var next = await h.SendAsync("/ok second", conversationId: conversation);
        Assert.Equal(RunStatus.Completed, next.Final!.Status);
        var stale = (await h.Store.GetAsync(h.User, staleId, default))!;
        Assert.Equal(RunStatus.Interrupted, stale.Status);
        Assert.Equal("server_lost", stale.StatusReason!);
    }

    // ---------------------------------------------------------------------------------------- context

    public static void OnlyAnsweredTurnsReachTheModel() => Wait(Async16());
    private static async Task Async16()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        await h.SendAsync("/ok first", conversationId: conversation);
        await h.SendAsync("/fail second", conversationId: conversation);   // never answered
        var third = await h.SendAsync("/ok third", conversationId: conversation);
        Assert.Equal(RunStatus.Completed, third.Final!.Status);

        var histories = h.Probe.Histories.ToArray();
        var seen = histories[^1];
        Assert.Equal(2, seen.Count);
        Assert.Equal("/ok first", seen[0].Content);
        Assert.Equal("Hello world", seen[1].Content);
    }
}
