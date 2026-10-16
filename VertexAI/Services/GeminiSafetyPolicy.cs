using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using VertexAI.Services.Auth;

namespace VertexAI.Services;

public sealed class GeminiSafetyPolicy
{
    private static readonly HarmCategory[] Categories =
    [
        HarmCategory.HARM_CATEGORY_HARASSMENT,
        HarmCategory.HARM_CATEGORY_HATE_SPEECH,
        HarmCategory.HARM_CATEGORY_DANGEROUS_CONTENT,
        HarmCategory.HARM_CATEGORY_SEXUALLY_EXPLICIT,
        HarmCategory.HARM_CATEGORY_IMAGE_HARASSMENT,
        HarmCategory.HARM_CATEGORY_IMAGE_HATE,
        HarmCategory.HARM_CATEGORY_IMAGE_DANGEROUS_CONTENT,
        HarmCategory.HARM_CATEGORY_IMAGE_SEXUALLY_EXPLICIT,
        HarmCategory.HARM_CATEGORY_JAILBREAK,
        HarmCategory.HARM_CATEGORY_CIVIC_INTEGRITY
    ];

    private readonly GeminiSafetySettings _settings;

    public GeminiSafetyPolicy(IOptions<GeminiSettings> settings)
    {
        _settings = settings.Value.Safety;
    }

    public IReadOnlyList<SafetySetting> CreateSafetySettings(AuthenticatedUser? user, string? requested = null)
    {
        var threshold = ResolveThreshold(user);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!GetAllowedThresholds(user).Contains(requested, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("The requested safety threshold is not permitted.");
            threshold = Enum.Parse<HarmBlockThreshold>(requested, ignoreCase: true);
        }
        return Categories
            .Select(category => new SafetySetting
            {
                Category = category,
                Threshold = threshold
            })
            .ToArray();
    }

    public IReadOnlyList<string> GetAllowedThresholds(AuthenticatedUser? user)
    {
        string[] thresholds = ["OFF", "BLOCK_NONE", "BLOCK_ONLY_HIGH",
            "BLOCK_MEDIUM_AND_ABOVE", "BLOCK_LOW_AND_ABOVE"];
        if (user?.IsAdmin == true && _settings.AdminCanDisable)
            return thresholds;
        var baseline = Array.IndexOf(thresholds, ResolveThreshold(user).ToString());
        // Unknown server configuration must never allow a weaker threshold.
        return thresholds.Skip(baseline < 0 ? thresholds.Length - 1 : baseline).ToArray();
    }

    private HarmBlockThreshold ResolveThreshold(AuthenticatedUser? user)
    {
        var configured = user?.IsAdmin == true && _settings.AdminCanDisable
            ? _settings.AdminThreshold
            : _settings.DefaultThreshold;

        return ParseThreshold(configured, HarmBlockThreshold.BLOCK_MEDIUM_AND_ABOVE);
    }

    private static HarmBlockThreshold ParseThreshold(string? value, HarmBlockThreshold fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return Enum.TryParse<HarmBlockThreshold>(value, ignoreCase: true, out var threshold)
            ? threshold
            : fallback;
    }
}
