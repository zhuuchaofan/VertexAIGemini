using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class OpenAICompatibleChatModelClient : IChatModelClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAICompatibleSettings _settings;
    private readonly List<ChatHistoryEntry> _history = [];
    private string _modelName;
    private string _currentPresetId = "default";
    private string _currentSystemPrompt = "";

    public OpenAICompatibleChatModelClient(
        HttpClient httpClient,
        IOptions<OpenAICompatibleSettings> settings)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        ModelOptions = OpenAICompatibleCatalog.CreateModelOptions(_settings);
        Presets = OpenAICompatibleCatalog.CreatePresets(_settings);
        _modelName = OpenAICompatibleCatalog.ResolveModelName(_settings.ModelName, ModelOptions);
        var defaultPreset = Presets.FirstOrDefault(p => p.Id == "default") ?? Presets.FirstOrDefault();
        _currentPresetId = defaultPreset?.Id ?? "default";
        _currentSystemPrompt = defaultPreset?.Prompt ?? "";
    }

    public int CurrentTokenCount { get; private set; }
    public string CurrentModelName => _modelName;
    public string CurrentPresetId => _currentPresetId;
    public string CurrentCustomPrompt => _currentPresetId == "custom" ? _currentSystemPrompt : "";
    public IReadOnlyList<ChatModelOption> ModelOptions { get; }
    public IReadOnlyList<PromptPresetConfig> Presets { get; }

    public Task ConfigureAsync(ChatSessionOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ModelName))
        {
            _modelName = OpenAICompatibleCatalog.ResolveModelName(options.ModelName, ModelOptions);
        }

        if (!string.IsNullOrWhiteSpace(options?.PresetId))
        {
            _currentPresetId = options.PresetId;
            _currentSystemPrompt = options.PresetId == "custom"
                ? options.CustomPrompt ?? ""
                : Presets.FirstOrDefault(p => p.Id == options.PresetId)?.Prompt ?? "";
        }

        return Task.CompletedTask;
    }

    public Task LoadHistoryAsync(IReadOnlyCollection<ChatHistoryEntry> messages)
    {
        _history.Clear();
        _history.AddRange(messages.TakeLast(Math.Max(0, _settings.MaxHistoryMessages)));
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request) =>
        StreamChatAsync(request, CancellationToken.None);

    private async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("OpenAI-compatible provider is enabled but no API key is configured.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(BuildRequestBody(request)),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            var text = ExtractDeltaText(data);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            CurrentTokenCount += EstimateTokens(text);
            yield return new ChatChunk { Text = text };
        }
    }

    private object BuildRequestBody(ChatModelRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(_currentSystemPrompt))
        {
            messages.Add(new { role = "system", content = _currentSystemPrompt });
        }

        messages.AddRange(_history.Select(message => new
        {
            role = message.Role == "model" ? "assistant" : message.Role,
            content = message.Role == "user"
                ? BuildUserContent(message.Content, message.Attachments ?? [])
                : message.Content
        }));

        messages.Add(new
        {
            role = "user",
            content = BuildUserContent(request.Message, request.Images)
        });

        CurrentTokenCount = messages.Sum(message => EstimateTokens(JsonSerializer.Serialize(message)));

        return new
        {
            model = _modelName,
            stream = true,
            messages
        };
    }

    private static object BuildUserContent(
        string message,
        IReadOnlyCollection<ChatImageAttachment> images)
    {
        if (images.Count == 0)
        {
            return message;
        }

        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            content.Add(new { type = "text", text = message });
        }

        foreach (var image in images)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:{image.MimeType};base64,{image.Base64Data}"
                }
            });
        }

        return content;
    }

    private static string ExtractDeltaText(string data)
    {
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return "";
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
        {
            return "";
        }

        return delta.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);
}
