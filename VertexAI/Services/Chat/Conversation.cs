namespace VertexAI.Services.Chat;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string? Title { get; set; }
    public string ProviderId { get; set; } = "gemini";
    public string ModelName { get; set; } = "";
    public string PresetId { get; set; } = "default";
    public string? CustomPrompt { get; set; }
    public string? HistorySummary { get; set; }
    public int TokenCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Message> Messages { get; set; } = [];
}
