using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Services.Attachments;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Quota;
using VertexAI.Services.Runs;

/// <summary>What the scripted model did, observed from outside.</summary>
internal sealed class ModelProbe
{
    private int _streamCalls;
    private long _cancelObservedAt;

    public int StreamCalls => Volatile.Read(ref _streamCalls);
    public ConcurrentQueue<IReadOnlyList<ChatHistoryEntry>> Histories { get; } = new();
    public ConcurrentQueue<int> ProviderMarkersAtCall { get; } = new();
    public SemaphoreSlim Gate { get; } = new(0);
    public Func<Task<int>>? CountStartedMarkers { get; set; }
    public long CancelObservedTicks => Interlocked.Read(ref _cancelObservedAt);

    public void RecordStreamCall() => Interlocked.Increment(ref _streamCalls);
    public void RecordCancel() => Interlocked.CompareExchange(ref _cancelObservedAt, Stopwatch.GetTimestamp(), 0);
}

/// <summary>
/// A model whose behavior is chosen by the first word of the message. Everything around it (run service, run store,
/// quota, conversation store) is the real implementation.
///   /ok  streams two chunks and ends        /slow  streams a line every 50 ms until cancelled
///   /gate  streams one chunk then waits for ModelProbe.Gate        /fail  provider error
///   /blocked  safety block                  /empty  ends with no text
/// </summary>
internal sealed class ScriptedModelClient(ModelProbe probe) : IChatModelClient
{
    private IReadOnlyList<ChatHistoryEntry> _history = [];

    public int CurrentTokenCount { get; private set; }
    public string CurrentModelName => "scripted";
    public string CurrentPresetId => "default";
    public string CurrentCustomPrompt => "";
    public IReadOnlyList<ChatModelOption> ModelOptions { get; } = [new() { Name = "Scripted", ModelName = "scripted" }];
    public IReadOnlyList<PromptPresetConfig> Presets { get; } = [new() { Id = "default", Name = "Default" }];

    public Task ConfigureAsync(ChatSessionOptions? options) => Task.CompletedTask;

    public Task LoadHistoryAsync(IReadOnlyCollection<ChatHistoryEntry> messages)
    {
        _history = messages.ToList();
        probe.Histories.Enqueue(_history);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request) =>
        StreamChatAsync(request, CancellationToken.None);

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        probe.RecordStreamCall();
        if (probe.CountStartedMarkers is { } count) probe.ProviderMarkersAtCall.Enqueue(await count());

        var script = request.Message.Split(' ', 2)[0];
        CurrentTokenCount = 42;
        switch (script)
        {
            case "/slow":
                for (var line = 1; line <= 400; line++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new ChatChunk { Text = $"line {line}\n" };
                    try { await Task.Delay(50, cancellationToken); }
                    catch (OperationCanceledException) { probe.RecordCancel(); throw; }
                }

                yield break;

            case "/gate":
                yield return new ChatChunk { Text = "first " };
                try { await probe.Gate.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { probe.RecordCancel(); throw; }
                yield return new ChatChunk { Text = "second" };
                yield break;

            case "/fail":
                throw new InvalidOperationException("Gemini ServerError: upstream unavailable");

            case "/blocked":
                throw new InvalidOperationException("Gemini response was blocked by safety settings: SAFETY");

            case "/empty":
                yield break;

            default:
                yield return new ChatChunk { Text = "Hello " };
                yield return new ChatChunk { Text = "world" };
                yield break;
        }
    }
}

internal sealed class ScriptedCatalog(ModelProbe probe) : IChatProviderCatalog
{
    public IReadOnlyList<IChatModelProvider> Providers { get; } = [];
    public string ResolveProviderId(string? providerId) => providerId ?? "scripted";
    public IChatModelClient CreateClient(string? providerId) => new ScriptedModelClient(probe);
}

internal sealed record RunOutcome(AdmitResult Result, List<RunEvent> Events, RunRecord? Final);

/// <summary>
/// The real run service on the real Firestore run store, quota service and conversation store, against the emulator.
/// Each harness gets its own Firestore project id, so tests cannot see each other's data.
/// </summary>
internal sealed class RunHarness
{
    public const int LeaseSeconds = 3;

    public FirestoreDb Db { get; }
    public ModelProbe Probe { get; } = new();
    public RunSettings Settings { get; }
    public RunTestHooks Hooks { get; } = new();
    public FirestoreRunStore Store { get; }
    public FirestoreChatQuotaService Quota { get; }
    public RunService Service { get; }
    public FirestoreConversationStore Conversations { get; }
    public AuthenticatedUser User { get; } = new(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "tester@example.test");

    public RunHarness(int dailyRequestLimit = 1000, Action<RunSettings>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")))
            throw new TestSkippedException("FIRESTORE_EMULATOR_HOST is not set");

        Db = new FirestoreDbBuilder
        {
            ProjectId = "demo-run-" + Guid.NewGuid().ToString("N"),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.Build();

        Settings = new RunSettings
        {
            Enabled = true,
            LeaseSeconds = LeaseSeconds,
            HeartbeatSeconds = 1,
            StopPollMilliseconds = 100,
            DisconnectGraceMilliseconds = 400,
            MaxDurationSeconds = 60,
            FinalizeTimeoutSeconds = 10
        };
        configure?.Invoke(Settings);

        Quota = new FirestoreChatQuotaService(Db, Options.Create(new QuotaSettings
        {
            Enabled = true,
            DailyRequestLimit = dailyRequestLimit,
            DailyTokenLimit = 10_000_000,
            DailySearchLimit = 1000,
            DailyAttachmentBytesLimit = 1L << 40,
            AdminBypassEnabled = false
        }));
        Store = new FirestoreRunStore(Db, Quota, Options.Create(Settings));
        Conversations = new FirestoreConversationStore(Db, new NoOpChatAttachmentStore());
        Service = NewService(new RunCancellationRegistry());

        Probe.CountStartedMarkers = async () =>
        {
            var started = await Db.Collection(FirestoreRunStore.RunsCollection).WhereEqualTo("providerCall", "started").GetSnapshotAsync();
            return started.Count;
        };
    }

    /// <summary>Another instance of the API: same storage, its own in-process stop registry.</summary>
    public RunService NewService(RunCancellationRegistry registry) => new(
        new ScriptedCatalog(Probe), Store, Options.Create(Settings), registry,
        NullLogger<RunService>.Instance, hooks: Hooks);

    public static string NewRunId() => Guid.NewGuid().ToString("D");

    public RunStartRequest Request(string message, string? runId = null, Guid? conversationId = null, AuthenticatedUser? user = null) =>
        new(user ?? User, runId ?? NewRunId(), conversationId ?? Guid.NewGuid(), message, null, new ChatSessionOptions());

    /// <summary>Admits and, if admitted, executes to the end, collecting every emitted event.</summary>
    public async Task<RunOutcome> SendAsync(
        string message, string? runId = null, Guid? conversationId = null,
        CancellationToken connection = default, AuthenticatedUser? user = null, RunService? service = null)
    {
        service ??= Service;
        var admission = await service.AdmitAsync(Request(message, runId, conversationId, user), connection);
        var events = new List<RunEvent>();
        if (admission.Result is AdmitResult.Admitted)
        {
            await service.ExecuteAsync(admission, e => { lock (events) events.Add(e); return Task.CompletedTask; }, connection);
        }

        var done = events.OfType<RunEvent.Done>().LastOrDefault()?.Run;
        return new RunOutcome(admission.Result, events, done);
    }

    public async Task<List<Dictionary<string, object>>> MessagesAsync(Guid conversationId)
    {
        var snapshot = await Db.Collection("messages")
            .WhereEqualTo("conversationId", conversationId.ToString("D")).GetSnapshotAsync();
        return snapshot.Documents.OrderBy(d => d.GetValue<Timestamp>("createdAt")).Select(d => d.ToDictionary()).ToList();
    }

    public async Task<Dictionary<string, object>?> ConversationAsync(Guid conversationId)
    {
        var snapshot = await Db.Collection("conversations").Document(conversationId.ToString("D")).GetSnapshotAsync();
        return snapshot.Exists ? snapshot.ToDictionary() : null;
    }

    public async Task<long> UsageAsync(string field, AuthenticatedUser? user = null)
    {
        var snapshot = await Quota.DailyDocumentFor(user ?? User).GetSnapshotAsync();
        if (!snapshot.Exists || !snapshot.ContainsField(field)) return 0;
        return snapshot.GetValue<long>(field);
    }

    public static async Task<bool> EventuallyAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }
}
