using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

public sealed record ThinkingChoice(string? Level = null, bool? Enabled = null, int? Budget = null);

/// <summary>
/// The settings a person chose, as stored in the "my defaults" layer and in a conversation's settings. Both layers
/// share this shape; a run keeps its own immutable copy in <see cref="RunRequested"/> and <see cref="RunEffective"/>.
/// </summary>
public sealed record ChatSettingsDocument(
    string ProviderId,
    string ModelName,
    string PresetId,
    string? CustomPrompt,
    string SearchMode,
    ThinkingChoice? Thinking,
    ChatAdvancedOptions? Advanced,
    DateTime? UpdatedAt = null)
{
    public const int MaxCustomPromptChars = 20_000;

    public static ChatSettingsDocument FromRequested(RunRequested requested, DateTime now)
    {
        var thinking = requested.ThinkingEnabled == null && requested.ThinkingLevel == null && requested.ThinkingBudget == null
            ? null
            : new ThinkingChoice(requested.ThinkingLevel, requested.ThinkingEnabled, requested.ThinkingBudget);
        return new ChatSettingsDocument(
            requested.ProviderId, requested.ModelName, requested.PresetId, requested.CustomPrompt,
            requested.SearchMode, thinking, requested.Advanced, now);
    }
}

/// <summary>Reads and writes the two editable settings layers. The third layer, the run snapshot, lives on the run.</summary>
public interface ISettingsStore
{
    Task<ChatSettingsDocument?> GetDefaultsAsync(AuthenticatedUser user, CancellationToken cancellationToken);
    Task SetDefaultsAsync(AuthenticatedUser user, ChatSettingsDocument settings, CancellationToken cancellationToken);

    /// <summary>Returns false when the conversation does not exist or belongs to someone else.</summary>
    Task<bool> SetConversationSettingsAsync(
        AuthenticatedUser user, Guid conversationId, ChatSettingsDocument settings, CancellationToken cancellationToken);
}

/// <summary>
/// Settings are only stored when they are coherent with the model's catalogue entry: a level the model does not
/// offer, sampling on a model that ignores it, or a filter this account may not use is refused, never saved.
/// A saved value is never an authorization: the safety threshold is checked again at every run.
/// </summary>
public static class SettingsValidator
{
    public static (ChatSettingsDocument? Settings, string? Error) Validate(
        ChatSettingsDocument? input,
        IChatProviderCatalog catalog,
        GeminiSafetyPolicy safety,
        AuthenticatedUser user,
        bool includeCustomPrompt)
    {
        if (input == null) return (null, "settings are required");

        var provider = catalog.Providers.FirstOrDefault(p =>
            string.Equals(p.Info.Id, input.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider == null) return (null, "unknown provider");

        var model = provider.ModelOptions.FirstOrDefault(m => m.ModelName == input.ModelName);
        if (model == null) return (null, "unknown model");

        if (!provider.Presets.Any(p => p.Id == input.PresetId)) return (null, "unknown prompt preset");

        var searchMode = input.SearchMode?.Trim().ToLowerInvariant();
        if (searchMode is not (SearchModes.Off or SearchModes.Auto or SearchModes.Force)) return (null, "unknown search mode");

        string? customPrompt = null;
        if (input.PresetId == "custom" && includeCustomPrompt)
        {
            customPrompt = input.CustomPrompt?.Trim();
            if (customPrompt is { Length: > ChatSettingsDocument.MaxCustomPromptChars }) return (null, "the custom prompt is too long");
        }

        var thinking = input.Thinking;
        if (thinking != null && (thinking.Level != null || thinking.Enabled != null || thinking.Budget != null))
        {
            var config = model.Thinking;
            if (config == null) return (null, "this model has no thinking options");
            if (thinking.Level != null && config.Options.Count > 0 && !config.Options.Any(o => o.Value == thinking.Level))
                return (null, "the thinking level is not offered by this model");
            if (thinking.Budget is < 1) return (null, "the thinking budget must be positive");
            if (thinking.Enabled == false && !config.CanDisable && !config.Options.Any(o => o.Value == "off"))
                return (null, "this model cannot turn thinking off");
        }
        else
        {
            thinking = null;
        }

        var advanced = input.Advanced;
        if (advanced != null)
        {
            if (advanced.Validate() is { } error) return (null, error);
            if (!model.SupportsSampling && (advanced.Temperature != null || advanced.TopP != null))
                return (null, "this model ignores temperature and top-p");
            if (advanced.MaxOutputTokens > model.MaxTokens) return (null, "the output limit is above what this model allows");
            if (!string.IsNullOrWhiteSpace(advanced.SafetyThreshold)
                && !safety.GetAllowedThresholds(user).Contains(advanced.SafetyThreshold, StringComparer.OrdinalIgnoreCase))
                return (null, "this account may not use that content filter level");
            if (advanced.Temperature == null && advanced.TopP == null && advanced.MaxOutputTokens == null
                && string.IsNullOrWhiteSpace(advanced.SafetyThreshold))
                advanced = null;
        }

        return (new ChatSettingsDocument(
            provider.Info.Id, model.ModelName, input.PresetId, customPrompt, searchMode, thinking, advanced), null);
    }
}
