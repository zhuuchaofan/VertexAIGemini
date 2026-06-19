using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class MockChatModelClient : IChatModelClient
{
    private readonly List<ChatHistoryEntry> _history = [];
    private string _modelName = "mock-fast";
    private string _currentPresetId = "default";
    private string _currentSystemPrompt = "";

    public int CurrentTokenCount { get; private set; }
    public string CurrentModelName => _modelName;
    public string CurrentPresetId => _currentPresetId;
    public string CurrentCustomPrompt => _currentPresetId == "custom" ? _currentSystemPrompt : "";

    public IReadOnlyList<ChatModelOption> ModelOptions { get; } =
    [
        new()
        {
            Name = "Mock Fast",
            ModelName = "mock-fast",
            Description = "Local deterministic provider for UI and API testing without cloud credentials.",
            SupportsThinking = true,
            MaxTokens = 8192
        },
        new()
        {
            Name = "Mock Detailed",
            ModelName = "mock-detailed",
            Description = "Local deterministic provider with a slightly richer streamed response.",
            SupportsThinking = true,
            MaxTokens = 32768
        }
    ];

    public IReadOnlyList<PromptPresetConfig> Presets { get; } =
    [
        new()
        {
            Id = "default",
            Name = "Mock Assistant",
            Prompt = "You are a local mock assistant used for testing.",
            Description = "Deterministic local test assistant"
        },
        new()
        {
            Id = "custom",
            Name = "Custom",
            Prompt = "",
            Description = "Use a custom system prompt for this test session"
        }
    ];

    public Task ConfigureAsync(ChatSessionOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ModelName))
        {
            _modelName = ModelOptions.Any(m => m.ModelName == options.ModelName)
                ? options.ModelName
                : ModelOptions[0].ModelName;
        }

        if (!string.IsNullOrWhiteSpace(options?.PresetId))
        {
            _currentPresetId = options.PresetId;
            _currentSystemPrompt = options.PresetId switch
            {
                "custom" => options.CustomPrompt ?? "",
                "default" when !string.IsNullOrWhiteSpace(options.DefaultAssistantPrompt) => options.DefaultAssistantPrompt,
                _ => ""
            };
        }

        return Task.CompletedTask;
    }

    public Task LoadHistoryAsync(IReadOnlyCollection<ChatHistoryEntry> messages)
    {
        _history.Clear();
        _history.AddRange(messages);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request)
    {
        var trimmed = request.Message.Trim();
        var attachmentCount = request.Attachments.Count;
        var historyCount = _history.Count;
        var searchText = request.EnableSearch ? " Search is enabled." : "";
        var promptText = string.IsNullOrWhiteSpace(_currentSystemPrompt)
            ? ""
            : $" Custom prompt length: {_currentSystemPrompt.Length}.";

        CurrentTokenCount = EstimateTokens(trimmed) + attachmentCount * 256 + historyCount * 16;

        await Task.Yield();

        yield return new ChatChunk
        {
            Text = $"Mock provider {_modelName} received ",
            IsThinking = false
        };

        yield return new ChatChunk
        {
            Text = string.IsNullOrWhiteSpace(trimmed) ? "an attachment-only request." : $"\"{trimmed}\".",
            IsThinking = false
        };

        if (attachmentCount > 0 || request.EnableSearch || historyCount > 0 || !string.IsNullOrWhiteSpace(promptText))
        {
            yield return new ChatChunk
            {
                Text = $" Attachments: {attachmentCount}. History messages: {historyCount}.{searchText}{promptText}",
                IsThinking = false
            };
        }
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);
}
