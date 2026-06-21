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

    public IReadOnlyList<SafetySetting> CreateSafetySettings(AuthenticatedUser? user)
    {
        var threshold = ResolveThreshold(user);
        return Categories
            .Select(category => new SafetySetting
            {
                Category = category,
                Threshold = threshold
            })
            .ToArray();
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
