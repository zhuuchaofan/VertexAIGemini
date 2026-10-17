using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VertexAI.Services;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>
/// A provider other than Gemini through the very same run chain: the real OpenAI-compatible client, run service,
/// stores and endpoints, with a fake upstream. It proves our side (what is sent, stopped, stored and recorded); it
/// says nothing about how any real provider behaves.
/// </summary>
internal static class OtherProviderTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static OpenAICompatibleProvider Provider(HttpMessageHandler handler) => new(
        new SingleClientFactory(handler),
        new OpenAICompatibleProviderSettings
        {
            Enabled = true, ProviderId = "deepseek", Name = "Fake DeepSeek", ApiKey = "test-key",
            Endpoint = "https://upstream.example/v1/chat/completions", ModelName = "deepseek-fake",
            Models = [new ChatModelOption { Name = "Deepseek Fake", ModelName = "deepseek-fake", SupportsThinking = true, MaxTokens = 32768 }]
        });

    private static ChatProviderCatalog Catalog(HttpMessageHandler handler) => new([Provider(handler)]);

    private static ApiTestHost Host(RunHarness harness, ChatProviderCatalog catalog) => new(harness, harness.User, catalog);

    private static async Task<JsonElement> PostRunAsync(ApiTestHost host, Guid conversation, string runId, object options)
    {
        var json = JsonSerializer.SerializeToElement(options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var body = new Dictionary<string, object?> { ["runId"] = runId, ["conversationId"] = conversation };
        foreach (var property in json.EnumerateObject()) body[property.Name] = property.Value;
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", body);
        await response.Content.ReadAsStringAsync();
        var run = await host.Http.GetAsync($"/api/v2/runs/{runId}");
        return JsonDocument.Parse(await run.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public static void ACompletedReplyKeepsItsThinkingUsageAndTheSettingsThatWereSent() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        var handler = new CapturingHandler(
            "data: {\"id\":\"cmpl-1\",\"model\":\"deepseek-fake-0613\",\"choices\":[{\"delta\":{\"reasoning_content\":\"weigh the options. \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Use **A**.\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4,\"total_tokens\":13,\"completion_tokens_details\":{\"reasoning_tokens\":2}}}\n\n" +
            "data: [DONE]\n\n");
        using var host = Host(h, Catalog(handler));
        var conversation = Guid.NewGuid();
        var run = await PostRunAsync(host, conversation, RunHarness.NewRunId(), new
        {
            message = "pick one", providerId = "deepseek", modelName = "deepseek-fake", presetId = "default",
            thinkingEnabled = true, thinkingLevel = "max", searchMode = "off",
            advanced = new { temperature = 0.3, maxOutputTokens = 777 }
        });

        Assert.Equal("completed", run.GetProperty("status").GetString()!);

        // What went upstream: this model, thinking at the requested depth, and the limits that were asked for.
        var body = handler.RequestBody ?? "";
        Assert.Contains("\"model\":\"deepseek-fake\"", body);
        Assert.Contains("\"reasoning_effort\":\"max\"", body);
        Assert.Contains("\"temperature\":0.3", body);
        Assert.Contains("\"max_tokens\":777", body);
        Assert.Contains("pick one", body);

        // What was kept: the reply and the thinking apart, the provider's own usage and finish reason, nothing invented.
        var messages = await h.MessagesAsync(conversation);
        Assert.Equal("Use **A**.", (string)messages[1]["content"]);
        Assert.Equal("weigh the options. ", (string)messages[1]["thinkingContent"]);
        Assert.Equal("provider", run.GetProperty("usage").GetProperty("source").GetString()!);
        Assert.Equal(9, run.GetProperty("usage").GetProperty("inputTokens").GetInt32());
        Assert.Equal(2, run.GetProperty("usage").GetProperty("thoughtsTokens").GetInt32());
        Assert.Equal("stop", run.GetProperty("finishReason").GetString()!);
        Assert.Equal("deepseek-fake-0613", run.GetProperty("modelVersion").GetString()!);
        Assert.Equal("max", run.GetProperty("effective").GetProperty("thinkingLevel").GetString()!);
        Assert.Equal(777, run.GetProperty("effective").GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(1, run.GetProperty("providerCalls").GetProperty("generate").GetInt32());
        Assert.Equal("deepseek", run.GetProperty("requested").GetProperty("providerId").GetString()!);
    }

    public static void AStopClosesTheUpstreamRequestAndKeepsWhatWasWritten() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        var handler = new SlowSseHandler();
        var catalog = Catalog(handler);
        var service = h.NewService(catalog);
        var runId = RunHarness.NewRunId();
        var conversation = Guid.NewGuid();
        var request = new RunStartRequest(h.User, runId, conversation, "go on", null,
            new ChatSessionOptions(ProviderId: "deepseek", ModelName: "deepseek-fake"));
        var admission = await service.AdmitAsync(request, default);
        var events = new List<RunEvent>();
        var execution = Task.Run(() => service.ExecuteAsync(admission, e => { lock (events) events.Add(e); return Task.CompletedTask; }, CancellationToken.None));
        Assert.True(await RunHarness.EventuallyAsync(() => { lock (events) return events.OfType<RunEvent.Delta>().Count() >= 3; }));

        var stoppedAt = Stopwatch.GetTimestamp();
        await service.StopAsync(h.User, runId, default);
        await execution;

        var record = (await h.Store.GetAsync(h.User, runId, default))!;
        Assert.Equal(RunStatus.Stopped, record.Status);
        Assert.True(record.HasAssistantMessage);
        Assert.True(await RunHarness.EventuallyAsync(() => handler.ClosedTicks != 0, 3000)); // the upstream saw the request end
        Assert.True(Stopwatch.GetElapsedTime(stoppedAt, handler.ClosedTicks).TotalSeconds < 2);
        Assert.Equal(1, handler.Requests);
        Assert.True(((string)(await h.MessagesAsync(conversation))[1]["content"]).StartsWith("word1 word2"));
        Assert.Equal("estimate", record.Usage!.Source); // no usage arrived, so the figure is ours and says so
        Assert.Null(record.FinishReason);
    }

    public static void AnImageAndAnEarlierAttachmentReachTheProviderInItsOwnFormat() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n");
        using var host = Host(h, Catalog(handler));
        var conversation = Guid.NewGuid();
        var png = Convert.ToBase64String([137, 80, 78, 71, 1, 2, 3]);
        await PostRunAsync(host, conversation, RunHarness.NewRunId(), new
        {
            message = "what is this", providerId = "deepseek", modelName = "deepseek-fake",
            attachments = new[] { new { base64Data = png, mimeType = "image/png", fileName = "a.png" } }
        });
        Assert.Contains("\"image_url\"", handler.RequestBody ?? "");
        Assert.Contains(png, handler.RequestBody ?? "");

        // The next question still carries that picture as context.
        await PostRunAsync(host, conversation, RunHarness.NewRunId(), new { message = "and now?", providerId = "deepseek", modelName = "deepseek-fake" });
        Assert.Contains(png, handler.RequestBody ?? "");
    }

    public static void AnUpstreamErrorEndsTheRunAsAFailureThatCanBeRetried() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        var failing = new FailingThenWorkingHandler();
        using var host = Host(h, Catalog(failing));
        var conversation = Guid.NewGuid();
        var first = RunHarness.NewRunId();
        var run = await PostRunAsync(host, conversation, first, new { message = "try me", providerId = "deepseek", modelName = "deepseek-fake" });
        Assert.Equal("failed", run.GetProperty("status").GetString()!);
        Assert.True(run.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.False(run.GetProperty("hasAssistantMessage").GetBoolean());

        var second = RunHarness.NewRunId();
        var retried = await PostRunAsync(host, conversation, second, new { retryOfRunId = first, providerId = "deepseek", modelName = "deepseek-fake" });
        Assert.Equal("completed", retried.GetProperty("status").GetString()!);
        Assert.Equal(2, (await h.MessagesAsync(conversation)).Count);
        Assert.Equal(2, failing.Calls);
    }

    private sealed class FailingThenWorkingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"fine\"}}]}\n\ndata: [DONE]\n\n") });
        }
    }
}
