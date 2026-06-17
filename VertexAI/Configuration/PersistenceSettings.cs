namespace VertexAI.Configuration;

public sealed class PersistenceSettings
{
    public string UserSettingsProvider { get; set; } = "postgres";
    public string ConversationProvider { get; set; } = "postgres";
    public string? FirestoreProjectId { get; set; }
}
