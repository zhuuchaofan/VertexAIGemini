namespace VertexAI.Services;

/// <summary>
/// Gemini 服务配置
/// </summary>
public class GeminiSettings
{
    public string ProjectId { get; set; } = "";
    public string Location { get; set; } = "global";
    public string ModelName { get; set; } = "gemini-3-flash-preview";
    public string? SystemPrompt { get; set; }

    // 历史管理配置
    public int MaxHistoryTokens { get; set; } = 100000;   // 最大历史 Token 数
    public int MaxHistoryRounds { get; set; } = 20;        // 最大对话轮数
    public int SummaryThreshold { get; set; } = 80000;     // 触发摘要的 Token 阈值

    // 新增配置绑定支持
    public List<GeminiModelOption> Models { get; set; } = [];
    public List<PresetItemConfig> Presets { get; set; } = [];
}

public class GeminiModelOption
{
    public string Name { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool SupportsThinking { get; set; }
    public int MaxTokens { get; set; } = 1048576;
}

public class PresetItemConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
/// 联网搜索引用来源
/// </summary>
public class SearchCitation
{
    public string Title { get; set; } = "";
    public string Uri { get; set; } = "";
}

/// <summary>
/// 聊天响应块 - 流式返回的单个片段
/// </summary>
public record ChatChunk
{
    public required string Text { get; init; }
    public bool IsThinking { get; init; }
    public List<SearchCitation>? Citations { get; init; }
}
