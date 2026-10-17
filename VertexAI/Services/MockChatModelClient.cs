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
            Description = "Deterministic provider for UI and API tests without cloud credentials.",
            SupportsThinking = true,
            MaxTokens = 8192
        },
        new()
        {
            Name = "Mock Detailed",
            ModelName = "mock-detailed",
            Description = "Deterministic provider with a slightly richer streamed response.",
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
            Prompt = "You are a deterministic mock assistant used for testing.",
            Description = "Deterministic test assistant"
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

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request) =>
        StreamChatAsync(request, CancellationToken.None);

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var trimmed = request.Message.Trim();

        // "/slow" streams one numbered line every 100 ms until it ends or is cancelled. It exists so a stop,
        // a dropped connection or a reload can be exercised end to end against a real stream.
        if (trimmed.StartsWith("/slow", StringComparison.Ordinal))
        {
            for (var line = 1; line <= 200; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatChunk { Text = $"line {line}\n", IsThinking = false };
                await Task.Delay(100, cancellationToken);
            }

            yield break;
        }

        // Scripted replies for the UI: rich markdown, a thinking stream with citations, and a provider failure.
        if (trimmed.StartsWith("/md", StringComparison.Ordinal))
        {
            foreach (var part in MarkdownSample.Chunks())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatChunk { Text = part, IsThinking = false };
                await Task.Delay(15, cancellationToken);
            }

            yield break;
        }

        if (trimmed.StartsWith("/think", StringComparison.Ordinal))
        {
            yield return new ChatChunk { Text = "先确认问题的范围。", IsThinking = true };
            yield return new ChatChunk { Text = "再核对两个来源是否一致。", IsThinking = true };
            await Task.Delay(30, cancellationToken);
            yield return new ChatChunk { Text = "结论：两个来源一致，详见 [1] 和 [2]。", IsThinking = false };
            yield return new ChatChunk
            {
                Text = "",
                Citations =
                [
                    new SearchCitation { Title = "示例来源一", Uri = "https://example.com/one" },
                    new SearchCitation { Title = "示例来源二", Uri = "https://example.org/two" }
                ]
            };
            yield break;
        }

        if (trimmed.StartsWith("/fail", StringComparison.Ordinal))
        {
            yield return new ChatChunk { Text = "这段内容在失败前已经生成。", IsThinking = false };
            await Task.Delay(20, cancellationToken);
            throw new InvalidOperationException("mock provider failure");
        }

        var attachmentCount = request.Attachments.Count;
        var historyCount = _history.Count;
        var searchMode = SearchModes.Normalize(request.SearchMode);
        var searchText = SearchModes.EnablesWebSearch(searchMode) ? $" Search mode: {searchMode}." : "";
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

        if (attachmentCount > 0 || SearchModes.EnablesWebSearch(searchMode) || historyCount > 0 || !string.IsNullOrWhiteSpace(promptText))
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

/// <summary>A fixed markdown reply with a table, lists, inline code and two code blocks (one long enough to collapse).</summary>
internal static class MarkdownSample
{
    public static IEnumerable<string> Chunks()
    {
        var lines = new List<string>
        {
            "按季度汇总如下：\n\n",
            "| 季度 | 订单数 | 销售额（万元） | 环比 |\n|---|---:|---:|---:|\n",
            "| Q1 | 3,204 | 412.6 | — |\n| Q2 | 3,587 | 468.1 | +13.5% |\n| Q3 | 3,402 | 441.9 | −5.6% |\n\n",
            "- 全年销售额 **1,858.9** 万元，第四季度占 28.9%。\n- 使用 `groupby` 即可复现。\n\n",
            "```python\ndf.groupby(df[\"日期\"].dt.quarter)[\"金额\"].sum()\n```\n\n",
            "下面是一个较长的封装：\n\n```typescript\n"
        };
        foreach (var line in lines) yield return line;
        for (var i = 1; i <= 36; i++) yield return $"const value{i} = {i}; // line {i}\n";
        yield return "```\n\n结束。\n";
    }
}
