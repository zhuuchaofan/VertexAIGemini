using Google.Api.Gax;
using Google.Cloud.Firestore;

internal sealed class TestSkippedException(string reason) : Exception(reason);

/// <summary>
/// Contract tests for the Firestore primitives the run lifecycle depends on (create-if-absent,
/// single-winner transactions, compare-and-set finalization). They run against the Firestore emulator
/// and are skipped when FIRESTORE_EMULATOR_HOST is not set, so a plain `dotnet run` still works locally.
/// </summary>
internal static class FirestoreEmulatorContractTests
{
    private static FirestoreDb Connect()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")))
            throw new TestSkippedException("FIRESTORE_EMULATOR_HOST is not set");

        return new FirestoreDbBuilder
        {
            ProjectId = "demo-contract-tests",
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.Build();
    }

    private static DocumentReference NewDocument(FirestoreDb db) =>
        db.Collection("contract-runs").Document(Guid.NewGuid().ToString("D"));

    public static void CreateFailsWhenTheDocumentAlreadyExists()
    {
        var db = Connect();
        var doc = NewDocument(db);
        doc.CreateAsync(new Dictionary<string, object> { ["owner"] = "first" }).GetAwaiter().GetResult();

        var failed = false;
        try
        {
            doc.CreateAsync(new Dictionary<string, object> { ["owner"] = "second" }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex.ToString().Contains("AlreadyExists"))
        {
            failed = true;
        }

        Assert.True(failed);
        Assert.Equal("first", doc.GetSnapshotAsync().GetAwaiter().GetResult().GetValue<string>("owner"));
    }

    /// <summary>
    /// The admission protocol: one transaction decides, and ANY failure of that transaction (lock timeout,
    /// aborted retries, deadline) is treated as "unknown", never as "lost". The executor id written by the
    /// winner is read back to find out whether this attempt's commit actually happened.
    /// </summary>
    private enum Admission { Admitted, Replay, NotAdmitted }

    private static async Task<(Admission Outcome, bool TransactionFailed)> Admit(
        FirestoreDb db, DocumentReference doc, string executorId)
    {
        try
        {
            // The callback can be re-run by the SDK on contention, so it must not have side effects;
            // the outcome is decided only from the committed result.
            var admitted = await db.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(doc);
                if (snapshot.Exists) return snapshot.GetValue<string>("executor") == executorId;
                transaction.Create(doc, new Dictionary<string, object> { ["executor"] = executorId, ["status"] = "running" });
                return true;
            });
            return (admitted ? Admission.Admitted : Admission.Replay, false);
        }
        catch (Exception)
        {
            var readBack = await doc.GetSnapshotAsync();
            if (!readBack.Exists) return (Admission.NotAdmitted, true);
            return (readBack.GetValue<string>("executor") == executorId ? Admission.Admitted : Admission.Replay, true);
        }
    }

    public static void ConcurrentAdmissionHasExactlyOneWinner()
    {
        var db = Connect();
        var doc = NewDocument(db);
        const int contenders = 50;
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<(string Executor, Admission Outcome, bool Failed)>();

        Task.WaitAll(Enumerable.Range(0, contenders).Select(contender => Task.Run(async () =>
        {
            var executor = $"exec-{contender}";
            var (outcome, failed) = await Admit(db, doc, executor);
            outcomes.Add((executor, outcome, failed));
        })).ToArray());

        var winners = outcomes.Where(o => o.Outcome == Admission.Admitted).ToList();
        Assert.Equal(1, winners.Count);
        var stored = doc.GetSnapshotAsync().GetAwaiter().GetResult();
        Assert.Equal(winners.Single().Executor, stored.GetValue<string>("executor"));
        // Nobody who lost may believe it is the owner.
        Assert.True(outcomes.Where(o => o.Outcome != Admission.Admitted).All(o => o.Executor != stored.GetValue<string>("executor")));
        // The point of the read-back: even contenders whose transaction failed reach a definite answer
        // whenever the run exists. Those that find no run are told to retry with the same run id.
        Console.WriteLine($"  contenders={contenders} admitted=1 transactionFailures={outcomes.Count(o => o.Failed)} notAdmitted={outcomes.Count(o => o.Outcome == Admission.NotAdmitted)}");
    }

    public static void AdmissionRetryWithTheSameExecutorIsRecognisedAsAdmitted()
    {
        // The commit succeeded but the acknowledgement was lost: the retry must see itself as the admitter,
        // not as a replay, otherwise nobody would ever execute the run.
        var db = Connect();
        var doc = NewDocument(db);
        var first = Admit(db, doc, "exec-A").GetAwaiter().GetResult();
        Assert.Equal(Admission.Admitted, first.Outcome);
        var again = Admit(db, doc, "exec-A").GetAwaiter().GetResult();
        Assert.Equal(Admission.Admitted, again.Outcome);
        var other = Admit(db, doc, "exec-B").GetAwaiter().GetResult();
        Assert.Equal(Admission.Replay, other.Outcome);
    }

    public static void ConcurrentFinalizersAreCompareAndSet()
    {
        var db = Connect();
        var doc = NewDocument(db);
        doc.SetAsync(new Dictionary<string, object> { ["status"] = "running", ["owner"] = "exec-1" }).GetAwaiter().GetResult();

        // Terminal writers (executor, stop, reconciler) all use "only if still running".
        var outcomes = new[] { "completed", "stopped", "interrupted", "failed", "completed", "interrupted" };
        var winners = new System.Collections.Concurrent.ConcurrentBag<string>();
        Task.WaitAll(outcomes.Select((outcome, writer) => Task.Run(async () =>
        {
            // Under contention the emulator may give up on a transaction ("lock timeout"). That says nothing about whether
            // it committed, so, like the real protocol, the writer reads the state back and tries again. Each writer
            // signs its write, so "did mine land?" has an answer.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var won = await db.RunTransactionAsync(async transaction =>
                    {
                        var snapshot = await transaction.GetSnapshotAsync(doc);
                        if (snapshot.GetValue<string>("status") != "running") return false;
                        transaction.Update(doc, new Dictionary<string, object> { ["status"] = outcome, ["by"] = writer });
                        return true;
                    });
                    if (won) winners.Add(outcome);
                    return;
                }
                catch (Exception) when (attempt < 8)
                {
                    var current = await doc.GetSnapshotAsync();
                    if (current.TryGetValue<long>("by", out var by) && by == writer)
                    {
                        winners.Add(outcome);
                        return;
                    }

                    await Task.Delay(Random.Shared.Next(30, 200));
                }
            }
        })).ToArray());

        Assert.Equal(1, winners.Count);
        Assert.Equal(winners.Single(), doc.GetSnapshotAsync().GetAwaiter().GetResult().GetValue<string>("status"));
    }

    public static void LeaseOwnerFencesStaleWriters()
    {
        var db = Connect();
        var doc = NewDocument(db);
        doc.SetAsync(new Dictionary<string, object> { ["status"] = "running", ["leaseOwner"] = "exec-1", ["heartbeat"] = 1 }).GetAwaiter().GetResult();

        bool Heartbeat(string executor, int beat) => db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(doc);
            if (snapshot.GetValue<string>("status") != "running" || snapshot.GetValue<string>("leaseOwner") != executor) return false;
            transaction.Update(doc, new Dictionary<string, object> { ["heartbeat"] = beat });
            return true;
        }).GetAwaiter().GetResult();

        Assert.True(Heartbeat("exec-1", 2));
        // A reconciler finalizes the run; the original executor is now a zombie.
        doc.UpdateAsync(new Dictionary<string, object> { ["status"] = "interrupted" }).GetAwaiter().GetResult();
        Assert.False(Heartbeat("exec-1", 3));
        Assert.Equal(2L, doc.GetSnapshotAsync().GetAwaiter().GetResult().GetValue<long>("heartbeat"));
    }

    // ---- execution right: the provider-call marker -------------------------------------------------

    private static Task<bool> BeginProviderCall(FirestoreDb db, DocumentReference doc, string executor) =>
        db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(doc);
            // Only the lease holder of a still-running run may start talking to the provider, and only once.
            if (snapshot.GetValue<string>("status") != "running"
                || snapshot.GetValue<string>("leaseOwner") != executor
                || snapshot.GetValue<string>("providerCall") != "not_started") return false;
            transaction.Update(doc, new Dictionary<string, object> { ["providerCall"] = "started" });
            return true;
        });

    private static Task<bool> Finalize(FirestoreDb db, DocumentReference doc, string status) =>
        db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(doc);
            if (snapshot.GetValue<string>("status") != "running") return false;
            transaction.Update(doc, new Dictionary<string, object> { ["status"] = status });
            return true;
        });

    private static DocumentReference RunningRun(FirestoreDb db, string executor)
    {
        var doc = NewDocument(db);
        doc.SetAsync(new Dictionary<string, object>
        {
            ["status"] = "running", ["leaseOwner"] = executor, ["providerCall"] = "not_started"
        }).GetAwaiter().GetResult();
        return doc;
    }

    public static void ProviderCallMarkerCanBeSetOnlyOnceByTheLeaseHolder()
    {
        var db = Connect();
        var doc = RunningRun(db, "exec-1");
        Assert.False(BeginProviderCall(db, doc, "someone-else").GetAwaiter().GetResult());
        Assert.True(BeginProviderCall(db, doc, "exec-1").GetAwaiter().GetResult());
        Assert.False(BeginProviderCall(db, doc, "exec-1").GetAwaiter().GetResult());
    }

    public static void ReconcilerWinningBeforeTheMarkerMeansZeroProviderCalls()
    {
        var db = Connect();
        var doc = RunningRun(db, "exec-1");
        // The lease expired and a reconciler finalized the run while the executor was paused after admission.
        Assert.True(Finalize(db, doc, "interrupted").GetAwaiter().GetResult());
        Assert.False(BeginProviderCall(db, doc, "exec-1").GetAwaiter().GetResult());
        // "not_started" is durable proof that no provider call was ever made.
        Assert.Equal("not_started", doc.GetSnapshotAsync().GetAwaiter().GetResult().GetValue<string>("providerCall"));
    }

    public static void MarkerAndFinalizeRaceNeverLeavesAnUnrecordedProviderCall()
    {
        var db = Connect();
        for (var round = 0; round < 10; round++)
        {
            var doc = RunningRun(db, "exec-1");
            var begin = Task.Run(() => BeginProviderCall(db, doc, "exec-1"));
            var finalize = Task.Run(() => Finalize(db, doc, "interrupted"));
            Task.WaitAll(begin, finalize);
            var final = doc.GetSnapshotAsync().GetAwaiter().GetResult();
            // Whatever order won, the executor may call the provider if and only if the marker says started.
            Assert.Equal(begin.Result ? "started" : "not_started", final.GetValue<string>("providerCall"));
            Assert.Equal("interrupted", final.GetValue<string>("status"));
        }
    }

    // ---- retry reuses the user message and has a single successor ---------------------------------

    private static Task<string> AdmitRetry(FirestoreDb db, DocumentReference source, string newRunId) =>
        db.RunTransactionAsync(async transaction =>
        {
            var newRun = db.Collection("contract-runs").Document(newRunId);
            if ((await transaction.GetSnapshotAsync(newRun)).Exists) return "replay";
            var src = await transaction.GetSnapshotAsync(source);
            if (src.GetValue<string>("status") == "running") return "source_not_terminal";
            if (src.GetValue<bool>("hasAssistantMessage")) return "source_has_content";
            if (src.ContainsField("retriedByRunId") && src.GetValue<string>("retriedByRunId") is { Length: > 0 }) return "already_retried";
            transaction.Create(newRun, new Dictionary<string, object>
            {
                ["status"] = "running", ["trigger"] = "retry", ["retryOfRunId"] = source.Id,
                ["userMessageId"] = src.GetValue<string>("userMessageId"), ["attempt"] = src.GetValue<long>("attempt") + 1
            });
            transaction.Update(source, new Dictionary<string, object> { ["retriedByRunId"] = newRunId });
            return "admitted";
        });

    public static void ConcurrentRetriesOfOneFailedRunHaveASingleSuccessor()
    {
        var db = Connect();
        var source = NewDocument(db);
        source.SetAsync(new Dictionary<string, object>
        {
            ["status"] = "failed", ["hasAssistantMessage"] = false, ["userMessageId"] = "msg-1", ["attempt"] = 1L
        }).GetAwaiter().GetResult();

        var runIds = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        Task.WaitAll(runIds.Select(id => Task.Run(async () =>
        {
            string outcome;
            try { outcome = await AdmitRetry(db, source, id); }
            catch (Exception)
            {
                // Unknown outcome: read back, exactly like admission does.
                var linked = (await source.GetSnapshotAsync()).ContainsField("retriedByRunId")
                    && (await source.GetSnapshotAsync()).GetValue<string>("retriedByRunId") == id;
                outcome = linked ? "admitted" : "unknown";
            }
            results[id] = outcome;
        })).ToArray());

        var winners = results.Where(r => r.Value == "admitted").Select(r => r.Key).ToList();
        Assert.Equal(1, winners.Count);
        var stored = source.GetSnapshotAsync().GetAwaiter().GetResult();
        Assert.Equal(winners.Single(), stored.GetValue<string>("retriedByRunId"));
        // The successor reuses the same user message and counts as attempt 2; nothing new was written for the message.
        var successor = db.Collection("contract-runs").Document(winners.Single()).GetSnapshotAsync().GetAwaiter().GetResult();
        Assert.Equal("msg-1", successor.GetValue<string>("userMessageId"));
        Assert.Equal(2L, successor.GetValue<long>("attempt"));
        // Replaying the winner's run id is idempotent, not a second retry.
        Assert.Equal("replay", AdmitRetry(db, source, winners.Single()).GetAwaiter().GetResult());
        Assert.True(results.Where(r => r.Key != winners.Single()).All(r => r.Value is "already_retried" or "unknown"));
    }

    public static void RetryIsRefusedWhenTheSourceStillRunsOrAlreadyHasContent()
    {
        var db = Connect();
        var running = NewDocument(db);
        running.SetAsync(new Dictionary<string, object>
        { ["status"] = "running", ["hasAssistantMessage"] = false, ["userMessageId"] = "m", ["attempt"] = 1L }).GetAwaiter().GetResult();
        Assert.Equal("source_not_terminal", AdmitRetry(db, running, Guid.NewGuid().ToString("D")).GetAwaiter().GetResult());

        var partial = NewDocument(db);
        partial.SetAsync(new Dictionary<string, object>
        { ["status"] = "interrupted", ["hasAssistantMessage"] = true, ["userMessageId"] = "m", ["attempt"] = 1L }).GetAwaiter().GetResult();
        Assert.Equal("source_has_content", AdmitRetry(db, partial, Guid.NewGuid().ToString("D")).GetAwaiter().GetResult());
    }
}
