using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VertexAI.Api;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>
/// The three settings layers over real HTTP and the Firestore emulator: my defaults, this conversation, and the
/// immutable copy each run keeps.
/// </summary>
internal static class SettingsTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Host : IDisposable
    {
        private readonly WebApplication _app;
        public RunHarness Harness { get; }
        public HttpClient Http { get; }

        public Host(RunHarness harness, AuthenticatedUser? user, bool enabled = true)
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
            Http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) };
        }

        public void Dispose()
        {
            Http.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static object Settings(
        string model = "scripted", string preset = "default", string? custom = null, string search = "auto",
        object? thinking = null, object? advanced = null) => new
    {
        providerId = "scripted", modelName = model, presetId = preset, customPrompt = custom,
        searchMode = search, thinking, advanced
    };

    private static async Task<HttpResponseMessage> SendRunAsync(Host host, Guid conversation, string runId, object options)
    {
        var json = JsonSerializer.SerializeToElement(options, Web);
        var body = new Dictionary<string, object?> { ["runId"] = runId, ["conversationId"] = conversation, ["message"] = "/ok hello" };
        foreach (var property in json.EnumerateObject())
        {
            // A run takes the thinking choice as flat fields; the settings layers keep it nested.
            if (property.Name == "thinking" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var inner in property.Value.EnumerateObject())
                    body["thinking" + char.ToUpperInvariant(inner.Name[0]) + inner.Name[1..]] = inner.Value;
            }
            else
            {
                body[property.Name] = property.Value;
            }
        }
        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", body);
        await response.Content.ReadAsStringAsync(); // the stream ends with the run
        return response;
    }

    private static async Task<JsonElement> GetAsync(Host host, string path)
    {
        var response = await host.Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> RejectionAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("invalid_settings", error.GetProperty("code").GetString()!);
        return error.GetProperty("message").GetString()!;
    }

    // ---------------------------------------------------------------------------------------- tests

    public static void DefaultsAreStoredPerAccountAndNeverCarryAPrompt() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        using var host = new Host(h, h.User);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(host, "/api/v2/me/settings")).GetProperty("defaults").ValueKind);

        var put = await host.Http.PutAsJsonAsync("/api/v2/me/settings",
            Settings("scripted-levels", "custom", custom: "secret prompt", search: "force", thinking: new { level = "high" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var defaults = (await GetAsync(host, "/api/v2/me/settings")).GetProperty("defaults");
        Assert.Equal("scripted-levels", defaults.GetProperty("modelName").GetString()!);
        Assert.Equal("force", defaults.GetProperty("searchMode").GetString()!);
        Assert.Equal("high", defaults.GetProperty("thinking").GetProperty("level").GetString()!);
        Assert.False(defaults.TryGetProperty("customPrompt", out var prompt) && prompt.ValueKind == JsonValueKind.String); // a prompt belongs to a conversation

        // Another account sees none of it.
        var other = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "other@example.test");
        using var otherHost = new Host(h, other);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(otherHost, "/api/v2/me/settings")).GetProperty("defaults").ValueKind);
    }

    public static void SettingsThatDoNotFitTheModelAreRefusedNotSaved() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        using var host = new Host(h, h.User);
        async Task<string> Refused(object body) => await RejectionAsync(await host.Http.PutAsJsonAsync("/api/v2/me/settings", body));

        Assert.Contains("model", await Refused(Settings("no-such-model")));
        Assert.Contains("search", await Refused(Settings(search: "sometimes")));
        Assert.Contains("preset", await Refused(Settings(preset: "nope")));
        Assert.Contains("thinking", await Refused(Settings("scripted", thinking: new { level = "high" }))); // this model has no thinking
        Assert.Contains("level", await Refused(Settings("scripted-levels", thinking: new { level = "extreme" })));
        Assert.Contains("budget", await Refused(Settings("scripted-budget", thinking: new { enabled = true, budget = 0 })));
        Assert.Contains("temperature", await Refused(Settings("scripted-levels", advanced: new { temperature = 0.5 }))); // sampling ignored there
        Assert.Contains("output limit", await Refused(Settings("scripted", advanced: new { maxOutputTokens = 5000 }))); // above this model's 4096
        Assert.Contains("filter", await Refused(Settings("scripted", advanced: new { safetyThreshold = "OFF" }))); // not allowed for this account
        await Refused(Settings("scripted", advanced: new { temperature = 9 }));

        Assert.Equal(JsonValueKind.Null, (await GetAsync(host, "/api/v2/me/settings")).GetProperty("defaults").ValueKind);

        // What does fit is accepted, including a budget model with thinking switched off.
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PutAsJsonAsync("/api/v2/me/settings",
            Settings("scripted-budget", thinking: new { enabled = false }, advanced: new { temperature = 0.3, maxOutputTokens = 2000 }))).StatusCode);
    }

    public static void AConversationKeepsItsOwnSettingsAndOnlyItsOwner() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        using var host = new Host(h, h.User);
        var conversation = Guid.NewGuid();

        // Not there yet: nothing to attach settings to.
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Http.PutAsJsonAsync($"/api/v2/conversations/{conversation}/settings", Settings())).StatusCode);

        await SendRunAsync(host, conversation, RunHarness.NewRunId(), Settings("scripted-levels", thinking: new { level = "high" }));
        var opened = (await GetAsync(host, $"/api/v2/conversations/{conversation}")).GetProperty("settings");
        Assert.Equal("scripted-levels", opened.GetProperty("modelName").GetString()!); // a send leaves its settings behind
        Assert.Equal("high", opened.GetProperty("thinking").GetProperty("level").GetString()!);

        var put = await host.Http.PutAsJsonAsync($"/api/v2/conversations/{conversation}/settings",
            Settings("scripted-budget", "custom", custom: "be brief", search: "off", thinking: new { enabled = true, budget = 500 }));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var changed = (await GetAsync(host, $"/api/v2/conversations/{conversation}")).GetProperty("settings");
        Assert.Equal("scripted-budget", changed.GetProperty("modelName").GetString()!);
        Assert.Equal("be brief", changed.GetProperty("customPrompt").GetString()!);
        Assert.Equal("off", changed.GetProperty("searchMode").GetString()!);
        Assert.Equal(500, changed.GetProperty("thinking").GetProperty("budget").GetInt32());

        // Defaults are a separate layer: nothing was written there.
        Assert.Equal(JsonValueKind.Null, (await GetAsync(host, "/api/v2/me/settings")).GetProperty("defaults").ValueKind);

        // Someone else cannot touch it, and cannot tell it exists.
        var other = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "other@example.test");
        using var otherHost = new Host(h, other);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherHost.Http.PutAsJsonAsync($"/api/v2/conversations/{conversation}/settings", Settings())).StatusCode);
        Assert.Equal("be brief", (await GetAsync(host, $"/api/v2/conversations/{conversation}")).GetProperty("settings").GetProperty("customPrompt").GetString()!);
    }

    public static void ARunKeepsItsOwnCopyWhateverIsChangedAfterwards() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness();
        using var host = new Host(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();
        await SendRunAsync(host, conversation, runId,
            Settings("scripted-levels", search: "force", thinking: new { level = "high" }));

        var before = await GetAsync(host, $"/api/v2/runs/{runId}");
        Assert.Equal("high", before.GetProperty("requested").GetProperty("thinkingLevel").GetString()!);
        Assert.Equal("force", before.GetProperty("requested").GetProperty("searchMode").GetString()!);
        Assert.Equal("scripted-levels", before.GetProperty("effective").GetProperty("modelName").GetString()!);

        // Change both editable layers; the finished run must not move.
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PutAsJsonAsync("/api/v2/me/settings",
            Settings("scripted-budget", search: "off", thinking: new { enabled = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Http.PutAsJsonAsync($"/api/v2/conversations/{conversation}/settings",
            Settings("scripted", search: "off"))).StatusCode);
        await SendRunAsync(host, conversation, RunHarness.NewRunId(), Settings("scripted", search: "off"));

        var after = await GetAsync(host, $"/api/v2/runs/{runId}");
        Assert.Equal(before.GetProperty("requested").GetRawText(), after.GetProperty("requested").GetRawText());
        Assert.Equal(before.GetProperty("effective").GetRawText(), after.GetProperty("effective").GetRawText());

        // The conversation's runs list shows the first run with its own settings, the second with its own.
        var runs = (await GetAsync(host, $"/api/v2/conversations/{conversation}")).GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("scripted-levels,scripted", string.Join(',', runs.Select(r => r.GetProperty("requested").GetProperty("modelName").GetString()!)));
    }

    public static void AConversationFromBeforeSettingsExistedFallsBackToItsOlderColumns() => Wait(Async5());
    private static async Task Async5()
    {
        var h = new RunHarness();
        using var host = new Host(h, h.User);
        var conversation = Guid.NewGuid();
        await SendRunAsync(host, conversation, RunHarness.NewRunId(), Settings("scripted-levels", "custom", custom: "old prompt"));

        // Make it look like a conversation written before the settings field existed.
        await h.Db.Collection("conversations").Document(conversation.ToString("D")).UpdateAsync("settings", FieldValue.Delete);

        var settings = (await GetAsync(host, $"/api/v2/conversations/{conversation}")).GetProperty("settings");
        Assert.Equal("scripted-levels", settings.GetProperty("modelName").GetString()!);
        Assert.Equal("custom", settings.GetProperty("presetId").GetString()!);
        Assert.Equal("old prompt", settings.GetProperty("customPrompt").GetString()!);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("searchMode").ValueKind); // unknown stays unknown: the client uses its defaults
    }

    public static void SettingsRoutesAreDarkUntilEnabledAndNeedASignedInUser() => Wait(Async6());
    private static async Task Async6()
    {
        var h = new RunHarness();
        using (var dark = new Host(h, h.User, enabled: false))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.GetAsync("/api/v2/me/settings")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.PutAsJsonAsync("/api/v2/me/settings", Settings())).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await dark.Http.PutAsJsonAsync($"/api/v2/conversations/{Guid.NewGuid()}/settings", Settings())).StatusCode);
        }

        using var anonymous = new Host(h, user: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/v2/me/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.PutAsJsonAsync("/api/v2/me/settings", Settings())).StatusCode);
    }

    public static void RolesAreKeptPerAccountAndValidated() => Wait(Async7());
    private static async Task Async7()
    {
        var h = new RunHarness();
        using var host = new Host(h, h.User);
        Assert.Equal(0, (await GetAsync(host, "/api/v2/me/roles")).GetProperty("roles").GetArrayLength());

        var created = await host.Http.PostAsJsonAsync("/api/v2/me/roles", new { name = "  写作搭子 ", prompt = " 你是我的写作搭子 " });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var role = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("role");
        var id = role.GetProperty("id").GetString()!;
        Assert.Equal("写作搭子", role.GetProperty("name").GetString()!);
        Assert.Equal("你是我的写作搭子", role.GetProperty("prompt").GetString()!);

        var updated = await host.Http.PutAsJsonAsync($"/api/v2/me/roles/{id}", new { name = "写作搭子 2", prompt = "新的提示词" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var listed = (await GetAsync(host, "/api/v2/me/roles")).GetProperty("roles");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal("新的提示词", listed[0].GetProperty("prompt").GetString()!);

        // Another account neither sees nor touches it.
        var other = new AuthenticatedUser(Guid.NewGuid(), "uid-" + Guid.NewGuid().ToString("N"), "other@example.test");
        using (var otherHost = new Host(h, other))
        {
            Assert.Equal(0, (await GetAsync(otherHost, "/api/v2/me/roles")).GetProperty("roles").GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound,
                (await otherHost.Http.PutAsJsonAsync($"/api/v2/me/roles/{id}", new { name = "x", prompt = "y" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherHost.Http.DeleteAsync($"/api/v2/me/roles/{id}")).StatusCode);
        }

        // Refused: empty or oversized fields, an id that cannot name a role.
        foreach (var bad in new object[]
        {
            new { name = "", prompt = "p" }, new { name = "n", prompt = "  " },
            new { name = new string('长', UserRoleValidator.MaxNameChars + 1), prompt = "p" },
            new { name = "n", prompt = new string('x', ChatSettingsDocument.MaxCustomPromptChars + 1) }
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Http.PostAsJsonAsync("/api/v2/me/roles", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.DeleteAsync("/api/v2/me/roles/not-an-id")).StatusCode);

        // The cap holds.
        for (var i = 1; i < UserRoleValidator.MaxRoles; i++)
            Assert.Equal(HttpStatusCode.OK, (await host.Http.PostAsJsonAsync("/api/v2/me/roles", new { name = "r" + i, prompt = "p" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Http.PostAsJsonAsync("/api/v2/me/roles", new { name = "extra", prompt = "p" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Http.DeleteAsync($"/api/v2/me/roles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.DeleteAsync($"/api/v2/me/roles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PostAsJsonAsync("/api/v2/me/roles", new { name = "again", prompt = "p" })).StatusCode);
    }

    public static void RoleRoutesAreDarkUntilEnabledAndNeedASignedInUser() => Wait(Async8());
    private static async Task Async8()
    {
        var h = new RunHarness();
        using (var dark = new Host(h, h.User, enabled: false))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.GetAsync("/api/v2/me/roles")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await dark.Http.PostAsJsonAsync("/api/v2/me/roles", new { name = "n", prompt = "p" })).StatusCode);
        }

        using var anonymous = new Host(h, user: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/v2/me/roles")).StatusCode);
    }
}
