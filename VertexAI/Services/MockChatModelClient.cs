using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class MockChatModelClient : IChatModelClient, IChatRunTelemetrySource
{
    private readonly List<ChatHistoryEntry> _history = [];
    private string _modelName = "mock-fast";
    private string _currentPresetId = "default";
    private string _currentSystemPrompt = "";

    public int CurrentTokenCount { get; private set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FlakyCalls = new();

    // Deterministic, and clearly the mock's own: it lets tests and the UI exercise usage and settings end to end.
    public ChatRunTelemetry Telemetry { get; } = new();
    private ChatSessionOptions? _options;

    public ChatEffectiveOptions Effective
    {
        get
        {
            var model = ModelOptions.FirstOrDefault(m => m.ModelName == _modelName);
            var sampling = model?.SupportsSampling != false;
            var thinking = model?.Thinking;
            var level = thinking is { Options.Count: > 0 }
                ? (thinking.Options.Any(o => o.Value == _options?.ThinkingLevel) ? _options!.ThinkingLevel : thinking.Default)
                : null;
            return new ChatEffectiveOptions(
                _modelName, _currentPresetId,
                thinking is null ? null : _options?.ThinkingEnabled ?? true,
                level, null,
                sampling ? _options?.Advanced?.Temperature ?? 1 : null,
                sampling ? _options?.Advanced?.TopP ?? 0.9 : null,
                _options?.Advanced?.MaxOutputTokens ?? 8192,
                _options?.Advanced?.SafetyThreshold);
        }
    }
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
            SupportsSampling = false,
            Thinking = new ChatThinkingConfig
            {
                Kind = "gemini-level",
                CanDisable = false,
                Default = "medium",
                Options =
                [
                    new() { Value = "low", Label = "低" },
                    new() { Value = "medium", Label = "中" },
                    new() { Value = "high", Label = "高" }
                ]
            },
            MaxTokens = 8192
        },
        new()
        {
            Name = "Mock Detailed",
            ModelName = "mock-detailed",
            Description = "Deterministic provider with sampling controls and no thinking.",
            MaxTokens = 32768
        },
        new()
        {
            Name = "Mock Budget",
            ModelName = "mock-budget",
            Description = "Deterministic provider with a thinking switch and token budget.",
            SupportsThinking = true,
            Thinking = new ChatThinkingConfig
            {
                Kind = "qwen-budget",
                CanDisable = true,
                Default = "on",
                Budgets = [500, 2000],
                DefaultBudget = 2000
            },
            MaxTokens = 16384
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
        _options = options;
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

            Complete(trimmed, MarkdownSample.Chunks().Sum(c => c.Length), 0);
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
            Complete(trimmed, 20, 21);
            yield break;
        }

        // Fails the first time a given message is sent and answers every time after, so a retry can be shown to work.
        if (trimmed.StartsWith("/flaky", StringComparison.Ordinal))
        {
            if (FlakyCalls.AddOrUpdate(trimmed, 1, (_, n) => n + 1) == 1)
            {
                await Task.Delay(20, cancellationToken);
                throw new InvalidOperationException("Gemini ServerError: mock upstream unavailable");
            }

            yield return new ChatChunk { Text = "这次成功了。", IsThinking = false };
            Complete(trimmed, 6, 0);
            yield break;
        }

        if (trimmed.StartsWith("/failfirst", StringComparison.Ordinal))
        {
            await Task.Delay(20, cancellationToken);
            throw new InvalidOperationException("Gemini ServerError: mock upstream unavailable");
        }

        if (trimmed.StartsWith("/fail", StringComparison.Ordinal))
        {
            yield return new ChatChunk { Text = "这段内容在失败前已经生成。", IsThinking = false };
            await Task.Delay(20, cancellationToken);
            throw new InvalidOperationException("mock provider failure");
        }

        Telemetry.GenerateCalls++;
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

        Complete(trimmed, 60, 0);
    }

    /// <summary>Reports usage the way a provider would at the end of a completed reply.</summary>
    private void Complete(string message, int outputChars, int thoughtChars)
    {
        var input = EstimateTokens(message) + _history.Count * 16;
        var output = Math.Max(1, outputChars / 4);
        var thoughts = thoughtChars > 0 ? Math.Max(1, thoughtChars / 4) : (int?)null;
        Telemetry.Usage = new ChatUsage(input, output, thoughts, input + output + (thoughts ?? 0));
        Telemetry.FinishReason = "STOP";
        Telemetry.ModelVersion = $"{_modelName}-mock";
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
