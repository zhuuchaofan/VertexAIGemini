namespace VertexAI.Components.Chat;

/// <summary>
/// 聊天消息模型
/// </summary>
public class ChatMessageModel
{
    public bool IsUser { get; init; }
    public string Content { get; set; } = "";
    public string? ThinkingContent { get; set; }
    public bool IsStreaming { get; set; }
}

/// <summary>
/// 对话列表项
/// </summary>
public class ConversationItem
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
}

/// <summary>
/// 预设列表项
/// </summary>
public class PresetItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
