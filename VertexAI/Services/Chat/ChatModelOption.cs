namespace VertexAI.Services.Chat;

public sealed class ChatModelOption
{
    public string Name { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool SupportsThinking { get; set; }
    public int MaxTokens { get; set; } = 1048576;
}
