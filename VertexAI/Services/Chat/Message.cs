namespace VertexAI.Services.Chat;

public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? ThinkingContent { get; set; }
    public string? AttachmentsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
