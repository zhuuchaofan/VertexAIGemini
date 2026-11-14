namespace VertexAI.Services.Chat;

public sealed class ChatModelOption
{
    public string Name { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool SupportsThinking { get; set; }
    public bool SupportsSampling { get; set; } = true;
    public ChatThinkingConfig? Thinking { get; set; }
    public int MaxTokens { get; set; } = 1048576;

    /// <summary>Answers with pictures (and a little text) instead of a conversation: no web search, its own daily allowance.</summary>
    public bool GeneratesImages { get; set; }
}

public sealed class ChatThinkingConfig
{
    public string Kind { get; set; } = "toggle";
    public bool CanDisable { get; set; } = true;
    public bool FixedEnabled { get; set; }
    public bool IncludeThoughts { get; set; } = true;
    public string? Default { get; set; }
    public List<ChatThinkingOption> Options { get; set; } = [];
    public List<int> Budgets { get; set; } = [];
    public int? DefaultBudget { get; set; }
}

public sealed class ChatThinkingOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}
