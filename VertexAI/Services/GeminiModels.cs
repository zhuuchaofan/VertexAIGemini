using VertexAI.Services.Chat;

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
    public List<ChatModelOption> Models { get; set; } = [];
    public List<PromptPresetConfig> Presets { get; set; } = [];
}
