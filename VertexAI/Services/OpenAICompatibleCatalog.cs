using VertexAI.Services.Chat;

namespace VertexAI.Services;

internal static class OpenAICompatibleCatalog
{
    public static IReadOnlyList<ChatModelOption> CreateModelOptions(OpenAICompatibleSettings settings)
    {
        if (settings.Models.Count > 0)
        {
            return settings.Models;
        }

        return
        [
            new()
            {
                Name = settings.ModelName,
                ModelName = settings.ModelName,
                Description = "Configured OpenAI-compatible chat model",
                SupportsThinking = false,
                MaxTokens = 128000
            }
        ];
    }

    public static IReadOnlyList<PromptPresetConfig> CreatePresets(OpenAICompatibleSettings settings)
    {
        if (settings.Presets.Count > 0)
        {
            return settings.Presets;
        }

        return GeminiCatalog.CreatePresets(new GeminiSettings());
    }

    public static string ResolveModelName(string? modelName, IReadOnlyList<ChatModelOption> models) =>
        models.Any(m => m.ModelName == modelName)
            ? modelName!
            : models[0].ModelName;
}
