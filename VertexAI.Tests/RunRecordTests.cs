using VertexAI.Api;
using VertexAI.Services.Runs;

/// <summary>
/// What a run keeps about itself: the settings that were really applied, how it ended, and token usage. Anything
/// the provider did not tell us stays unknown instead of becoming zero, and an estimate is always labelled.
/// </summary>
internal static class RunRecordTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    public static void ACompletedRunKeepsWhatTheProviderReported() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var outcome = await h.SendAsync("/usage go", runId);
        Assert.Equal(RunStatus.Completed, outcome.Final!.Status);

        var record = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal("provider", record.Usage!.Source);
        Assert.Equal(11, record.Usage.InputTokens!.Value);
        Assert.Equal(22, record.Usage.OutputTokens!.Value);
        Assert.Equal(3, record.Usage.ThoughtsTokens!.Value);
        Assert.Equal(36, record.Usage.TotalTokens!.Value);
        Assert.Equal("STOP", record.FinishReason!);
        Assert.Equal("scripted-1", record.ModelVersion!);
        Assert.Equal("resp-1", record.ResponseId!);
        Assert.Equal(1, record.ProviderCalls!.Generate);
        Assert.Equal(0, record.ProviderCalls.Summarize);
        Assert.Equal(11, record.OutputChars);
        Assert.NotNull(record.FirstOutputAt);
        Assert.NotNull(record.ProviderCallStartedAt);
        Assert.True(record.FirstOutputAt >= record.ProviderCallStartedAt);

        // Effective settings are what the client applied (resolved), next to what was asked for.
        Assert.Equal("scripted", record.Effective!.ModelName);
        Assert.Equal("medium", record.Effective.ThinkingLevel!);
        Assert.Equal(0.9, record.Effective.TopP!.Value);
        Assert.Equal(8192, record.Effective.MaxOutputTokens!.Value);
        Assert.Equal("BlockMediumAndAbove", record.Effective.SafetyThreshold!);
        Assert.Equal("auto", record.Effective.SearchMode);
        Assert.Equal("scripted", record.Requested!.ModelName);

        var snapshot = RunSnapshot.From(record);
        Assert.Equal("STOP", snapshot.FinishReason!);
        Assert.Equal(11, snapshot.Output!.Chars);
    }

    public static void AStoppedRunKeepsAnEstimateLabelledAsOneAndLeavesTheRestUnknown() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var admission = await h.Service.AdmitAsync(h.Request("/slow go", runId), default);
        var events = new List<RunEvent>();
        var execution = Task.Run(() => h.Service.ExecuteAsync(admission, e => { lock (events) events.Add(e); return Task.CompletedTask; }, CancellationToken.None));
        Assert.True(await RunHarness.EventuallyAsync(() => { lock (events) return events.OfType<RunEvent.Delta>().Count() >= 2; }));
        await h.Service.StopAsync(h.User, runId, default);
        await execution;

        var record = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal(RunStatus.Stopped, record.Status);
        Assert.Equal("estimate", record.Usage!.Source);
        Assert.Null(record.Usage.InputTokens);
        Assert.Null(record.Usage.TotalTokens);
        Assert.True(record.Usage.OutputTokens > 0);
        Assert.Null(record.FinishReason);
        Assert.Null(record.ModelVersion);
        Assert.Equal(1, record.ProviderCalls!.Generate);
        Assert.NotNull(record.Effective); // written before the provider was called, so it survives a stop
    }

    public static void ARunThatNeverReachedTheProviderHasNoEffectiveUsageOrCalls() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness(dailyRequestLimit: 1);
        await h.SendAsync("/ok first");
        var rejectedId = RunHarness.NewRunId();
        var rejected = await h.SendAsync("/ok second", rejectedId);
        Assert.True(rejected.Result is AdmitResult.Rejected);

        var record = (await h.Store.GetAsync(h.User, rejectedId, default))!;
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Null(record.Effective);
        Assert.Null(record.Usage);
        Assert.Null(record.ProviderCalls);
        Assert.Null(record.FirstOutputAt);
        Assert.Null(record.ProviderCallStartedAt);
        Assert.NotNull(record.Requested); // what was asked for is always known
    }

    public static void AFailedRunWithPartialTextStillKeepsItsEndingAndCounts() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        var runId = RunHarness.NewRunId();
        var outcome = await h.SendAsync("/fail now", runId);
        Assert.Equal(RunStatus.Failed, outcome.Final!.Status);
        var record = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal(1, record.ProviderCalls!.Generate);
        Assert.Null(record.Usage); // nothing was produced and the provider reported nothing
        Assert.Null(record.FinishReason);
        Assert.NotNull(record.Effective);
    }
}
