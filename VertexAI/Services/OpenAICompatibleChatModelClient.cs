using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class OpenAICompatibleChatModelClient : IChatModelClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAICompatibleProviderSettings _settings;
    private readonly List<ChatHistoryEntry> _history = [];
    private string _modelName;
    private string _currentPresetId = "default";
    private string _currentSystemPrompt = "";
    private ChatSessionOptions? _options;

    public OpenAICompatibleChatModelClient(
        HttpClient httpClient,
        OpenAICompatibleProviderSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
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
            _currentSystemPrompt = ResolveSystemPrompt(options);
        }

        _options = options;
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
            throw new InvalidOperationException($"OpenAI-compatible provider '{_settings.ProviderId}' is enabled but no API key is configured.");
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

            var chunk = ExtractDelta(data);
            if (string.IsNullOrEmpty(chunk.Text))
            {
                continue;
            }

            CurrentTokenCount += EstimateTokens(chunk.Text);
            yield return chunk;
        }
    }

    private Dictionary<string, object?> BuildRequestBody(ChatModelRequest request)
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
            content = BuildUserContent(request.Message, request.Attachments)
        });

        CurrentTokenCount = messages.Sum(message => EstimateTokens(JsonSerializer.Serialize(message)));

        var body = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["stream"] = true,
            ["messages"] = messages,
            // 显式限制最大输出 Token 数，防止被接口默认值提前截断
            ["max_tokens"] = 20480
        };

        ApplyThinkingOptions(body);
        return body;
    }

    private string ResolveSystemPrompt(ChatSessionOptions options)
    {
        if (options.PresetId == "custom" && !string.IsNullOrWhiteSpace(options.CustomPrompt))
        {
            return options.CustomPrompt;
        }

        if (options.PresetId == "default" && !string.IsNullOrWhiteSpace(options.DefaultAssistantPrompt))
        {
            return options.DefaultAssistantPrompt;
        }

        return Presets.FirstOrDefault(p => p.Id == options.PresetId)?.Prompt ?? "";
    }

    private void ApplyThinkingOptions(Dictionary<string, object?> body)
    {
        var model = ModelOptions.FirstOrDefault(item => item.ModelName == _modelName);
        var thinking = model?.Thinking;
        if (thinking == null)
        {
            return;
        }

        var enabled = thinking.FixedEnabled
            || (_options?.ThinkingEnabled ?? thinking.Default != "off");
        var requestedLevel = NormalizeThinkingLevel(thinking, _options?.ThinkingLevel);
        var budget = NormalizeThinkingBudget(thinking, _options?.ThinkingBudget);

        switch (thinking.Kind)
        {
            case "deepseek-effort":
                if (!enabled || requestedLevel == "off")
                {
                    body["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" };
                    return;
                }

                body["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
                body["reasoning_effort"] = requestedLevel == "max" ? "max" : "high";
                return;

            case "qwen-budget":
                body["enable_thinking"] = enabled;
                if (enabled && budget is > 0)
                {
                    body["thinking_budget"] = budget;
                }
                return;

            case "zhipu-toggle":
                body["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = enabled ? "enabled" : "disabled"
                };
                return;

            case "kimi-fixed":
                body["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["keep"] = "all"
                };
                return;

            case "kimi-toggle":
                if (!enabled)
                {
                    body["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" };
                    return;
                }

                body["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["keep"] = "all"
                };
                return;

            case "openai-effort":
                if (enabled && requestedLevel != "off")
                {
                    body["reasoning_effort"] = requestedLevel ?? thinking.Default ?? "medium";
                }
                return;
        }
    }

    private static string? NormalizeThinkingLevel(ChatThinkingConfig thinking, string? requested)
    {
        if (thinking.Options.Count == 0)
        {
            return thinking.Default;
        }

        if (!string.IsNullOrWhiteSpace(requested)
            && thinking.Options.Any(option => option.Value == requested))
        {
            return requested;
        }

        return thinking.Default;
    }

    private static int? NormalizeThinkingBudget(ChatThinkingConfig thinking, int? requested)
    {
        if (requested is > 0)
        {
            return requested;
        }

        return thinking.DefaultBudget ?? thinking.Budgets.FirstOrDefault();
    }

    private static object BuildUserContent(
        string message,
        IReadOnlyCollection<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return message;
        }

        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            content.Add(new { type = "text", text = message });
        }

        foreach (var attachment in attachments)
        {
            if (IsImage(attachment))
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{attachment.MimeType};base64,{attachment.Base64Data}"
                    }
                });
                continue;
            }

            content.Add(new
            {
                type = "text",
                text = CreateAttachmentText(attachment)
            });
        }

        return content;
    }

    private static bool IsImage(ChatAttachment attachment) =>
        attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string CreateAttachmentText(ChatAttachment attachment)
    {
        var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment" : attachment.FileName;
        if (!IsTextLike(attachment.MimeType))
        {
            return $"[Attached file: {fileName} ({attachment.MimeType}). This provider cannot read this binary file directly.]";
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(attachment.Base64Data));
            return $"[Attached file: {fileName} ({attachment.MimeType})]\n{text}";
        }
        catch (FormatException)
        {
            return $"[Attached file: {fileName} ({attachment.MimeType}) could not be decoded.]";
        }
    }

    private static bool IsTextLike(string mimeType) =>
        mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("application/x-yaml", StringComparison.OrdinalIgnoreCase);

    private static ChatChunk ExtractDelta(string data)
    {
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return new ChatChunk { Text = "" };
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
        {
            return new ChatChunk { Text = "" };
        }

        if (delta.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            return new ChatChunk
            {
                Text = reasoningContent.GetString() ?? "",
                IsThinking = true
            };
        }

        if (delta.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            return new ChatChunk
            {
                Text = reasoning.GetString() ?? "",
                IsThinking = true
            };
        }

        return new ChatChunk
        {
            Text = delta.TryGetProperty("content", out var content)
                ? content.GetString() ?? ""
                : ""
        };
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);
}
