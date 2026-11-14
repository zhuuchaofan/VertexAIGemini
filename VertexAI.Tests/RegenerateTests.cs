using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VertexAI.Services.Runs;

/// <summary>
/// Answering the latest question again although it already has a reply: over real HTTP, the real run and conversation
/// stores on the Firestore emulator, and a scripted model.
/// </summary>
internal static class RegenerateTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static async Task<HttpResponseMessage> PostAsync(ApiTestHost host, object body)
    {
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", body);
        await response.Content.ReadAsStringAsync();
        return response;
    }

    private static Task<HttpResponseMessage> SendAsync(ApiTestHost host, Guid conversation, string runId, string message) =>
        PostAsync(host, new { runId, conversationId = conversation, message });

    private static Task<HttpResponseMessage> RegenerateAsync(ApiTestHost host, Guid conversation, string runId, string? of) =>
        PostAsync(host, new { runId, conversationId = conversation, retryOfRunId = of, regenerate = true });

    private static async Task<string> CodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    public static void AnswersTheLatestTurnAgainAndKeepsTheOldReplyOutOfContext() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var first = RunHarness.NewRunId();
        var second = RunHarness.NewRunId();
        var again = RunHarness.NewRunId();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, conversation, first, "/ok first")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, conversation, second, "/ok second")).StatusCode);
        Assert.True((await h.Store.GetAsync(h.User, second, default))!.HasAssistantMessage);

        Assert.Equal(HttpStatusCode.OK, (await RegenerateAsync(host, conversation, again, second)).StatusCode);

        // The question is not copied; both replies are kept, the new one last.
        var messages = await h.MessagesAsync(conversation);
        Assert.Equal(5, messages.Count);
        Assert.Equal(2, messages.Count(m => (string)m["role"] == "user"));
        Assert.Equal(again, (string)messages[^1]["runId"]);
        Assert.Equal("model", (string)messages[^1]["role"]);

        var record = (await h.Store.GetAsync(h.User, again, default))!;
        Assert.Equal(RunStatus.Completed, record.Status);
        Assert.Equal("regenerate", record.Trigger);
        Assert.Equal(2, record.Attempt);
        Assert.Equal(second, record.RetryOfRunId!);
        Assert.Equal((await h.Store.GetAsync(h.User, second, default))!.UserMessageId, record.UserMessageId);
        Assert.Equal(again, (await h.Store.GetAsync(h.User, second, default))!.RetriedByRunId!);

        // The model was asked the second question again, with only the first turn as context: not its earlier reply.
        Assert.Equal("/ok second", h.Probe.Requests.Last().Message);
        var context = h.Probe.Histories.Last();
        Assert.Equal(2, context.Count);
        Assert.Equal("/ok first", context[0].Content);
    }

    public static void RefusedWithoutASourceForAnOlderTurnOrTwice() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var first = RunHarness.NewRunId();
        var second = RunHarness.NewRunId();
        await SendAsync(host, conversation, first, "/ok first");
        await SendAsync(host, conversation, second, "/ok second");

        Assert.Equal("regenerate_needs_run", await CodeAsync(await RegenerateAsync(host, conversation, RunHarness.NewRunId(), null), HttpStatusCode.BadRequest));
        Assert.Equal("not_latest_turn", await CodeAsync(await RegenerateAsync(host, conversation, RunHarness.NewRunId(), first), HttpStatusCode.Conflict));

        // A plain retry of an answered turn is still refused: only an explicit regenerate answers it again.
        Assert.Equal("source_has_content", await CodeAsync(
            await PostAsync(host, new { runId = RunHarness.NewRunId(), conversationId = conversation, retryOfRunId = second }), HttpStatusCode.Conflict));

        var again = RunHarness.NewRunId();
        Assert.Equal(HttpStatusCode.OK, (await RegenerateAsync(host, conversation, again, second)).StatusCode);
        Assert.Equal("already_retried", await CodeAsync(await RegenerateAsync(host, conversation, RunHarness.NewRunId(), second), HttpStatusCode.Conflict));

        // The newest answer can itself be regenerated.
        var third = RunHarness.NewRunId();
        Assert.Equal(HttpStatusCode.OK, (await RegenerateAsync(host, conversation, third, again)).StatusCode);
        Assert.Equal(3, (await h.Store.GetAsync(h.User, third, default))!.Attempt);
    }

    public static void TheNextQuestionSeesTheRegeneratedTurnOnce() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var first = RunHarness.NewRunId();
        await SendAsync(host, conversation, first, "/ok first");
        await RegenerateAsync(host, conversation, RunHarness.NewRunId(), first);
        await SendAsync(host, conversation, RunHarness.NewRunId(), "/ok next");

        var context = h.Probe.Histories.Last();
        Assert.Equal(2, context.Count); // one question and one reply, not the question with two replies
        Assert.Equal("/ok first", context[0].Content);
        Assert.Equal("model", context[1].Role);
    }
}
