using System.ComponentModel.DataAnnotations;

namespace VertexAI.Data.Entities;

/// <summary>
/// 对话实体 - 表示一个完整的对话会话
/// </summary>
public class Conversation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 用户 ID (来自 Supabase Auth)
    /// </summary>
    [Required]
    public Guid UserId { get; set; }

    /// <summary>
    /// 对话标题 (自动生成或用户定义)
    /// </summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// 使用的预设 ID
    /// </summary>
    [MaxLength(50)]
    public string PresetId { get; set; } = "default";

    /// <summary>
    /// 自定义提示词 (当 PresetId 为 custom 时使用)
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>
    /// 历史摘要 (用于长对话压缩)
    /// </summary>
    public string? HistorySummary { get; set; }

    /// <summary>
    /// 当前 Token 计数 (用于持久化)
    /// </summary>
    public int TokenCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public ICollection<Message> Messages { get; set; } = [];
}
