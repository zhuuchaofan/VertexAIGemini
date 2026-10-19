using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VertexAI.Services;
using VertexAI.Services.Chat;
using VertexAI.Services.Search;

/// <summary>
/// Search before the answer for providers that have no search of their own. A scripted search backend and a fake
/// upstream: this proves when we search, what the model is given, and what is recorded, not how Gemini or any
/// provider behaves.
/// </summary>
internal static class WebSearchTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 1, 48, 0, TimeSpan.Zero);

    private static WebSearchRequest Ask(string message, string mode = "auto") => new(message, [], mode, Now);

    private sealed class ScriptedBackend : IWebSearchBackend
    {
        public Func<string, string> Decide { get; set; } = _ => "{\"search\": true, \"query\": \"rewritten query\"}";
        public Func<string, WebSearchFindings> Search { get; set; } = q =>
            new WebSearchFindings($"digest for {q}", [new SearchCitation { Title = "Source A", Uri = "https://a.example/x" }]);
        public TimeSpan Delay { get; set; }
        public int Decides { get; private set; }
        public int Searches { get; private set; }
        public string? LastQuery { get; private set; }

        public async Task<string> DecideAsync(string prompt, CancellationToken cancellationToken)
        {
            Decides++;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return Decide(prompt);
        }

        public async Task<WebSearchFindings> SearchAsync(string query, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Searches++;
            LastQuery = query;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return Search(query);
        }
    }

    private static WebSearchService Service(ScriptedBackend backend, double timeoutSeconds = 5) =>
        new(backend, TimeSpan.FromSeconds(timeoutSeconds));

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    // ---------------------------------------------------------------------------------------- pure parts

    public static void TheRouterAnswerIsReadLeniently()
    {
        Assert.True(SearchDecision.Parse("{\"search\": true, \"query\": \"西安 天气\"}")!.Search);
        Assert.Equal("西安 天气", SearchDecision.Parse("{\"search\": true, \"query\": \"西安 天气\"}")!.Query!);
        Assert.False(SearchDecision.Parse("```json\n{\"search\": false, \"query\": \"\"}\n```")!.Search);
        Assert.True(SearchDecision.Parse("Sure: {\"search\":true}")!.Search);
        Assert.True(SearchDecision.Parse("") is null);
        Assert.True(SearchDecision.Parse("not json") is null);
        Assert.True(SearchDecision.Parse("{\"search\": \"yes\"}") is null); // not a boolean
        Assert.True(SearchDecision.Parse("{\"query\": \"x\"}") is null);
    }

    public static void TheKeywordRulesCatchWhatChangesOverTime()
    {
        foreach (var yes in new[] { "今天西安什么天气", "苹果最新的手机是什么", "现在汇率多少", "who won the score today", "2026年的新规", "帮我搜索一下这个" })
        {
            Assert.True(TimeSensitivity.LooksTimeSensitive(yes));
        }

        foreach (var no in new[] { "把这段话翻译成英文", "写一篇关于秋天的文章", "解释一下什么是闭包", "你好" })
        {
            Assert.False(TimeSensitivity.LooksTimeSensitive(no));
        }
    }

    public static void TheModelIsToldTheDateInBeijingTime()
    {
        Assert.Equal("2026年10月19日 星期一 09:48（UTC+8）", ChatTime.Describe(Now, 8));
        Assert.Equal("2026年10月19日 星期一 01:48（UTC+0）", ChatTime.Describe(Now, 0));
    }

    public static void TheQuestionIsComposedWithDateResultsAndNumberedSources()
    {
        var used = new WebSearchOutcome(WebSearchStatus.Used, "q", "the digest", [
            new SearchCitation { Title = "Source A", Uri = "https://a.example" },
            new SearchCitation { Title = "", Uri = "https://b.example" }], null, 2);
        var text = WebSearchPrompt.Compose("what now?", Now, used);
        Assert.Contains("[当前时间：2026年10月19日 星期一 09:48", text);
        Assert.Contains("the digest", text);
        Assert.Contains("[1] Source A", text);
        Assert.Contains("[2] https://b.example", text);
        Assert.Contains("不要编造", text);
        Assert.True(text.EndsWith("what now?"));

        var failed = WebSearchPrompt.Compose("what now?", Now, WebSearchOutcome.None(WebSearchStatus.Failed, "x"));
        Assert.Contains("检索没有成功", failed);
        Assert.False(failed.Contains("[联网检索结果"));

        var plain = WebSearchPrompt.Compose("what now?", Now, WebSearchOutcome.None(WebSearchStatus.Skipped, "x"));
        Assert.Contains("[当前时间", plain);
        Assert.False(plain.Contains("检索"));
    }

    // ---------------------------------------------------------------------------------------- the decision

    public static void WithTheWebOffNothingIsAsked() => Wait(Async1());
    private static async Task Async1()
    {
        var backend = new ScriptedBackend();
        var outcome = await Service(backend).SearchAsync(Ask("今天天气", "off"), default);
        Assert.Equal(WebSearchStatus.Skipped, outcome.Status);
        Assert.Equal(0, backend.Decides + backend.Searches);
    }

    public static void AutoSearchesWhenTheRouterSaysSoWithItsRewrittenQuery() => Wait(Async2());
    private static async Task Async2()
    {
        var backend = new ScriptedBackend();
        var outcome = await Service(backend).SearchAsync(Ask("那明天呢"), default);
        Assert.Equal(WebSearchStatus.Used, outcome.Status);
        Assert.Equal("rewritten query", outcome.Query!);
        Assert.Equal("rewritten query", backend.LastQuery!);
        Assert.Equal(2, outcome.ProviderCalls);
        Assert.Equal(1, outcome.Citations.Count);
    }

    public static void AutoSkipsWhenTheRouterSaysTheQuestionNeedsNoWeb() => Wait(Async3());
    private static async Task Async3()
    {
        var backend = new ScriptedBackend { Decide = _ => "{\"search\": false, \"query\": \"\"}" };
        var outcome = await Service(backend).SearchAsync(Ask("写一篇关于秋天的文章"), default);
        Assert.Equal(WebSearchStatus.Skipped, outcome.Status);
        Assert.Equal(0, backend.Searches);
        Assert.Equal(1, outcome.ProviderCalls); // only the router
    }

    public static void ForceSearchesEvenWhenTheRouterWouldSkip() => Wait(Async4());
    private static async Task Async4()
    {
        var backend = new ScriptedBackend { Decide = _ => "{\"search\": false, \"query\": \"better query\"}" };
        var outcome = await Service(backend).SearchAsync(Ask("写一篇文章", "force"), default);
        Assert.Equal(WebSearchStatus.Used, outcome.Status);
        Assert.Equal("better query", backend.LastQuery!); // the router still improves the query
    }

    public static void WithoutTheRouterPlainRulesDecide() => Wait(Async5());
    private static async Task Async5()
    {
        var backend = new ScriptedBackend { Decide = _ => throw new InvalidOperationException("router down") };
        var service = Service(backend);

        var time = await service.SearchAsync(Ask("今天西安什么天气"), default);
        Assert.Equal(WebSearchStatus.Used, time.Status);
        Assert.Equal("今天西安什么天气", backend.LastQuery!); // no rewrite available: the question itself

        var plain = await service.SearchAsync(Ask("把这段话翻译成英文"), default);
        Assert.Equal(WebSearchStatus.Skipped, plain.Status);
        Assert.Contains("关键词规则", plain.Reason ?? "");
    }

    public static void ABadRouterAnswerFallsBackTheSameWay() => Wait(Async6());
    private static async Task Async6()
    {
        var backend = new ScriptedBackend { Decide = _ => "I think you should search." };
        var outcome = await Service(backend).SearchAsync(Ask("现在的油价"), default);
        Assert.Equal(WebSearchStatus.Used, outcome.Status);
    }

    // ---------------------------------------------------------------------------------------- failures never break the answer

    public static void AFailedOrEmptySearchIsReportedNotThrown() => Wait(Async7());
    private static async Task Async7()
    {
        var failing = new ScriptedBackend { Search = _ => throw new HttpRequestException("503") };
        var failed = await Service(failing).SearchAsync(Ask("今天天气"), default);
        Assert.Equal(WebSearchStatus.Failed, failed.Status);
        Assert.Contains("HttpRequestException", failed.Reason ?? "");
        Assert.Equal("rewritten query", failed.Query!);

        var empty = new ScriptedBackend { Search = _ => new WebSearchFindings("  ", []) };
        Assert.Equal(WebSearchStatus.Failed, (await Service(empty).SearchAsync(Ask("今天天气"), default)).Status);
    }

    public static void ASlowSearchTimesOutAndTheAnswerGoesOn() => Wait(Async8());
    private static async Task Async8()
    {
        var backend = new ScriptedBackend { Delay = TimeSpan.FromSeconds(3) };
        var started = DateTime.UtcNow;
        var outcome = await Service(backend, timeoutSeconds: 0.2).SearchAsync(Ask("今天天气"), default);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        // The router timed out first, so the rules decided; the search then timed out too.
        Assert.Equal(WebSearchStatus.Failed, outcome.Status);
        Assert.Contains("TimeoutException", outcome.Reason ?? "");
    }

    public static void AStopWhileSearchingIsNotSwallowed() => Wait(Async9());
    private static async Task Async9()
    {
        var backend = new ScriptedBackend { Delay = TimeSpan.FromSeconds(5) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = false;
        try { await Service(backend, 30).SearchAsync(Ask("今天天气"), cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert.True(cancelled);
    }

    public static void SourcesAreDeduplicatedAndCapped() => Wait(Async10());
    private static async Task Async10()
    {
        var many = Enumerable.Range(0, 30).Select(i => new SearchCitation { Title = $"s{i % 20}", Uri = $"https://x.example/{i % 20}" }).ToList();
        many.Add(new SearchCitation { Title = "no link", Uri = "" });
        var backend = new ScriptedBackend { Search = _ => new WebSearchFindings("d", many) };
        var outcome = await Service(backend).SearchAsync(Ask("今天天气"), default);
        Assert.Equal(12, outcome.Citations.Count);
        Assert.True(outcome.Citations.All(c => !string.IsNullOrWhiteSpace(c.Uri)));
    }

    // ---------------------------------------------------------------------------------------- through the run chain

    private static OpenAICompatibleProvider Provider(HttpMessageHandler handler) => new(
        new SingleClientFactory(handler),
        new OpenAICompatibleProviderSettings
        {
            Enabled = true, ProviderId = "deepseek", Name = "Fake DeepSeek", ApiKey = "test-key",
            Endpoint = "https://upstream.example/v1/chat/completions", ModelName = "deepseek-fake",
            Models = [new ChatModelOption { Name = "Deepseek Fake", ModelName = "deepseek-fake", MaxTokens = 32768 }]
        });

    /// <summary>What the model was asked: the content of the last message in the upstream request.</summary>
    private static string Asked(CapturingHandler handler)
    {
        using var document = JsonDocument.Parse(handler.RequestBody ?? "{}");
        var messages = document.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? "";
    }

    private const string Reply = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\ndata: [DONE]\n\n";

    private static async Task<JsonElement> RunAsync(RunHarness harness, CapturingHandler handler, IWebSearchService search, string message, string mode)
    {
        using var host = new ApiTestHost(harness, harness.User, new ChatProviderCatalog([Provider(handler)]), search, new FixedClock(Now));
        var runId = RunHarness.NewRunId();
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", new Dictionary<string, object?>
        {
            ["runId"] = runId, ["conversationId"] = Guid.NewGuid(), ["message"] = message,
            ["providerId"] = "deepseek", ["modelName"] = "deepseek-fake", ["presetId"] = "default", ["searchMode"] = mode
        });
        await response.Content.ReadAsStringAsync();
        var run = await host.Http.GetAsync($"/api/v2/runs/{runId}");
        return JsonDocument.Parse(await run.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public static void AProviderWithoutSearchAnswersFromWhatWasFound() => Wait(Async11());
    private static async Task Async11()
    {
        var h = new RunHarness();
        var handler = new CapturingHandler(Reply);
        var backend = new ScriptedBackend();
        var run = await RunAsync(h, handler, Service(backend), "今天西安什么天气", "auto");

        Assert.Equal("completed", run.GetProperty("status").GetString()!);
        var body = Asked(handler);
        Assert.Contains("2026年10月19日 星期一 09:48", body);
        Assert.Contains("digest for rewritten query", body);
        Assert.Contains("[1] Source A", body);
        Assert.Contains("今天西安什么天气", body);
        Assert.False(body.Contains("请先联网查证")); // a model that cannot search is not told to

        var search = run.GetProperty("search");
        Assert.Equal("used", search.GetProperty("status").GetString()!);
        Assert.Equal("rewritten query", search.GetProperty("query").GetString()!);
        Assert.Equal(1, search.GetProperty("sources").GetInt32());
        Assert.Equal(2, run.GetProperty("providerCalls").GetProperty("search").GetInt32());
        Assert.Equal(1, run.GetProperty("output").GetProperty("citations").GetInt32());
    }

    public static void ForceSearchesEverythingAndAutoSkipsPlainWriting() => Wait(Async12());
    private static async Task Async12()
    {
        var h = new RunHarness();
        var skipHandler = new CapturingHandler(Reply);
        var backend = new ScriptedBackend { Decide = _ => "{\"search\": false, \"query\": \"\"}" };
        var skipped = await RunAsync(h, skipHandler, Service(backend), "写一篇关于秋天的文章", "auto");
        Assert.Equal("skipped", skipped.GetProperty("search").GetProperty("status").GetString()!);
        Assert.Equal(0, backend.Searches);
        Assert.False(Asked(skipHandler).Contains("联网检索结果"));
        Assert.Contains("[当前时间", Asked(skipHandler)); // the date still goes with every question

        var forceHandler = new CapturingHandler(Reply);
        var forced = await RunAsync(h, forceHandler, Service(backend), "写一篇关于秋天的文章", "force");
        Assert.Equal("used", forced.GetProperty("search").GetProperty("status").GetString()!);
        Assert.Contains("联网检索结果", Asked(forceHandler));
    }

    public static void WithTheWebOffTheProviderIsNotAskedToSearch() => Wait(Async13());
    private static async Task Async13()
    {
        var h = new RunHarness();
        var handler = new CapturingHandler(Reply);
        var backend = new ScriptedBackend();
        var run = await RunAsync(h, handler, Service(backend), "今天西安什么天气", "off");
        Assert.Equal("completed", run.GetProperty("status").GetString()!);
        Assert.Equal(0, backend.Decides + backend.Searches);
        Assert.False(Asked(handler).Contains("联网检索结果"));
        Assert.False(run.TryGetProperty("search", out var search) && search.ValueKind == JsonValueKind.Object);
    }

    public static void ASearchThatFailsStillGetsAnAnswerThatSaysSo() => Wait(Async14());
    private static async Task Async14()
    {
        var h = new RunHarness();
        var handler = new CapturingHandler(Reply);
        var backend = new ScriptedBackend { Search = _ => throw new HttpRequestException("search down") };
        var run = await RunAsync(h, handler, Service(backend), "今天西安什么天气", "auto");

        Assert.Equal("completed", run.GetProperty("status").GetString()!);
        Assert.Equal("failed", run.GetProperty("search").GetProperty("status").GetString()!);
        Assert.Contains("检索没有成功", Asked(handler));
        Assert.False(Asked(handler).Contains("联网检索结果｜"));
    }

    public static void WithNoSearchServiceTheRunSaysItIsUnavailable() => Wait(Async15());
    private static async Task Async15()
    {
        var h = new RunHarness();
        var handler = new CapturingHandler(Reply);
        var run = await RunAsync(h, handler, new UnavailableWebSearchService(), "今天西安什么天气", "auto");
        Assert.Equal("completed", run.GetProperty("status").GetString()!);
        Assert.Equal("unavailable", run.GetProperty("search").GetProperty("status").GetString()!);
        Assert.Contains("检索没有成功", Asked(handler));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
