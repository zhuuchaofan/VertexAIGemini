using System.Diagnostics;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VertexAI.Services;

/// <summary>
/// Counts the requests the Gemini SDK really sends to a local fake upstream. The run lifecycle promises
/// at most one provider call per logical call, which only holds if the SDK never retries on its own.
/// </summary>
internal static class GeminiRetryTests
{
    private static readonly GenerateContentConfig Config = new();

    public static void ProductionOptionsAllowExactlyOneAttempt()
    {
        var options = GeminiHttpOptions.Create();
        Assert.NotNull(options.RetryOptions);
        Assert.Equal(1, options.RetryOptions!.Attempts ?? 0);
    }

    public static void EveryCallIssuesExactlyOneUpstreamRequest()
    {
        using var upstream = FakeUpstream.Start();
        foreach (var mode in new[] { "503", "429", "500", "reset" })
        {
            upstream.Mode = mode;
            foreach (var (name, call) in Calls())
            {
                upstream.Requests = 0;
                var client = new Client(apiKey: "test-key", httpOptions: GeminiHttpOptions.Create(upstream.Url), clientOptions: GeminiHttpOptions.CreateClientOptions());
                try { call(client).GetAwaiter().GetResult(); } catch (Exception) { /* the failure itself is expected */ }
                Assert.Equal(1, upstream.Requests);
                Console.WriteLine($"  {mode,-5} {name,-11} upstream requests = {upstream.Requests}");
            }
        }
    }

    public static void HarnessDetectsRetriesWhenTheyAreEnabled()
    {
        // Guards the test above against passing vacuously: with retries turned on the same harness must see more.
        using var upstream = FakeUpstream.Start();
        upstream.Mode = "503";
        var retrying = GeminiHttpOptions.Create(upstream.Url);
        retrying.RetryOptions = new HttpRetryOptions { Attempts = 3, InitialDelay = 0.01, MaxDelay = 0.02 };
        var client = new Client(apiKey: "test-key", httpOptions: retrying);
        try { Generate(client).GetAwaiter().GetResult(); } catch (Exception) { }
        Assert.True(upstream.Requests > 1);
    }

    public static void CancellingTheTokenStopsTheStreamAndClosesTheUpstreamRequest()
    {
        using var upstream = FakeUpstream.Start();
        upstream.Mode = "slow-stream";
        var client = new Client(apiKey: "test-key", httpOptions: GeminiHttpOptions.Create(upstream.Url), clientOptions: GeminiHttpOptions.CreateClientOptions());
        using var cts = new CancellationTokenSource();
        var received = 0;
        var cancelledAt = Stopwatch.StartNew();
        var outcome = "completed";
        try
        {
            var stream = client.Models.GenerateContentStreamAsync("gemini-test", "hi", Config, cts.Token);
            Consume(stream, () => { if (++received == 3) { cancelledAt.Restart(); cts.Cancel(); } }).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { outcome = "cancelled"; }

        Console.WriteLine($"  outcome={outcome} received={received} cancelToEndMs={cancelledAt.ElapsedMilliseconds}");
        var closed = SpinUntil(() => upstream.AbortedRequests >= 1, 5000);
        var closeLatencyMs = cancelledAt.ElapsedMilliseconds;
        Console.WriteLine($"  upstream observed close: {closed} ({closeLatencyMs}ms after cancel)");
        Assert.Equal("cancelled", outcome);
        Assert.True(closed);
        // With .NET's default 2 s response drain this is ~2000 ms; the production options must close immediately.
        Assert.True(closeLatencyMs < 500);
        Assert.Equal(1, upstream.Requests);
    }

    private static async Task Consume(IAsyncEnumerable<GenerateContentResponse> stream, Action onItem)
    {
        await foreach (var _ in stream) onItem();
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        while (System.Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static (string Name, Func<Client, Task> Call)[] Calls() =>
    [
        ("stream", Stream),
        ("generate", Generate),
        ("countTokens", CountTokens)
    ];

    private static Task Stream(Client client) => Consume(client.Models.GenerateContentStreamAsync("gemini-test", "hi", Config), () => { });
    private static Task Generate(Client client) => client.Models.GenerateContentAsync("gemini-test", "hi", Config);
    private static Task CountTokens(Client client) => client.Models.CountTokensAsync("gemini-test", "hi");

    private sealed class FakeUpstream : IDisposable
    {
        private readonly WebApplication _app;
        private int _requests;
        private int _aborted;

        public string Mode { get; set; } = "503";
        public string Url { get; }
        public int Requests { get => Volatile.Read(ref _requests); set => Volatile.Write(ref _requests, value); }
        public int AbortedRequests => Volatile.Read(ref _aborted);

        private FakeUpstream(WebApplication app, string url)
        {
            _app = app;
            Url = url;
        }

        public static FakeUpstream Start()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            FakeUpstream? self = null;
            app.Run(async context =>
            {
                Interlocked.Increment(ref self!._requests);
                switch (self.Mode)
                {
                    case "reset":
                        context.Abort();
                        return;
                    case "slow-stream":
                        context.Response.ContentType = "text/event-stream";
                        try
                        {
                            for (var i = 0; i < 2000; i++)
                            {
                                await context.Response.WriteAsync(
                                    "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"x\"}]}}]}\n\n",
                                    context.RequestAborted);
                                await context.Response.Body.FlushAsync(context.RequestAborted);
                                await Task.Delay(30, context.RequestAborted);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref self._aborted);
                        }
                        return;
                    default:
                        context.Response.StatusCode = int.Parse(self.Mode);
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(
                            "{\"error\":{\"code\":" + self.Mode + ",\"message\":\"fake\",\"status\":\"UNAVAILABLE\"}}");
                        return;
                }
            });
            app.StartAsync().GetAwaiter().GetResult();
            var address = app.Urls.First();
            self = new FakeUpstream(app, address);
            return self;
        }

        public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();
    }
}
