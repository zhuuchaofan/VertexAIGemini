using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAI.Api;
using VertexAI.Configuration;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>
/// The run API over real HTTP: real endpoints, real Kestrel, real RunService on the Firestore emulator, scripted model.
/// </summary>
internal static class RunEndpointTests
{
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private sealed class ApiHost : IDisposable
    {
        private readonly WebApplication _app;
        public string Url { get; }
        public RunHarness Harness { get; }
        public HttpClient Http { get; }

        public ApiHost(RunHarness harness, AuthenticatedUser? user, bool enabled = true)
        {
            Harness = harness;
            harness.Settings.Enabled = enabled;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IUserContext>(new FakeUserContext(user));
            builder.Services.AddSingleton(harness.Service);
            builder.Services.AddSingleton<IOptions<RunSettings>>(Options.Create(harness.Settings));
            builder.Services.AddSingleton(new GeminiSafetyPolicy(Options.Create(new GeminiSettings())));
            builder.Services.AddSingleton<IConversationStore>(harness.Conversations);
            builder.Services.AddSingleton<ISettingsStore>(new FirestoreSettingsStore(harness.Db));
            builder.Services.AddSingleton<IUserRoleStore>(new FirestoreUserRoleStore(harness.Db));
            builder.Services.AddSingleton<VertexAI.Services.Attachments.IChatAttachmentStore>(harness.Attachments);
            builder.Services.AddSingleton<IChatProviderCatalog>(new ScriptedCatalog(harness.Probe));
            _app = builder.Build();
            _app.MapRunEndpoints();
            _app.StartAsync().GetAwaiter().GetResult();
            Url = _app.Urls.First();
            Http = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(60) };
        }

        public void Dispose()
        {
            Http.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private sealed record Sse(string Name, JsonElement Data);

    private static async Task<List<Sse>> ReadEventsAsync(HttpResponseMessage response, Func<Sse, bool>? stopWhen = null, CancellationToken ct = default)
    {
        var events = new List<Sse>();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? name = null;
        var data = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:")) name = line[6..].Trim();
            else if (line.StartsWith("data:")) data.Add(line[5..].TrimStart());
            else if (line.Length == 0 && name != null)
            {
                var item = new Sse(name, JsonDocument.Parse(string.Join("\n", data)).RootElement.Clone());
                events.Add(item);
                name = null;
                data.Clear();
                if (stopWhen?.Invoke(item) == true) break;
            }
        }

        return events;
    }

    private static object Body(string message, string? runId = null, Guid? conversationId = null) => new
    {
        runId = runId ?? RunHarness.NewRunId(),
        conversationId = conversationId ?? Guid.NewGuid(),
        message
    };

    private static Task<HttpResponseMessage> PostAsync(ApiHost host, object body, CancellationToken ct = default) =>
        host.Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v2/runs") { Content = JsonContent.Create(body) },
            HttpCompletionOption.ResponseHeadersRead, ct);

    private static async Task<JsonElement> GetJsonAsync(ApiHost host, string path)
    {
        var response = await host.Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").Clone();
        Assert.Equal(code, error.GetProperty("code").GetString()!);
        return error;
    }

    // ------------------------------------------------------------------------------------------------

    public static void ARunStreamsInOrderAndEndsWithTheServersTerminalState() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        using var response = await PostAsync(host, Body("/ok hi"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType!.ToString());

        var events = await ReadEventsAsync(response);
        Assert.Equal("run", events[0].Name);
        Assert.Equal("done", events[^1].Name);
        var text = string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("content").GetString()));
        Assert.Equal("Hello world", text);
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString()!);
        // delta offsets are contiguous, so a client can detect a gap
        var offsets = events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("offset").GetInt32()).ToArray();
        Assert.SequenceEqual(new[] { 0, 6 }, offsets);
    }

    public static void ReplayingAFinishedRunStreamsItsStateAndExecutesNothing() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        var body = Body("/ok hi");
        using (var first = await PostAsync(host, body)) await ReadEventsAsync(first);

        using var again = await PostAsync(host, body);
        var events = await ReadEventsAsync(again);
        Assert.SequenceEqual(new[] { "run", "done", "end" }, events.Select(e => e.Name).ToArray());
        Assert.True(events[0].Data.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, h.Probe.StreamCalls);
    }

    public static void ADroppedConnectionIsSettledAsInterruptedAndTheTextIsKept() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        var runId = RunHarness.NewRunId();
        using var cancel = new CancellationTokenSource();
        var response = await PostAsync(host, Body("/slow go", runId), cancel.Token);
        await ReadEventsAsync(response, e => e.Name == "delta", cancel.Token);
        cancel.Cancel(); // the browser goes away
        response.Dispose();

        JsonElement run = default;
        Assert.True(await RunHarness.EventuallyAsync(() =>
        {
            run = Wait(GetJsonAsync(host, $"/api/v2/runs/{runId}"));
            return run.GetProperty("status").GetString() != "running";
        }, 10_000));
        Assert.Equal("interrupted", run.GetProperty("status").GetString()!);
        Assert.Equal("connection", run.GetProperty("statusReason").GetString()!);
        Assert.True(run.GetProperty("hasAssistantMessage").GetBoolean());
    }

    public static void TheStopEndpointEndsTheRunAndTheStreamReportsIt() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        var runId = RunHarness.NewRunId();
        using var response = await PostAsync(host, Body("/slow go", runId));
        var stream = Task.Run(() => ReadEventsAsync(response));
        await Task.Delay(500);

        var stop = await host.Http.PostAsync($"/api/v2/runs/{runId}/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var events = await stream;
        Assert.Equal("done", events[^1].Name);
        Assert.Equal("stopped", events[^1].Data.GetProperty("status").GetString()!);

        // Idempotent; and a run that does not exist (or is not yours) is simply not found.
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PostAsync($"/api/v2/runs/{runId}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.PostAsync($"/api/v2/runs/{RunHarness.NewRunId()}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.PostAsync("/api/v2/runs/not-a-guid/stop", null)).StatusCode);
    }

    public static void RequestsAreValidatedBeforeAnyRunExists() => Wait(Async5());
    private static async Task Async5()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);

        await ErrorAsync(await PostAsync(host, new { runId = "nope", conversationId = Guid.NewGuid(), message = "x" }),
            HttpStatusCode.BadRequest, "invalid_run_id");
        await ErrorAsync(await PostAsync(host, new { runId = RunHarness.NewRunId(), message = "x" }),
            HttpStatusCode.BadRequest, "invalid_conversation_id");
        await ErrorAsync(await PostAsync(host, Body("   ")), HttpStatusCode.BadRequest, "message_required");
        await ErrorAsync(await PostAsync(host, new
        {
            runId = RunHarness.NewRunId(), conversationId = Guid.NewGuid(), message = "x",
            attachments = new[] { new { base64Data = "AAAA", mimeType = "application/x-msdownload", fileName = "a.exe" } }
        }), HttpStatusCode.BadRequest, "invalid_attachments");
        await ErrorAsync(await PostAsync(host, new
        {
            runId = RunHarness.NewRunId(), conversationId = Guid.NewGuid(), message = "x",
            advanced = new { temperature = 9 }
        }), HttpStatusCode.BadRequest, "invalid_options");

        Assert.Equal(0, h.Probe.StreamCalls);
        var runs = await h.Db.Collection("runs").GetSnapshotAsync();
        Assert.Equal(0, runs.Count); // nothing was created for any of them
    }

    public static void ConflictsAndBusyConversationsAreReportedAs409() => Wait(Async6());
    private static async Task Async6()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        using (var first = await PostAsync(host, Body("/ok one", runId, conversation))) await ReadEventsAsync(first);

        await ErrorAsync(await PostAsync(host, Body("/ok different", runId, conversation)),
            HttpStatusCode.Conflict, "idempotency_mismatch");

        var busyId = RunHarness.NewRunId();
        using var running = await PostAsync(host, Body("/slow go", busyId, conversation));
        var stream = Task.Run(() => ReadEventsAsync(running));
        await Task.Delay(500);
        var error = await ErrorAsync(await PostAsync(host, Body("/ok two", RunHarness.NewRunId(), conversation)),
            HttpStatusCode.Conflict, "run_in_progress");
        Assert.Equal(busyId, error.GetProperty("extra").GetProperty("activeRunId").GetString()!);

        await host.Http.PostAsync($"/api/v2/runs/{busyId}/stop", null);
        await stream;
    }

    public static void RunRoutesAreDarkUntilEnabledAndRequireASignedInUser() => Wait(Async7());
    private static async Task Async7()
    {
        var h = new RunHarness();
        using (var dark = new ApiHost(h, h.User, enabled: false))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(dark, Body("/ok x"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.GetAsync($"/api/v2/runs/{RunHarness.NewRunId()}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.PostAsync($"/api/v2/runs/{RunHarness.NewRunId()}/stop", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.GetAsync($"/api/v2/conversations/{Guid.NewGuid()}")).StatusCode);
        }

        using var anonymous = new ApiHost(h, user: null);
        await ErrorAsync(await PostAsync(anonymous, Body("/ok x")), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync($"/api/v2/runs/{RunHarness.NewRunId()}")).StatusCode);
        Assert.Equal(0, h.Probe.StreamCalls);
    }

    public static void TheConversationEndpointReturnsMessagesRunsAndSettlesLostRuns() => Wait(Async8());
    private static async Task Async8()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        var conversation = Guid.NewGuid();
        using (var first = await PostAsync(host, Body("/ok hello", conversationId: conversation))) await ReadEventsAsync(first);

        // A second run whose process "died" right after it was admitted.
        var lostId = RunHarness.NewRunId();
        var lost = (AdmitResult.Admitted)(await h.Service.AdmitAsync(h.Request("/ok lost", lostId, conversation), default)).Result;
        Assert.True(await h.Store.BeginProviderCallAsync(lost.Run, default));
        await Task.Delay((RunHarness.LeaseSeconds + 1) * 1000);

        var detail = await GetJsonAsync(host, $"/api/v2/conversations/{conversation}");
        var messages = detail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("hello", messages[0].GetProperty("content").GetString()!.Replace("/ok ", ""));
        Assert.Equal("final", messages.First(m => m.GetProperty("role").GetString() == "model").GetProperty("status").GetString()!);
        var runs = detail.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("interrupted", runs.Single(r => r.GetProperty("runId").GetString() == lostId).GetProperty("status").GetString()!);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("activeRunId").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync($"/api/v2/conversations/{Guid.NewGuid()}")).StatusCode);
    }

    public static void CitationsAreStoredWithTheReplyOnceEachAndReturnedOnReload() => Wait(Async9());
    private static async Task Async9()
    {
        var h = new RunHarness();
        using var host = new ApiHost(h, h.User);
        var conversation = Guid.NewGuid();
        using (var response = await PostAsync(host, Body("/cite sources", conversationId: conversation))) await ReadEventsAsync(response);

        var detail = await GetJsonAsync(host, $"/api/v2/conversations/{conversation}");
        var reply = detail.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "model");
        var citations = reply.GetProperty("citations").EnumerateArray().ToList();
        Assert.Equal("https://example.com/one|https://example.org/two", string.Join('|', citations.Select(c => c.GetProperty("uri").GetString()!)));
        Assert.Equal("One", citations[0].GetProperty("title").GetString()!); // the first title for an address wins

        var user = detail.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal(0, user.GetProperty("citations").GetArrayLength());
    }

    // ---------------------------------------------------------------------------------------- rate limits

    /// <summary>Not an emulator test: the real classification and budgets, through the real middleware.</summary>
    public static void RunControlRoutesHaveTheirOwnBudgetAndCreatingARunKeepsItsLimit()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(RateLimitPolicies.Partition);
        });
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/api/v2/runs/{id}", (string id) => "snapshot");
        app.MapPost("/api/v2/runs/{id}/stop", (string id) => "stopped");
        app.MapPost("/api/v2/runs", () => "created");
        app.MapGet("/api/conversations", () => "list");
        app.StartAsync().GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        HttpStatusCode Get(string path) => Wait(http.GetAsync(path)).StatusCode;
        HttpStatusCode Post(string path) => Wait(http.PostAsync(path, null)).StatusCode;

        try
        {
            // Reading and stopping runs: 120 per minute, shared between the two routes.
            var control = Enumerable.Range(0, RateLimitPolicies.RunControlPermitsPerMinute).Select(i => i % 2 == 0 ? Get("/api/v2/runs/abc") : Post("/api/v2/runs/abc/stop")).ToList();
            Assert.True(control.All(code => code == HttpStatusCode.OK));
            Assert.Equal(HttpStatusCode.TooManyRequests, Get("/api/v2/runs/abc"));
            Assert.Equal(HttpStatusCode.TooManyRequests, Post("/api/v2/runs/abc/stop"));

            // Everything else keeps its own, unchanged budget...
            Assert.Equal(HttpStatusCode.OK, Get("/api/conversations"));
            // ...and creating a run is NOT control traffic: 29 more after the one above, then the limit applies.
            var created = Enumerable.Range(0, RateLimitPolicies.DefaultPermitsPerMinute - 1).Select(_ => Post("/api/v2/runs")).ToList();
            Assert.True(created.All(code => code == HttpStatusCode.OK));
            Assert.Equal(HttpStatusCode.TooManyRequests, Post("/api/v2/runs"));
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static void TheDevelopmentOverridesCannotBeSwitchedOnOutsideDevelopment()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DevelopmentExtensions.FakeUserKey] = "true" }).Build();

        foreach (var name in new[] { "Production", "Staging", "" })
        {
            var refused = false;
            try { new ServiceCollection().AddDevelopmentOverrides(configuration, new TestEnvironment(name)); }
            catch (InvalidOperationException) { refused = true; }
            Assert.True(refused);
        }

        var services = new ServiceCollection().AddDevelopmentOverrides(configuration, new TestEnvironment("Development"));
        Assert.True(services.Any(d => d.ServiceType == typeof(IUserContext) && d.ImplementationType == typeof(DevUserContext)));

        // Off by default: nothing is registered and nothing throws, in any environment.
        var off = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.Equal(0, new ServiceCollection().AddDevelopmentOverrides(off, new TestEnvironment("Production")).Count);
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    public static void TheDevUserHeaderOnlyYieldsAStableAnonymousUser()
    {
        var context = new DefaultHttpContext();
        Assert.Null(Wait(new DevUserContext().GetCurrentUserAsync(context)));

        context.Request.Headers[DevUserContext.HeaderName] = "alice";
        var first = Wait(new DevUserContext().GetCurrentUserAsync(context))!;
        var again = Wait(new DevUserContext().GetCurrentUserAsync(context))!;
        Assert.Equal(first.LocalUserId, again.LocalUserId);
        Assert.False(first.IsAdmin);

        context.Request.Headers[DevUserContext.HeaderName] = "bob";
        Assert.True(first.LocalUserId != Wait(new DevUserContext().GetCurrentUserAsync(context))!.LocalUserId);
    }
}
