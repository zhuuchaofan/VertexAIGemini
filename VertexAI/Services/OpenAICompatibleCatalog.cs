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
            .Select(ApplyEnvironment)
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


    /// <summary>Values that come from the environment: the API key, the on/off switch and the Vertex location.</summary>
    private static OpenAICompatibleProviderSettings ApplyEnvironment(OpenAICompatibleProviderSettings settings)
    {
        var result = settings.Clone();

        if (string.IsNullOrWhiteSpace(result.ApiKey) && !string.IsNullOrWhiteSpace(result.ApiKeyEnv))
        {
            var apiKey = Environment.GetEnvironmentVariable(result.ApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(apiKey)) result.ApiKey = apiKey;
        }

        if (!string.IsNullOrWhiteSpace(result.EnabledEnv)
            && string.Equals(Environment.GetEnvironmentVariable(result.EnabledEnv), "true", StringComparison.OrdinalIgnoreCase))
        {
            result.Enabled = true;
        }

        if (!string.IsNullOrWhiteSpace(result.LocationEnv))
        {
            var location = Environment.GetEnvironmentVariable(result.LocationEnv);
            if (!string.IsNullOrWhiteSpace(location)) result.Location = location.Trim();
        }

        if (result.AuthMode == OpenAICompatibleAuth.GoogleAdc)
        {
            result.Endpoint = ResolveVertexEndpoint(result.Endpoint, result.Location, CurrentProjectId());
        }

        return result;
    }

    private static string? CurrentProjectId() =>
        new[] { "PROJECT_ID", "VertexAI__ProjectId", "GOOGLE_CLOUD_PROJECT" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// The Vertex AI OpenAI-compatible chat endpoint. A blank endpoint is built from the project and location;
    /// {project} and {location} in a given endpoint are filled in. Empty when the project is not known.
    /// </summary>
    public static string ResolveVertexEndpoint(string? endpoint, string? location, string? projectId)
    {
        var place = string.IsNullOrWhiteSpace(location) ? "global" : location.Trim();
        if (string.IsNullOrWhiteSpace(projectId)) return string.IsNullOrWhiteSpace(endpoint) ? "" : endpoint.Replace("{location}", place);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint.Replace("{project}", projectId).Replace("{location}", place);
        }

        var host = place == "global" ? "aiplatform.googleapis.com" : $"{place}-aiplatform.googleapis.com";
        return $"https://{host}/v1/projects/{projectId}/locations/{place}/endpoints/openapi/chat/completions";
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
