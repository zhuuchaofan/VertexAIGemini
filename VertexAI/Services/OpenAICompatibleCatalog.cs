using VertexAI.Services.Chat;

namespace VertexAI.Services;

internal static class OpenAICompatibleCatalog
{
    public static IReadOnlyList<OpenAICompatibleProviderSettings> CreateEnabledProviderSettings(
        OpenAICompatibleSettings settings)
    {
        var providers = settings.Providers.ToList();
        var singleProvider = CreateProviderSettings(settings);
        if (IsSingleProviderConfigured(singleProvider))
        {
            providers.Add(singleProvider);
        }

        return providers
            .Select(ApplyEnvironmentApiKey)
            .Where(IsConfigured)
            .ToList();
    }

    public static IReadOnlyList<ChatModelOption> CreateModelOptions(OpenAICompatibleProviderSettings settings)
    {
        if (settings.Models.Count > 0)
        {
            return NormalizeThinking(settings.ProviderId, settings.Models);
        }

        return NormalizeThinking(settings.ProviderId,
        [
            new()
            {
                Name = settings.ModelName,
                ModelName = settings.ModelName,
                Description = "Configured OpenAI-compatible chat model",
                SupportsThinking = false,
                MaxTokens = 128000
            }
        ]);
    }

    public static IReadOnlyList<PromptPresetConfig> CreatePresets(OpenAICompatibleProviderSettings settings)
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

    private static OpenAICompatibleProviderSettings CreateProviderSettings(OpenAICompatibleSettings settings) => new()
    {
        Enabled = settings.Enabled,
        ProviderId = settings.ProviderId,
        Name = settings.Name,
        Description = settings.Description,
        Endpoint = settings.Endpoint,
        ApiKey = settings.ApiKey,
        ModelName = settings.ModelName,
        MaxHistoryMessages = settings.MaxHistoryMessages,
        Models = settings.Models,
        Presets = settings.Presets
    };

    private static OpenAICompatibleProviderSettings ApplyEnvironmentApiKey(OpenAICompatibleProviderSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiKeyEnv))
        {
            return settings;
        }

        var apiKey = Environment.GetEnvironmentVariable(settings.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return settings;
        }

        return new()
        {
            Enabled = settings.Enabled,
            ProviderId = settings.ProviderId,
            Name = settings.Name,
            Description = settings.Description,
            Endpoint = settings.Endpoint,
            ApiKey = apiKey,
            ApiKeyEnv = settings.ApiKeyEnv,
            ModelName = settings.ModelName,
            MaxHistoryMessages = settings.MaxHistoryMessages,
            Models = settings.Models,
            Presets = settings.Presets
        };
    }

    private static IReadOnlyList<ChatModelOption> NormalizeThinking(string providerId, IReadOnlyList<ChatModelOption> models)
    {
        foreach (var model in models)
        {
            model.SupportsThinking = model.Thinking != null || model.SupportsThinking;
            if (model.SupportsThinking && model.Thinking == null)
            {
                model.Thinking = CreateDefaultThinking(providerId, model.ModelName);
            }
        }

        return models;
    }

    private static ChatThinkingConfig CreateDefaultThinking(string providerId, string modelName)
    {
        if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            return new()
            {
                Kind = "deepseek-effort",
                Default = "high",
                Options =
                [
                    new() { Value = "off", Label = "关闭" },
                    new() { Value = "high", Label = "高" },
                    new() { Value = "max", Label = "最大" }
                ]
            };
        }

        if (providerId.Equals("qwen", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return new()
            {
                Kind = "qwen-budget",
                Default = "on",
                DefaultBudget = 500,
                Budgets = [50, 500, 2000]
            };
        }

        if (providerId.Equals("kimi", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("kimi-k2.7-code", StringComparison.OrdinalIgnoreCase))
        {
            return new()
            {
                Kind = "kimi-fixed",
                FixedEnabled = true,
                CanDisable = false,
                Default = "on"
            };
        }

        if (providerId.Equals("zhipu", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("glm-", StringComparison.OrdinalIgnoreCase))
        {
            return new()
            {
                Kind = "zhipu-toggle",
                Default = "on"
            };
        }

        return new()
        {
            Kind = "openai-effort",
            Default = "medium",
            Options =
            [
                new() { Value = "off", Label = "关闭" },
                new() { Value = "low", Label = "低" },
                new() { Value = "medium", Label = "中" },
                new() { Value = "high", Label = "高" }
            ]
        };
    }

    private static bool IsConfigured(OpenAICompatibleProviderSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ProviderId)
        && !string.IsNullOrWhiteSpace(settings.Endpoint)
        && !string.IsNullOrWhiteSpace(settings.ModelName)
        && (settings.Enabled || !string.IsNullOrWhiteSpace(settings.ApiKey));

    private static bool IsSingleProviderConfigured(OpenAICompatibleProviderSettings settings) =>
        settings.Enabled || !string.IsNullOrWhiteSpace(settings.ApiKey);
}
