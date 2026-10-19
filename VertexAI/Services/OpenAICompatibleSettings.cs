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
    /// <summary>"api-key" (default, a fixed key) or "google-adc" (a short-lived Google token from the runtime service account).</summary>
    public string AuthMode { get; set; } = OpenAICompatibleAuth.ApiKey;
    /// <summary>Name of an environment variable; the provider is enabled when it is "true". Lets a built-in entry stay off until chosen.</summary>
    public string EnabledEnv { get; set; } = "";
    /// <summary>Vertex AI location used to build the endpoint of a "google-adc" provider whose Endpoint is blank.</summary>
    public string Location { get; set; } = "global";
    public string LocationEnv { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int MaxHistoryMessages { get; set; } = 20;
    public List<ChatModelOption> Models { get; set; } = [];
    public List<PromptPresetConfig> Presets { get; set; } = [];

    public OpenAICompatibleProviderSettings Clone() => (OpenAICompatibleProviderSettings)MemberwiseClone();
}

public static class OpenAICompatibleAuth
{
    public const string ApiKey = "api-key";
    public const string GoogleAdc = "google-adc";
}
