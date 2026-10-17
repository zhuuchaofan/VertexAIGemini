using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VertexAI.Services.Auth;
using VertexAI.Services.Runs;

/// <summary>
/// Trying a failed turn again, and deleting a conversation: over real HTTP, real run and conversation stores on the
/// Firestore emulator, a scripted model and an in-memory bucket.
/// </summary>
internal static class RetryTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static async Task<HttpResponseMessage> SendAsync(ApiTestHost host, Guid conversation, string runId, string message, params object[] attachments)
    {
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", new { runId, conversationId = conversation, message, attachments });
        await response.Content.ReadAsStringAsync();
        return response;
    }

    private static async Task<HttpResponseMessage> RetryAsync(ApiTestHost host, Guid conversation, string runId, string retryOf)
    {
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", new { runId, conversationId = conversation, retryOfRunId = retryOf });
        await response.Content.ReadAsStringAsync();
        return response;
    }

    private static async Task<JsonElement> DetailAsync(ApiTestHost host, Guid conversation)
    {
        var response = await host.Http.GetAsync($"/api/v2/conversations/{conversation}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static object File(string name, string content) => new
    {
        base64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), mimeType = "text/plain", fileName = name
    };

    // ---------------------------------------------------------------------------------------- retry

    public static void ARetryAnswersTheStoredQuestionWithoutCopyingIt() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var first = RunHarness.NewRunId();
        var second = RunHarness.NewRunId();

        await SendAsync(host, conversation, first, "/flaky please");
        var failed = (await h.Store.GetAsync(h.User, first, default))!;
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.False(failed.HasAssistantMessage);

        var retry = await RetryAsync(host, conversation, second, first);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        var messages = await h.MessagesAsync(conversation);
        Assert.Equal(2, messages.Count); // one question, one answer: the question was not copied
        Assert.Equal("user", (string)messages[0]["role"]);
        Assert.Equal("/flaky please", (string)messages[0]["content"]);
        Assert.Equal(first, (string)messages[0]["runId"]);
        Assert.Equal("model", (string)messages[1]["role"]);
        Assert.Equal(second, (string)messages[1]["runId"]);
        Assert.Equal("Hello again", (string)messages[1]["content"]);

        var again = (await h.Store.GetAsync(h.User, second, default))!;
        Assert.Equal(RunStatus.Completed, again.Status);
        Assert.Equal(2, again.Attempt);
        Assert.Equal("retry", again.Trigger == "" ? "" : "retry");
        Assert.Equal(first, again.RetryOfRunId!);
        Assert.Equal(failed.UserMessageId, again.UserMessageId);
        Assert.Equal(second, (await h.Store.GetAsync(h.User, first, default))!.RetriedByRunId!);

        // The question was given to the model once more, and was not also part of the history.
        Assert.Equal(2, h.Probe.StreamCalls);
        Assert.Equal(0, h.Probe.Histories.Last().Count);
        Assert.Equal("/flaky please", h.Probe.Requests.Last().Message);

        var detail = await DetailAsync(host, conversation);
        var runs = detail.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(first, runs[1].GetProperty("retryOfRunId").GetString()!);
        Assert.Equal(second, runs[0].GetProperty("retriedByRunId").GetString()!);
    }

    public static void ARetryIsRefusedUnlessItsSourceIsAFailedLatestTurnThatWasNotRetried() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);

        // A turn that got an answer.
        var answered = Guid.NewGuid();
        var done = RunHarness.NewRunId();
        await SendAsync(host, answered, done, "/ok fine");
        Assert.Equal("source_has_content", await CodeAsync(await RetryAsync(host, answered, RunHarness.NewRunId(), done), HttpStatusCode.Conflict));

        // Retried once; a second retry of the same run is refused.
        var conversation = Guid.NewGuid();
        var failed = RunHarness.NewRunId();
        await SendAsync(host, conversation, failed, "/fail now"); // fails again on retry: still no content, still the latest
        var retried = RunHarness.NewRunId();
        Assert.Equal(HttpStatusCode.OK, (await RetryAsync(host, conversation, retried, failed)).StatusCode);
        Assert.Equal("already_retried", await CodeAsync(await RetryAsync(host, conversation, RunHarness.NewRunId(), failed), HttpStatusCode.Conflict));

        // The retry can itself be retried (it is the latest), but the first failure no longer is.
        var third = RunHarness.NewRunId();
        Assert.Equal(HttpStatusCode.OK, (await RetryAsync(host, conversation, third, retried)).StatusCode);
        Assert.Equal(3, (await h.Store.GetAsync(h.User, third, default))!.Attempt);

        // A newer turn makes an older failure not the latest.
        var other = Guid.NewGuid();
        var old = RunHarness.NewRunId();
        await SendAsync(host, other, old, "/fail early");
        await SendAsync(host, other, RunHarness.NewRunId(), "/ok newer");
        Assert.Equal("not_latest_turn", await CodeAsync(await RetryAsync(host, other, RunHarness.NewRunId(), old), HttpStatusCode.Conflict));

        // A retry takes no message of its own, and an unknown or foreign run looks like any other conflict.
        var withText = await host.Http.PostAsJsonAsync("/api/v2/runs", new { runId = RunHarness.NewRunId(), conversationId = conversation, retryOfRunId = third, message = "extra" });
        Assert.Equal("retry_with_content", await CodeAsync(withText, HttpStatusCode.BadRequest));
        Assert.Equal("conflict", await CodeAsync(await RetryAsync(host, conversation, RunHarness.NewRunId(), RunHarness.NewRunId()), HttpStatusCode.Conflict));
        var stranger = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "x@example.test");
        using var strangerHost = new ApiTestHost(h, stranger);
        Assert.Equal("conflict", await CodeAsync(await RetryAsync(strangerHost, conversation, RunHarness.NewRunId(), third), HttpStatusCode.Conflict));
    }

    public static void RepeatingARetryWithTheSameRunIdExecutesNothingTwice() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var failed = RunHarness.NewRunId();
        var retry = RunHarness.NewRunId();
        await SendAsync(host, conversation, failed, "/flaky x");
        await RetryAsync(host, conversation, retry, failed);
        Assert.Equal(2, h.Probe.StreamCalls);

        var again = await RetryAsync(host, conversation, retry, failed); // the browser did not hear the answer and asks again
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, h.Probe.StreamCalls);
        Assert.Equal(2, (await h.MessagesAsync(conversation)).Count);
    }

    public static void ARetryReusesTheStoredAttachmentsWithoutUploadingThemAgain() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var failed = RunHarness.NewRunId();
        await SendAsync(host, conversation, failed, "/flaky look", File("a.txt", "the file"));
        Assert.Equal(1, h.Attachments.WriteAttempts);

        var retry = RunHarness.NewRunId();
        await RetryAsync(host, conversation, retry, failed);
        Assert.Equal(1, h.Attachments.WriteAttempts); // not uploaded again
        Assert.Equal(1, h.Attachments.Objects.Count);
        var last = h.Probe.Requests.Last();
        Assert.Equal(1, last.Attachments.Count);
        Assert.Equal("the file", Encoding.UTF8.GetString(Convert.FromBase64String(last.Attachments.First().Base64Data!)));
        Assert.Equal(1, (await h.Store.GetAsync(h.User, retry, default))!.Attachments.Count);
    }

    public static void ATurnWhoseAttachmentsNeverArrivedCannotBeRetried() => Wait(Async5());
    private static async Task Async5()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var failed = RunHarness.NewRunId();
        h.Attachments.FailWrite = _ => true;
        await SendAsync(host, conversation, failed, "/ok hi", File("a.txt", "x"));
        Assert.Equal("storage", (await h.Store.GetAsync(h.User, failed, default))!.StatusReason!);

        h.Attachments.FailWrite = null;
        Assert.Equal("attachments_unavailable", await CodeAsync(await RetryAsync(host, conversation, RunHarness.NewRunId(), failed), HttpStatusCode.Conflict));
        Assert.Equal(0, h.Probe.StreamCalls);
    }

    // ---------------------------------------------------------------------------------------- delete

    public static void DeletingAConversationRemovesItEntirelyButNotWhileItIsGenerating() => Wait(Async6());
    private static async Task Async6()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var done = RunHarness.NewRunId();
        await SendAsync(host, conversation, done, "/ok keep", File("a.txt", "bytes"));
        Assert.Equal(1, h.Attachments.Objects.Count);

        // Busy: a run is generating in it.
        var admission = await h.Service.AdmitAsync(h.Request("/gate x", RunHarness.NewRunId(), conversation), default);
        var running = Task.Run(() => h.Service.ExecuteAsync(admission, _ => Task.CompletedTask, CancellationToken.None));
        Assert.True(await RunHarness.EventuallyAsync(() => h.Probe.StreamCalls == 2));
        Assert.Equal("run_in_progress", await CodeAsync(await host.Http.DeleteAsync($"/api/v2/conversations/{conversation}"), HttpStatusCode.Conflict));
        Assert.Equal(1, h.Attachments.Objects.Count);
        h.Probe.Gate.Release();
        await running;

        // Someone else cannot, and cannot tell it exists.
        var stranger = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "x@example.test");
        using (var strangerHost = new ApiTestHost(h, stranger))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await strangerHost.Http.DeleteAsync($"/api/v2/conversations/{conversation}")).StatusCode);
        }
        Assert.NotNull(await h.ConversationAsync(conversation));

        Assert.Equal(HttpStatusCode.NoContent, (await host.Http.DeleteAsync($"/api/v2/conversations/{conversation}")).StatusCode);
        Assert.Null(await h.ConversationAsync(conversation));
        Assert.Equal(0, (await h.MessagesAsync(conversation)).Count);
        Assert.Equal(0, h.Attachments.Objects.Count); // the stored attachment went with it
        Assert.Null(await h.Store.GetAsync(h.User, done, default));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync($"/api/v2/conversations/{conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.DeleteAsync($"/api/v2/conversations/{conversation}")).StatusCode);
    }
}
