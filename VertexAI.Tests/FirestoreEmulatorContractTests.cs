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
        Task.WaitAll(outcomes.Select(outcome => Task.Run(async () =>
        {
            var won = await db.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(doc);
                if (snapshot.GetValue<string>("status") != "running") return false;
                transaction.Update(doc, new Dictionary<string, object> { ["status"] = outcome });
                return true;
            });
            if (won) winners.Add(outcome);
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
}
