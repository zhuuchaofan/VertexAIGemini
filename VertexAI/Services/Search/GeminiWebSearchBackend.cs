using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Search;

/// <summary>
/// The search behind other providers: Gemini with Google Search grounding, in the same Vertex AI project. A small call
/// decides and rewrites the query; a second grounded call returns a digest and the sources it used.
/// </summary>
public sealed class GeminiWebSearchBackend : IWebSearchBackend
{
    private readonly Lazy<Client> _client;
    private readonly string _modelName;

    public GeminiWebSearchBackend(IOptions<GeminiSettings> gemini, IOptions<WebSearchSettings> settings)
    {
        var config = gemini.Value;
        _modelName = string.IsNullOrWhiteSpace(settings.Value.ModelName)
            ? GeminiCatalog.ResolveModelName(config.ModelName, GeminiCatalog.CreateModelOptions(config))
            : settings.Value.ModelName;
        _client = new Lazy<Client>(() => new Client(
            project: config.ProjectId,
            location: config.Location,
            vertexAI: true,
            httpOptions: GeminiHttpOptions.Create(),
            clientOptions: GeminiHttpOptions.CreateClientOptions()));
    }

    public async Task<string> DecideAsync(string prompt, CancellationToken cancellationToken)
    {
        var response = await _client.Value.Models.GenerateContentAsync(
            model: _modelName,
            contents: prompt,
            config: new GenerateContentConfig
            {
                ResponseMimeType = "application/json",
                Temperature = 0,
                MaxOutputTokens = 512,
                ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low }
            },
            cancellationToken: cancellationToken);
        return TextOf(response);
    }

    public async Task<WebSearchFindings> SearchAsync(string query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var response = await _client.Value.Models.GenerateContentAsync(
            model: _modelName,
            contents: WebSearchPrompt.Search(query, now),
            config: new GenerateContentConfig
            {
                Tools = [new Tool { GoogleSearch = new GoogleSearch() }],
                Temperature = 0.2,
                MaxOutputTokens = 4096,
                ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low }
            },
            cancellationToken: cancellationToken);

        var citations = new List<SearchCitation>();
        var chunks = response.Candidates?.FirstOrDefault()?.GroundingMetadata?.GroundingChunks;
        if (chunks != null)
        {
            foreach (var chunk in chunks)
            {
                if (chunk.Web is { } web)
                {
                    citations.Add(new SearchCitation { Title = web.Title ?? "", Uri = web.Uri ?? "" });
                }
            }
        }

        return new WebSearchFindings(TextOf(response), citations);
    }

    private static string TextOf(GenerateContentResponse response) =>
        string.Concat(response.Candidates?.FirstOrDefault()?.Content?.Parts?
            .Where(part => part.Thought != true && !string.IsNullOrEmpty(part.Text))
            .Select(part => part.Text) ?? []);
}

/// <summary>Deterministic stand-in for development and tests: no network, no cost.</summary>
public sealed class FakeWebSearchBackend : IWebSearchBackend
{
    public Task<string> DecideAsync(string prompt, CancellationToken cancellationToken)
    {
        var question = prompt[(prompt.LastIndexOf("用户的最后一个问题：", StringComparison.Ordinal) + "用户的最后一个问题：".Length)..];
        var needs = TimeSensitivity.LooksTimeSensitive(question);
        return Task.FromResult($"{{\"search\": {(needs ? "true" : "false")}, \"query\": \"{(needs ? "fake query" : "")}\"}}");
    }

    public Task<WebSearchFindings> SearchAsync(string query, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(new WebSearchFindings(
            $"FAKE-DIGEST for \"{query}\": the fake web says sunny, 21 degrees.",
            [new SearchCitation { Title = "Fake Source", Uri = "https://example.com/fake-source" }]));
}
