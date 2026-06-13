using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class OpenAICompatibleSettings
{
    public bool Enabled { get; set; }
    public string ProviderId { get; set; } = "openai-compatible";
    public string Name { get; set; } = "OpenAI Compatible";
    public string Description { get; set; } = "OpenAI-compatible chat completions provider";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string ModelName { get; set; } = "gpt-4.1-mini";
    public int MaxHistoryMessages { get; set; } = 20;
    public List<ChatModelOption> Models { get; set; } = [];
    public List<PromptPresetConfig> Presets { get; set; } = [];
    public List<OpenAICompatibleProviderSettings> Providers { get; set; } = [];
}

public sealed class OpenAICompatibleProviderSettings
{
    public bool Enabled { get; set; }
    public string ProviderId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "OpenAI-compatible chat completions provider";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiKeyEnv { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int MaxHistoryMessages { get; set; } = 20;
    public List<ChatModelOption> Models { get; set; } = [];
    public List<PromptPresetConfig> Presets { get; set; } = [];
}
