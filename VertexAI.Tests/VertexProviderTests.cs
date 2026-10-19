using System.Net;
using System.Text;
using VertexAI.Services;
using VertexAI.Services.Chat;

/// <summary>
/// A partner model on Vertex AI (Grok) through the OpenAI-compatible client with a Google token instead of a fixed key.
/// A fake upstream and a fake token source: this proves what we send and when we do not, not how Vertex behaves.
/// </summary>
internal static class VertexProviderTests
{
    private static readonly string[] Switches = ["GROK_VERTEX_ENABLED", "GROK_VERTEX_LOCATION", "PROJECT_ID", "VertexAI__ProjectId", "GOOGLE_CLOUD_PROJECT"];

    private static void WithEnvironment(Dictionary<string, string?> values, Action action)
    {
        var saved = Switches.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Switches) Environment.SetEnvironmentVariable(name, values.GetValueOrDefault(name));
            action();
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>The shipped appsettings.json, read from the repository so the test sees what is deployed.</summary>
    private static OpenAICompatibleSettings BuiltInSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "VertexAI", "appsettings.json"))) directory = directory.Parent;
        var path = Path.Combine(directory!.FullName, "VertexAI", "appsettings.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return System.Text.Json.JsonSerializer.Deserialize<OpenAICompatibleSettings>(
            document.RootElement.GetProperty("OpenAICompatible").GetRawText(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static void TheEndpointIsBuiltFromProjectAndLocation()
    {
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/p1/locations/global/endpoints/openapi/chat/completions",
            OpenAICompatibleCatalog.ResolveVertexEndpoint("", null, "p1"));
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/p1/locations/us-central1/endpoints/openapi/chat/completions",
            OpenAICompatibleCatalog.ResolveVertexEndpoint("", "us-central1", "p1"));
        Assert.Equal(
            "https://eu.example/v1/projects/p1/locations/europe-west4/x",
            OpenAICompatibleCatalog.ResolveVertexEndpoint("https://eu.example/v1/projects/{project}/locations/{location}/x", "europe-west4", "p1"));
        Assert.Equal("", OpenAICompatibleCatalog.ResolveVertexEndpoint("", "global", null)); // no project: not usable
    }

    public static void TheBuiltInGrokEntryStaysOffUntilItIsSwitchedOn()
    {
        WithEnvironment(new() { ["PROJECT_ID"] = "p1" }, () =>
        {
            var providers = OpenAICompatibleCatalog.CreateEnabledProviderSettings(BuiltInSettings());
            Assert.False(providers.Any(p => p.ProviderId == "grok-vertex"));
        });

        WithEnvironment(new() { ["PROJECT_ID"] = "p1", ["GROK_VERTEX_ENABLED"] = "true" }, () =>
        {
            var grok = OpenAICompatibleCatalog.CreateEnabledProviderSettings(BuiltInSettings()).Single(p => p.ProviderId == "grok-vertex");
            Assert.Equal(OpenAICompatibleAuth.GoogleAdc, grok.AuthMode);
            Assert.Equal("xai/grok-4.6", grok.ModelName);
            Assert.Equal("https://aiplatform.googleapis.com/v1/projects/p1/locations/global/endpoints/openapi/chat/completions", grok.Endpoint);
            Assert.Equal("", grok.ApiKey); // no key anywhere
        });

        WithEnvironment(new() { ["PROJECT_ID"] = "p1", ["GROK_VERTEX_ENABLED"] = "true", ["GROK_VERTEX_LOCATION"] = "us-central1" }, () =>
        {
            var grok = OpenAICompatibleCatalog.CreateEnabledProviderSettings(BuiltInSettings()).Single(p => p.ProviderId == "grok-vertex");
            Assert.Contains("us-central1-aiplatform.googleapis.com", grok.Endpoint);
            Assert.Contains("/locations/us-central1/", grok.Endpoint);
        });

        // Switched on but the project is unknown: not offered rather than offered and broken.
        WithEnvironment(new() { ["GROK_VERTEX_ENABLED"] = "true" }, () =>
        {
            Assert.False(OpenAICompatibleCatalog.CreateEnabledProviderSettings(BuiltInSettings()).Any(p => p.ProviderId == "grok-vertex"));
        });
    }

    public static void AGoogleTokenGoesInTheBearerHeaderAndNoApiKeyIsNeeded()
    {
        var handler = new RecordingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");
        var tokens = new FakeTokens("token-1");
        var client = new OpenAICompatibleChatModelClient(new HttpClient(handler), Grok(), tokens);

        var chunks = Run(Read(client.StreamChatAsync(new ChatModelRequest("hello", []))));

        Assert.Equal("hi", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal("Bearer token-1", handler.Authorization);
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/p1/locations/global/endpoints/openapi/chat/completions", handler.Uri);
        Assert.Contains("\"model\":\"xai/grok-4.6\"", handler.Body ?? "");
        Assert.Equal(1, tokens.Requests);
    }

    public static void AFailedTokenStopsBeforeAnyRequestIsSent()
    {
        var handler = new RecordingHandler("");
        var client = new OpenAICompatibleChatModelClient(new HttpClient(handler), Grok(), new FakeTokens(null));

        var failed = false;
        try { Run(Read(client.StreamChatAsync(new ChatModelRequest("hello", [])))); }
        catch (InvalidOperationException) { failed = true; }

        Assert.True(failed);
        Assert.Equal(0, handler.Calls);
    }

    public static void ApiKeyProvidersStillNeedTheirKey()
    {
        var handler = new RecordingHandler("");
        var settings = Grok();
        settings.AuthMode = OpenAICompatibleAuth.ApiKey;
        var client = new OpenAICompatibleChatModelClient(new HttpClient(handler), settings, new FakeTokens("unused"));

        var failed = false;
        try { Run(Read(client.StreamChatAsync(new ChatModelRequest("hello", [])))); }
        catch (InvalidOperationException) { failed = true; }

        Assert.True(failed);
        Assert.Equal(0, handler.Calls);
    }

    private static OpenAICompatibleProviderSettings Grok() => new()
    {
        Enabled = true, ProviderId = "grok-vertex", Name = "Grok (Vertex AI)", AuthMode = OpenAICompatibleAuth.GoogleAdc,
        Endpoint = "https://aiplatform.googleapis.com/v1/projects/p1/locations/global/endpoints/openapi/chat/completions",
        ModelName = "xai/grok-4.6",
        Models = [new ChatModelOption { Name = "Grok 4.6", ModelName = "xai/grok-4.6", MaxTokens = 128000 }]
    };

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static async Task<List<ChatChunk>> Read(IAsyncEnumerable<ChatChunk> chunks)
    {
        var list = new List<ChatChunk>();
        await foreach (var chunk in chunks) list.Add(chunk);
        return list;
    }

    private sealed class FakeTokens(string? token) : IGoogleAccessTokenSource
    {
        public int Requests { get; private set; }

        public Task<string> GetAsync(CancellationToken cancellationToken)
        {
            Requests++;
            return token is null ? throw new InvalidOperationException("no credentials") : Task.FromResult(token);
        }
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public string? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Uri = request.RequestUri?.ToString();
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8) };
        }
    }
}
