namespace VertexAI.Configuration;

public sealed class WorkspaceSettings
{
    public bool EnableLegacyBlazor { get; set; } = true;
    public string DefaultProviderId { get; set; } = "gemini";
}
