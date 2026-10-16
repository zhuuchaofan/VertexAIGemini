using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Services.Attachments;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Quota;

// Integration tests against the Firestore emulator. They run only when FIRESTORE_EMULATOR_HOST is set (see
// docs/audit/phase0-release-readiness.md for the one-line way to start it) and never touch a real project.
internal static class EmulatorTests
{
    public static bool Available => !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"));

    public static (string Name, Action Test)[] All =>
    [
        ("Emulator: global request cap is atomic under 40 concurrent users", GlobalCapIsAtomicUnderConcurrency),
        ("Emulator: per-user cap is atomic under concurrency", PerUserCapIsAtomicUnderConcurrency),
        ("Emulator: contention profile of the global bucket", ContentionProfile),
        ("Emulator: a rejected reservation writes nothing to either bucket", RejectedReservationWritesNothing),
        ("Emulator: settlement releases the estimate and records actual usage", SettlementReleasesEstimate),
        ("Emulator: failed request settles to zero tokens", FailedRequestSettlesToZero),
        ("Emulator: settlement lands on the reservation day, not today", SettlementUsesReservationDay),
        ("Emulator: reservations released by settlement make room again", ReleasedReservationsMakeRoom),
        ("Emulator: conversation ownership is enforced for every operation", ConversationOwnershipIsEnforced),
        ("Emulator: message status round-trips and legacy messages read as completed", MessageStatusRoundTrips),
        ("Emulator: failed replies are shown but never fed back to the model", FailedRepliesAreNotHistory),
        ("Emulator: full orchestrator flow persists user + partial cancelled reply", OrchestratorFlowPersistsPartialReply),
    ];

    /// <summary>Wipes the emulator's data so every run starts from the same empty state.</summary>
    public static void Reset()
    {
        using var http = new HttpClient();
        var host = System.Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
        var response = http.DeleteAsync($"http://{host}/emulator/v1/projects/demo-release-readiness/databases/(default)/documents")
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    private static FirestoreDb Db() => new FirestoreDbBuilder
    {
        ProjectId = "demo-release-readiness",
        EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOnly
    }.Build();

    private static AuthenticatedUser NewUser() => new(Guid.NewGuid(), $"uid-{Guid.NewGuid():N}", "u@example.com");

    private static ChatQuotaRequest Request(AuthenticatedUser user, string day, int estimated = 100, int searches = 0) =>
        new(user, 1, estimated, searches, 0, day);

    private static FirestoreChatQuotaService Service(FirestoreDb db, QuotaSettings settings) =>
        new(db, Options.Create(settings));

    private static string UniqueDay(int seed) => $"2099{seed % 12 + 1:D2}{seed / 12 % 28 + 1:D2}";

    private static async Task<string> Outcome(Func<Task> action)
    {
        try
        {
            await action();
            return "ok";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("quota") || ex.Message.Contains("budget"))
        {
            return "limited";
        }
        catch (UserFacingChatException)
        {
            return "busy"; // contention outlasted the retries: fail closed with a clear message
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static async Task<long> Read(DocumentReference doc, string field)
    {
        var snapshot = await doc.GetSnapshotAsync();
        return snapshot.Exists && snapshot.TryGetValue<long>(field, out var value) ? value : 0;
    }

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Run(Task task) => task.GetAwaiter().GetResult();

    // ---- quota ----------------------------------------------------------------------------

    public static void GlobalCapIsAtomicUnderConcurrency()
    {
        var db = Db();
        var day = UniqueDay(1);
        var service = Service(db, new QuotaSettings
        {
            DailyRequestLimit = 1000, DailyTokenLimit = 10_000_000, DailySearchLimit = 1000,
            GlobalDailyRequestLimit = 5, GlobalDailyTokenLimit = 0, GlobalDailySearchLimit = 0
        });

        var users = Enumerable.Range(0, 40).Select(_ => NewUser()).ToList();
        var results = Run(Task.WhenAll(users.Select(user =>
            Outcome(() => service.CheckAndReserveAsync(Request(user, day))))));

        var ok = results.Count(r => r == "ok");
        var limited = results.Count(r => r == "limited");
        var busy = results.Count(r => r == "busy");
        var errors = results.Where(r => r.StartsWith("error")).ToList();
        System.Console.WriteLine($"    global cap=5, 40 concurrent users -> ok={ok} limited={limited} busy(clear message)={busy} raw-errors={errors.Count}");
        Assert.Equal(0, errors.Count); // contention must never surface as a raw RpcException

        var globalRequests = Run(Read(db.Collection("usageQuotaGlobal").Document(day), "requests"));
        // The invariant that matters for spend: never more than the cap, and the counter equals what was admitted.
        Assert.True(ok <= 5);
        Assert.Equal((long)ok, globalRequests);
        Assert.True(ok >= 1);
    }

    public static void ContentionProfile()
    {
        var db = Db();
        var seed = 20;
        foreach (var attempts in new[] { 25 })
        {
            foreach (var concurrency in new[] { 2, 5, 10 })
            {
                var day = UniqueDay(seed++);
                var service = Service(db, new QuotaSettings
                {
                    DailyRequestLimit = 1000, DailyTokenLimit = 10_000_000, DailySearchLimit = 1000,
                    GlobalDailyRequestLimit = 1000, GlobalDailyTokenLimit = 0, GlobalDailySearchLimit = 0,
                    TransactionMaxAttempts = attempts
                });
                var started = System.Diagnostics.Stopwatch.StartNew();
                var results = Run(Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ =>
                {
                    var user = NewUser();
                    return Outcome(() => service.CheckAndReserveAsync(Request(user, day)));
                })));
                var ok = results.Count(r => r == "ok");
                System.Console.WriteLine($"    attempts={attempts,2} concurrent={concurrency,2} -> ok={ok,2}/{concurrency} in {started.ElapsedMilliseconds}ms");
                // Whatever the contention, the counter must equal the number admitted.
                Assert.Equal((long)ok, Run(Read(db.Collection("usageQuotaGlobal").Document(day), "requests")));
            }
        }
    }

    public static void PerUserCapIsAtomicUnderConcurrency()
    {
        var db = Db();
        var day = UniqueDay(2);
        var service = Service(db, new QuotaSettings
        {
            DailyRequestLimit = 3, DailyTokenLimit = 10_000_000, DailySearchLimit = 1000,
            GlobalDailyRequestLimit = 0, GlobalDailyTokenLimit = 0, GlobalDailySearchLimit = 0
        });
        var user = NewUser();

        var results = Run(Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Outcome(() => service.CheckAndReserveAsync(Request(user, day))))));

        var ok = results.Count(r => r == "ok");
        System.Console.WriteLine($"    user cap=3, 12 concurrent requests -> ok={ok} limited={results.Count(r => r == "limited")} errors={results.Count(r => r.StartsWith("error"))}");
        Assert.True(ok <= 3);
        Assert.True(ok >= 1);
        var doc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(user), day));
        Assert.Equal((long)ok, Run(Read(doc, "requests")));
    }

    public static void RejectedReservationWritesNothing()
    {
        var db = Db();
        var day = UniqueDay(3);
        var service = Service(db, new QuotaSettings
        {
            DailyRequestLimit = 1000, DailyTokenLimit = 10_000_000, DailySearchLimit = 1000,
            GlobalDailyRequestLimit = 1, GlobalDailyTokenLimit = 0, GlobalDailySearchLimit = 0
        });
        var first = NewUser();
        var second = NewUser();

        Run(service.CheckAndReserveAsync(Request(first, day)));
        var outcome = Run(Outcome(() => service.CheckAndReserveAsync(Request(second, day))));
        Assert.Equal("limited", outcome);

        // Atomic across the two documents: the rejected user's own counter must not have moved.
        var secondDoc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(second), day));
        Assert.Equal(0L, Run(Read(secondDoc, "requests")));
        Assert.Equal(1L, Run(Read(db.Collection("usageQuotaGlobal").Document(day), "requests")));
    }

    public static void SettlementReleasesEstimate()
    {
        var db = Db();
        var day = UniqueDay(4);
        var service = Service(db, new QuotaSettings());
        var user = NewUser();
        var request = Request(user, day, estimated: 1000);

        Run(service.CheckAndReserveAsync(request));
        var userDoc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(user), day));
        var globalDoc = db.Collection("usageQuotaGlobal").Document(day);
        Assert.Equal(1000L, Run(Read(userDoc, "estimatedTokens")));

        Run(service.RecordTokenUsageAsync(request, 400));

        Assert.Equal(0L, Run(Read(userDoc, "estimatedTokens")));
        Assert.Equal(400L, Run(Read(userDoc, "actualTokens")));
        Assert.Equal(0L, Run(Read(globalDoc, "estimatedTokens")));
        Assert.Equal(400L, Run(Read(globalDoc, "actualTokens")));
    }

    public static void FailedRequestSettlesToZero()
    {
        var db = Db();
        var day = UniqueDay(5);
        var service = Service(db, new QuotaSettings());
        var user = NewUser();
        var request = Request(user, day, estimated: 700);

        Run(service.CheckAndReserveAsync(request));
        Run(service.RecordTokenUsageAsync(request, 0));

        var userDoc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(user), day));
        Assert.Equal(0L, Run(Read(userDoc, "estimatedTokens")));
        Assert.Equal(0L, Run(Read(userDoc, "actualTokens")));
        Assert.Equal(1L, Run(Read(userDoc, "requests"))); // the request itself still counts
    }

    public static void SettlementUsesReservationDay()
    {
        var db = Db();
        var reservedDay = UniqueDay(6);
        var service = Service(db, new QuotaSettings());
        var user = NewUser();
        var request = Request(user, reservedDay, estimated: 500);

        Run(service.CheckAndReserveAsync(request));
        Run(service.RecordTokenUsageAsync(request, 200));

        var reservedDoc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(user), reservedDay));
        var todayDoc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(user), FirestoreChatQuotaService.TodayDateKey()));
        Assert.Equal(200L, Run(Read(reservedDoc, "actualTokens")));
        Assert.Equal(0L, Run(Read(reservedDoc, "estimatedTokens")));
        Assert.False(Run(todayDoc.GetSnapshotAsync()).Exists); // nothing leaked onto another day
    }

    public static void ReleasedReservationsMakeRoom()
    {
        var db = Db();
        var day = UniqueDay(7);
        var service = Service(db, new QuotaSettings { DailyRequestLimit = 1000, DailyTokenLimit = 1000, GlobalDailyRequestLimit = 0, GlobalDailyTokenLimit = 0, GlobalDailySearchLimit = 0 });
        var user = NewUser();

        var a = Request(user, day, estimated: 800);
        Run(service.CheckAndReserveAsync(a));
        // A second 800-token request does not fit while the first reservation is outstanding...
        Assert.Equal("limited", Run(Outcome(() => service.CheckAndReserveAsync(Request(user, day, estimated: 800)))));
        // ...but after the first ends cheaply (100 real tokens) it does.
        Run(service.RecordTokenUsageAsync(a, 100));
        Assert.Equal("ok", Run(Outcome(() => service.CheckAndReserveAsync(Request(user, day, estimated: 800)))));
    }

    // ---- conversation store ---------------------------------------------------------------

    private static FirestoreConversationStore Store(FirestoreDb db) => new(db, new NoOpChatAttachmentStore());

    public static void ConversationOwnershipIsEnforced()
    {
        var db = Db();
        var store = Store(db);
        var owner = NewUser();
        var intruder = NewUser();

        var conversation = Run(store.CreateConversationAsync(owner, "gemini", "gemini-3.5-flash", "default"))!;
        Run(store.AddMessageAsync(conversation.Id, owner, "user", "secret question"));

        Assert.Null(Run(store.GetConversationAsync(conversation.Id, intruder)));
        Assert.Null(Run(store.AddMessageAsync(conversation.Id, intruder, "user", "injected")));
        Run(store.UpdateTitleAsync(conversation.Id, intruder, "hijacked"));
        Run(store.DeleteConversationAsync(conversation.Id, intruder));
        Assert.Equal(0, Run(store.GetHistoryAsync(conversation.Id, intruder, 10)).Count);
        Assert.Equal(0, Run(store.GetUserConversationsAsync(intruder, 0, 10)).Count);

        var still = Run(store.GetConversationAsync(conversation.Id, owner))!;
        Assert.Equal(1, still.Messages.Count);
        Assert.Equal("secret question", still.Messages.Single().Content);
        Assert.True(still.Title != "hijacked");
    }

    public static void MessageStatusRoundTrips()
    {
        var db = Db();
        var store = Store(db);
        var user = NewUser();
        var conversation = Run(store.CreateConversationAsync(user, "gemini", "gemini-3.5-flash", "default"))!;

        // A message written by the previous version of the app: no status field at all.
        var legacyId = Guid.NewGuid();
        Run(db.Collection("messages").Document(legacyId.ToString("D")).SetAsync(new Dictionary<string, object?>
        {
            ["uid"] = user.FirebaseUid, ["localUserId"] = user.LocalUserId.ToString("D"),
            ["conversationId"] = conversation.Id.ToString("D"), ["role"] = "model", ["content"] = "old answer",
            ["thinkingContent"] = null, ["attachmentsJson"] = null,
            ["createdAt"] = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-5))
        }));

        Run(store.AddMessageAsync(conversation.Id, user, "user", "q"));
        Run(store.AddMessageAsync(conversation.Id, user, "model", "partial", status: ChatStatuses.Cancelled));
        Run(store.AddMessageAsync(conversation.Id, user, "model", "cut", status: ChatStatuses.Truncated));

        var loaded = Run(store.GetConversationAsync(conversation.Id, user))!;
        var byContent = loaded.Messages.ToDictionary(m => m.Content);
        Assert.Null(byContent["old answer"].Status); // reads as completed
        Assert.Equal(ChatStatuses.Cancelled, byContent["partial"].Status);
        Assert.Equal(ChatStatuses.Truncated, byContent["cut"].Status);
        Assert.Equal(4, loaded.Messages.Count);
    }

    public static void FailedRepliesAreNotHistory()
    {
        var db = Db();
        var store = Store(db);
        var user = NewUser();
        var conversation = Run(store.CreateConversationAsync(user, "gemini", "gemini-3.5-flash", "default"))!;

        Run(store.AddMessageAsync(conversation.Id, user, "user", "q1"));
        Run(store.AddMessageAsync(conversation.Id, user, "model", "good answer"));
        Run(store.AddMessageAsync(conversation.Id, user, "user", "q2"));
        Run(store.AddMessageAsync(conversation.Id, user, "model", "half of a failed ans", status: ChatStatuses.Failed));
        Run(store.AddMessageAsync(conversation.Id, user, "user", "q3"));
        Run(store.AddMessageAsync(conversation.Id, user, "model", "stopped early", status: ChatStatuses.Cancelled));

        var history = Run(store.GetHistoryAsync(conversation.Id, user, 20));
        var contents = history.Select(h => h.Content).ToList();
        Assert.False(contents.Contains("half of a failed ans"));
        Assert.True(contents.Contains("good answer"));
        Assert.True(contents.Contains("stopped early")); // cancelled partials are legitimate context
        // The shown conversation still includes the failed one for the user.
        Assert.True(Run(store.GetConversationAsync(conversation.Id, user))!.Messages.Any(m => m.Content == "half of a failed ans"));
    }

    public static void OrchestratorFlowPersistsPartialReply()
    {
        var db = Db();
        var store = Store(db);
        var user = NewUser();
        var day = UniqueDay(8);
        var quota = Service(db, new QuotaSettings());
        using var cts = new CancellationTokenSource();
        var model = new FakeChatModelClient { Chunks = [new ChatChunk { Text = "part one " }, new ChatChunk { Text = "part two " }] };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model), store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatOrchestrator>.Instance, quota: quota);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, user, "hello", []),
            update =>
            {
                if (update.Content.Contains("part one")) cts.Cancel();
                return Task.CompletedTask;
            },
            cts.Token));

        Assert.Equal(ChatStatuses.Cancelled, result.Status);
        var saved = Run(store.GetConversationAsync(result.ConversationId!.Value, user))!;
        Assert.Equal(2, saved.Messages.Count);
        Assert.Equal("hello", saved.Messages.First(m => m.Role == "user").Content);
        var reply = saved.Messages.Single(m => m.Role == "model");
        Assert.Equal(ChatStatuses.Cancelled, reply.Status);
        Assert.Equal("part one ", reply.Content);

        // Quota ended settled: no outstanding reservation is left behind for today.
        var userDoc = db.Collection("usageQuotas").Document(FirestoreChatQuotaService.DailyDocumentId(FirestoreChatQuotaService.ResolveUserId(user), FirestoreChatQuotaService.TodayDateKey()));
        Assert.Equal(0L, Run(Read(userDoc, "estimatedTokens")));
        Assert.Equal(1L, Run(Read(userDoc, "requests")));
        _ = day;
    }
}
