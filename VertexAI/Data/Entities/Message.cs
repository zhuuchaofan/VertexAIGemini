using System.ComponentModel.DataAnnotations;

namespace VertexAI.Data.Entities;

/// <summary>
/// 消息实体 - 表示对话中的单条消息
/// </summary>
public class Message
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 所属对话 ID
    /// </summary>
    [Required]
    public Guid ConversationId { get; set; }

    /// <summary>
    /// 消息角色: user / model
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "user";

    /// <summary>
    /// 消息内容
    /// </summary>
    [Required]
    public string Content { get; set; } = "";

    /// <summary>
    /// AI 思考过程 (仅 model 角色有)
    /// </summary>
    public string? ThinkingContent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public Conversation Conversation { get; set; } = null!;
}
