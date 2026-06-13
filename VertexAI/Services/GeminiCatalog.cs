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
                Name = "Gemini 3.5 Flash",
                ModelName = "gemini-3.5-flash",
                Description = "旗舰速度模型，提供极佳的响应速度与多模态能力",
                Thinking = CreateGeminiThinking(["minimal", "low", "medium", "high"], "medium"),
                MaxTokens = 1048576
            },
            new()
            {
                Name = "Gemini 3.1 Flash Lite",
                ModelName = "gemini-3.1-flash-lite",
                Description = "超低延迟、极度轻量级，适合日常高频交互",
                Thinking = CreateGeminiThinking(["minimal", "low", "medium", "high"], "minimal"),
                MaxTokens = 1048576
            },
            new()
            {
                Name = "Gemini 3 Flash (Preview)",
                ModelName = "gemini-3-flash-preview",
                Description = "第三代快速原型预览，均衡的多模态多任务模型",
                Thinking = CreateGeminiThinking(["minimal", "low", "medium", "high"], "high"),
                MaxTokens = 1048576
            },
            new()
            {
                Name = "Gemini 3.1 Pro (Preview)",
                ModelName = "gemini-3.1-pro-preview",
                Description = "深度推理版预览，适合复杂的代码逻辑和长文本深度思考",
                Thinking = CreateGeminiThinking(["low", "medium", "high"], "high", canDisable: false),
                MaxTokens = 2097152
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
