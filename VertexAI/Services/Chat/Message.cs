namespace VertexAI.Services.Chat;

public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? ThinkingContent { get; set; }
    public string? AttachmentsJson { get; set; }

    /// <summary>The run that produced this message. Absent on messages written before runs existed.</summary>
    public string? RunId { get; set; }

    /// <summary>Web sources the reply cites, as JSON. Absent when there were none (or before they were kept).</summary>
    public string? CitationsJson { get; set; }

    /// <summary>"final" or "partial". Absent means final.</summary>
    public string? Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
