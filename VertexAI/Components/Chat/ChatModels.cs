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

    /// <summary>
    /// 附加的图片列表
    /// </summary>
    public List<ImageAttachment>? Attachments { get; set; }
}

/// <summary>
/// 图片附件模型
/// </summary>
public class ImageAttachment
{
    public required string Base64Data { get; init; }
    public required string MimeType { get; init; }
    public string? FileName { get; init; }

    /// <summary>
    /// 用于 UI 显示的 Data URL
    /// </summary>
    public string DataUrl => $"data:{MimeType};base64,{Base64Data}";
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
    public string? Description { get; set; }
}

