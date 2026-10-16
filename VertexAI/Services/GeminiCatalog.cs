using VertexAI.Services.Chat;

namespace VertexAI.Services;

internal static class GeminiCatalog
{
    public static IReadOnlyList<PromptPresetConfig> CreatePresets(GeminiSettings config)
    {
        var presets = SystemPromptPresets.All.Select(p => new PromptPresetConfig
        {
            Id = p.Id,
            Name = p.Name,
            Prompt = p.Prompt,
            Description = p.Description ?? ""
        }).ToList();

        if (config.Presets == null || config.Presets.Count == 0)
        {
            return presets;
        }

        foreach (var configuredPreset in config.Presets)
        {
            var existing = presets.FirstOrDefault(p => p.Id == configuredPreset.Id);
            if (existing != null)
            {
                existing.Name = configuredPreset.Name;
                existing.Prompt = configuredPreset.Prompt;
                if (!string.IsNullOrWhiteSpace(configuredPreset.Description))
                {
                    existing.Description = configuredPreset.Description;
                }
            }
            else
            {
                var customIndex = presets.FindIndex(p => p.Id == "custom");
                if (customIndex >= 0)
                {
                    presets.Insert(customIndex, configuredPreset);
                }
                else
                {
                    presets.Add(configuredPreset);
                }
            }
        }

        return presets;
    }

    public static IReadOnlyList<ChatModelOption> CreateModelOptions(GeminiSettings config)
    {
        if (config.Models is { Count: > 0 })
        {
            return NormalizeThinking(config.Models);
        }

        return NormalizeThinking(
        [
            new()
            {
                Name = "Gemini 3.8 Flash",
                ModelName = "gemini-3.8-flash",
                Description = "快速多模态模型，支持低、中、高思考级别",
                Thinking = CreateGeminiThinking(["low", "medium", "high"], "medium", canDisable: false),
                SupportsSampling = false,
                MaxTokens = 1048576
            },
            new()
            {
                Name = "Gemini 4 Argon",
                ModelName = "gemini-4-argon",
                Description = "Gemini 4 Argon，支持低、中、高思考级别及统一上下文",
                Thinking = CreateGeminiThinking(["low", "medium", "high"], "medium", canDisable: false),
                SupportsSampling = false,
                MaxTokens = 1048576
            }
        ]);
    }

    public static string ResolveModelName(string? modelName, IReadOnlyList<ChatModelOption> models) =>
        models.Any(m => m.ModelName == modelName)
            ? modelName!
            : models[0].ModelName;

    public static string ResolvePresetId(IReadOnlyList<PromptPresetConfig> presets) =>
        presets.FirstOrDefault(p => p.Id == "default")?.Id
            ?? presets.FirstOrDefault()?.Id
            ?? "default";

    private static IReadOnlyList<ChatModelOption> NormalizeThinking(IReadOnlyList<ChatModelOption> models)
    {
        foreach (var model in models)
        {
            model.SupportsThinking = model.Thinking != null || model.SupportsThinking;
            if (model.SupportsThinking && model.Thinking == null)
            {
                model.Thinking = CreateGeminiThinking(["minimal", "low", "medium", "high"], "medium");
            }
        }

        return models;
    }

    private static ChatThinkingConfig CreateGeminiThinking(
        string[] values,
        string defaultValue,
        bool canDisable = true) => new()
    {
        Kind = "gemini-level",
        CanDisable = canDisable,
        Default = defaultValue,
        Options = values.Select(value => new ChatThinkingOption
        {
            Value = value,
            Label = value switch
            {
                "off" => "关闭",
                "minimal" => "极低",
                "low" => "低",
                "medium" => "中",
                "high" => "高",
                _ => value
            }
        }).ToList()
    };
}
