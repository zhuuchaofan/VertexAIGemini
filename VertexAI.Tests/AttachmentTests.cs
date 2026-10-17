using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VertexAI.Services.Attachments;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>
/// Attachments on the run API: written only after admission, never over an existing object, never sent to a model
/// unless stored, and never served to anyone but their owner. Real endpoints, real run and conversation stores on the
/// Firestore emulator, a scripted model and an in-memory bucket.
/// </summary>
internal static class AttachmentTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static object File(string name, string content, string mime = "text/plain") => new
    {
        base64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
        mimeType = mime,
        fileName = name
    };

    private static async Task<HttpResponseMessage> RunAsync(
        ApiTestHost host, Guid conversation, string runId, string message, params object[] attachments)
    {
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", new
        {
            runId, conversationId = conversation, message, attachments
        });
        await response.Content.ReadAsStringAsync(); // a stream ends when the run does
        return response;
    }

    private static async Task<JsonElement> GetAsync(ApiTestHost host, string path)
    {
        var response = await host.Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static string Hash16(string content) => AttachmentNames.Sha256Hex(Encoding.UTF8.GetBytes(content))[..16];

    // ---------------------------------------------------------------------------------------- tests

    public static void AnAttachmentIsStoredAfterAdmissionUnderItsHashAndSentToTheModel() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        var response = await RunAsync(host, conversation, runId, "/ok summarize", File("notes.txt", "alpha beta"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var messageId = RunIds.UserMessageId(runId);
        var expectedName = $"chat-attachments/{conversation:D}/{messageId:D}/00-{Hash16("alpha beta")}.txt";
        Assert.Equal(expectedName, h.Attachments.Objects.Keys.Single());
        Assert.Equal(1, h.Attachments.WriteAttempts);

        // The model was given the content, not just a reference.
        var request = h.Probe.Requests.Single();
        Assert.Equal(1, request.Attachments.Count);
        Assert.Equal("alpha beta", Encoding.UTF8.GetString(Convert.FromBase64String(request.Attachments.First().Base64Data!)));

        // The message records metadata and the final state; the browser never receives content in the conversation.
        var detail = await GetAsync(host, $"/api/v2/conversations/{conversation}");
        var user = detail.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
        var attachment = user.GetProperty("attachments")[0];
        Assert.Equal("notes.txt", attachment.GetProperty("fileName").GetString()!);
        Assert.Equal("stored", attachment.GetProperty("state").GetString()!);
        Assert.Equal(10, attachment.GetProperty("sizeBytes").GetInt64());
        Assert.False(detail.GetRawText().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("alpha beta"))));

        var download = await host.Http.GetAsync($"/api/v2/conversations/{conversation}/messages/{messageId}/attachments/0");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("alpha beta", await download.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition!.DispositionType);

        // The run keeps metadata only, and the quota counted the bytes.
        var record = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal(1, record.Attachments.Count);
        Assert.Equal(AttachmentNames.Sha256Hex(Encoding.UTF8.GetBytes("alpha beta")), record.Attachments[0].Sha256!);
        Assert.Equal(10L, await h.UsageAsync("attachmentBytes"));
    }

    public static void ARepeatedRunIdWithDifferentAttachmentContentIsRefusedBeforeAnythingIsWritten() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        await RunAsync(host, conversation, runId, "/ok hi", File("a.txt", "first content"));
        var original = h.Attachments.Objects.Single();
        var originalBytes = original.Value.ToArray();

        var other = await RunAsync(host, conversation, runId, "/ok hi", File("a.txt", "different content"));
        Assert.Equal("idempotency_mismatch", await ErrorCodeAsync(other, HttpStatusCode.Conflict));
        Assert.Equal(1, h.Attachments.WriteAttempts); // nothing was attempted for the conflicting request
        Assert.Equal(1, h.Attachments.Objects.Count);
        Assert.Equal(Convert.ToHexString(originalBytes), Convert.ToHexString(h.Attachments.Objects[original.Key]));

        // The same request again is a replay: it executes nothing and writes nothing.
        var replay = await RunAsync(host, conversation, runId, "/ok hi", File("a.txt", "first content"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, h.Attachments.WriteAttempts);
        Assert.Equal(1, h.Probe.StreamCalls);
    }

    public static void RefusedAdmissionsNeverTouchTheBucket() => Wait(Async3());
    private static async Task Async3()
    {
        // Someone else's conversation.
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        using (var owner = new ApiTestHost(h, h.User)) await RunAsync(owner, conversation, RunHarness.NewRunId(), "/ok mine");
        var stranger = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "x@example.test");
        using (var host = new ApiTestHost(h, stranger))
        {
            Assert.Equal("conflict", await ErrorCodeAsync(await RunAsync(host, conversation, RunHarness.NewRunId(), "/ok hi", File("a.txt", "x")), HttpStatusCode.Conflict));
        }
        Assert.Equal(0, h.Attachments.WriteAttempts);

        // A conversation that is busy.
        var busyHarness = new RunHarness();
        using var busyHost = new ApiTestHost(busyHarness, busyHarness.User);
        var busyConversation = Guid.NewGuid();
        var first = busyHarness.Service.AdmitAsync(busyHarness.Request("/gate x", RunHarness.NewRunId(), busyConversation), default).GetAwaiter().GetResult();
        var running = Task.Run(() => busyHarness.Service.ExecuteAsync(first, _ => Task.CompletedTask, CancellationToken.None));
        Assert.True(await RunHarness.EventuallyAsync(() => busyHarness.Probe.StreamCalls == 1));
        Assert.Equal("run_in_progress", await ErrorCodeAsync(await RunAsync(busyHost, busyConversation, RunHarness.NewRunId(), "/ok hi", File("a.txt", "x")), HttpStatusCode.Conflict));
        busyHarness.Probe.Gate.Release();
        await running;
        Assert.Equal(0, busyHarness.Attachments.WriteAttempts);

        // A request the quota refuses: stored as a failed run, with its attachment unavailable, and nothing in the bucket.
        var quotaHarness = new RunHarness(dailyRequestLimit: 1);
        using var quotaHost = new ApiTestHost(quotaHarness, quotaHarness.User);
        var quotaConversation = Guid.NewGuid();
        await RunAsync(quotaHost, quotaConversation, RunHarness.NewRunId(), "/ok first");
        await RunAsync(quotaHost, quotaConversation, RunHarness.NewRunId(), "/ok second", File("a.txt", "x"));
        Assert.Equal(0, quotaHarness.Attachments.WriteAttempts);
        var detail = await GetAsync(quotaHost, $"/api/v2/conversations/{quotaConversation}");
        var refused = detail.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal("failed", refused.GetProperty("attachments")[0].GetProperty("state").GetString()!);
    }

    public static void AnExistingObjectIsNeverOverwrittenAndDifferentContentGetsADifferentName() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();

        // The same bytes already sit under the name this run will use (an earlier, interrupted attempt of the same run).
        var name = $"chat-attachments/{conversation:D}/{RunIds.UserMessageId(runId):D}/00-{Hash16("same bytes")}.txt";
        var earlier = Encoding.UTF8.GetBytes("same bytes");
        h.Attachments.Objects[name] = earlier;
        await RunAsync(host, conversation, runId, "/ok hi", File("a.txt", "same bytes"));

        Assert.Equal(1, h.Attachments.WriteAttempts);
        Assert.Equal(0, h.Attachments.Deletes);
        Assert.Equal(1, h.Attachments.Objects.Count);
        Assert.True(ReferenceEquals(earlier, h.Attachments.Objects[name]));
        var detail = await GetAsync(host, $"/api/v2/conversations/{conversation}");
        Assert.Equal("stored", detail.GetProperty("messages")[0].GetProperty("attachments")[0].GetProperty("state").GetString()!);

        // Two different files in one message, and the same name in the next message, never collide.
        await RunAsync(host, conversation, RunHarness.NewRunId(), "/ok again", File("a.txt", "one"), File("a.txt", "two"));
        Assert.Equal(3, h.Attachments.Objects.Count);
        Assert.Equal(3, h.Attachments.Objects.Keys.Distinct().Count());
    }

    public static void AStorageFailureEndsTheRunWithoutTheModelAndRemovesWhatItWrote() => Wait(Async5());
    private static async Task Async5()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        h.Attachments.FailWrite = name => name.Contains("/01-");

        var response = await RunAsync(host, conversation, runId, "/ok hi", File("a.txt", "first"), File("b.txt", "second"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the stream still ends with the run's real outcome

        var record = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Equal("storage", record.StatusReason!);
        Assert.True(record.Error!.Retryable);
        Assert.Equal(0, h.Probe.StreamCalls); // the model was never called
        Assert.False(record.ProviderCallStarted);
        Assert.Equal(0, h.Attachments.Objects.Count); // what this run created is gone again
        Assert.Equal(1, h.Attachments.Deletes);
        Assert.Equal(0L, await h.UsageAsync("requests")); // the reservation went back
        Assert.Equal(0L, await h.UsageAsync("attachmentBytes"));

        var detail = await GetAsync(host, $"/api/v2/conversations/{conversation}");
        var messages = detail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(1, messages.Count); // the question is kept; there is no reply
        Assert.Equal("failed", messages[0].GetProperty("attachments")[0].GetProperty("state").GetString()!);
        Assert.Equal("failed", messages[0].GetProperty("attachments")[1].GetProperty("state").GetString()!);
    }

    public static void ACrashBetweenAdmissionAndUploadLeavesAttachmentsThatReadAsUnavailable() => Wait(Async6());
    private static async Task Async6()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();

        // Admitted, and then the process died before doing anything else.
        var admission = await h.Service.AdmitAsync(
            new RunStartRequest(h.User, runId, conversation, "/ok hi", null, new ChatSessionOptions(),
                [new ChatAttachment(Convert.ToBase64String(Encoding.UTF8.GetBytes("x")), "text/plain", "a.txt")]),
            default);
        Assert.True(admission.Result is AdmitResult.Admitted);
        await Task.Delay((RunHarness.LeaseSeconds + 1) * 1000);

        var detail = await GetAsync(host, $"/api/v2/conversations/{conversation}"); // reading settles the lost run
        var user = detail.GetProperty("messages")[0];
        Assert.Equal("failed", user.GetProperty("attachments")[0].GetProperty("state").GetString()!);
        Assert.Equal("interrupted", detail.GetProperty("runs")[0].GetProperty("status").GetString()!);
        Assert.Equal(0, h.Probe.StreamCalls);
        Assert.Equal(0, h.Attachments.WriteAttempts);
        Assert.Equal(0L, await h.UsageAsync("requests"));

        var download = await host.Http.GetAsync($"/api/v2/conversations/{conversation}/messages/{RunIds.UserMessageId(runId)}/attachments/0");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    public static void WithoutABucketTheContentStaysOnTheMessageAndIsServedFromThere() => Wait(Async7());
    private static async Task Async7()
    {
        var h = new RunHarness(attachmentStorage: false);
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        await RunAsync(host, conversation, runId, "/ok hi", File("a.txt", "inline content"));

        Assert.Equal(0, h.Attachments.WriteAttempts);
        var stored = (await h.MessagesAsync(conversation))[0];
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("inline content")), (string)stored["attachmentsJson"]);
        var detail = await GetAsync(host, $"/api/v2/conversations/{conversation}");
        Assert.False(detail.GetRawText().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("inline content"))));
        Assert.Equal("stored", detail.GetProperty("messages")[0].GetProperty("attachments")[0].GetProperty("state").GetString()!);

        var download = await host.Http.GetAsync($"/api/v2/conversations/{conversation}/messages/{RunIds.UserMessageId(runId)}/attachments/0");
        Assert.Equal("inline content", await download.Content.ReadAsStringAsync());
        Assert.Equal(1, h.Probe.Requests.Single().Attachments.Count);
    }

    public static void EarlierAttachmentsStayInTheContextAndOnlyWhenStored() => Wait(Async8());
    private static async Task Async8()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        await RunAsync(host, conversation, RunHarness.NewRunId(), "/ok first", File("a.txt", "remember me"));
        await RunAsync(host, conversation, RunHarness.NewRunId(), "/ok second");

        var history = h.Probe.Histories.Last();
        var question = history.First(e => e.Role == "user");
        Assert.Equal(1, question.Attachments!.Count);
        Assert.Equal("remember me", Encoding.UTF8.GetString(Convert.FromBase64String(question.Attachments.First().Base64Data!)));

        // An object that has gone missing is left out of the context; the run does not fail because of it.
        h.Attachments.Objects.Clear();
        var third = await RunAsync(host, conversation, RunHarness.NewRunId(), "/ok third");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var last = h.Probe.Histories.Last();
        Assert.Equal(0, last.First(e => e.Role == "user").Attachments!.Count);
        Assert.Equal(3, h.Probe.StreamCalls);
    }

    public static void InvalidAttachmentsAreRefusedBeforeAnyRunExists() => Wait(Async9());
    private static async Task Async9()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();

        async Task Refused(params object[] attachments) =>
            Assert.Equal("invalid_attachments", await ErrorCodeAsync(await RunAsync(host, conversation, RunHarness.NewRunId(), "/ok hi", attachments), HttpStatusCode.BadRequest));

        await Refused(Enumerable.Range(0, 9).Select(i => File($"{i}.txt", "x")).ToArray()); // more than eight
        await Refused(File("a.exe", "x", "application/x-msdownload")); // a type nobody allowed
        await Refused(new { base64Data = "not base64!!", mimeType = "text/plain", fileName = "a.txt" });
        await Refused(File("big.txt", new string('x', 600 * 1024))); // above the 512 KB file limit

        // Nothing was admitted or written.
        Assert.Equal(0, h.Probe.StreamCalls);
        Assert.Equal(0, h.Attachments.WriteAttempts);
        Assert.Null(await h.ConversationAsync(conversation));

        // Without a bucket the content lives inside the message, so the total must fit in one document.
        var inline = new RunHarness(attachmentStorage: false);
        using var inlineHost = new ApiTestHost(inline, inline.User);
        var big = Convert.ToBase64String(new byte[400 * 1024]);
        var response = await inlineHost.Http.PostAsJsonAsync("/api/v2/runs", new
        {
            runId = RunHarness.NewRunId(), conversationId = Guid.NewGuid(), message = "hi",
            attachments = new[] { new { base64Data = big, mimeType = "image/png", fileName = "a.png" }, new { base64Data = big, mimeType = "image/png", fileName = "b.png" } }
        });
        Assert.Equal("invalid_attachments", await ErrorCodeAsync(response, HttpStatusCode.BadRequest));
    }

    public static void OnlyTheOwnerCanDownloadAnAttachmentAndOnlyAValidIndex() => Wait(Async10());
    private static async Task Async10()
    {
        var h = new RunHarness();
        using var owner = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        await RunAsync(owner, conversation, runId, "/ok hi", File("a.txt", "secret"));
        var path = $"/api/v2/conversations/{conversation}/messages/{RunIds.UserMessageId(runId)}/attachments";

        Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync($"{path}/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"{path}/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"{path}/-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"/api/v2/conversations/{Guid.NewGuid()}/messages/{RunIds.UserMessageId(runId)}/attachments/0")).StatusCode);

        var stranger = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "x@example.test");
        using var other = new ApiTestHost(h, stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"{path}/0")).StatusCode);

        // An object that was altered in the bucket is not served.
        h.Attachments.Objects[h.Attachments.Objects.Keys.Single()] = Encoding.UTF8.GetBytes("tamper");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"{path}/0")).StatusCode);
    }
}
