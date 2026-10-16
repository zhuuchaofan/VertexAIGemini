namespace VertexAI.Services.Chat;

/// <summary>Optional per-request Gemini generation and filtering controls.</summary>
public sealed record ChatAdvancedOptions(
    string? SafetyThreshold = null,
    double? Temperature = null,
    double? TopP = null,
    int? MaxOutputTokens = null)
{
    public string? Validate()
    {
        if (Temperature is { } temperature && (!double.IsFinite(temperature) || temperature is < 0 or > 2))
            return "Temperature must be between 0 and 2.";
        if (TopP is { } topP && (!double.IsFinite(topP) || topP is <= 0 or > 1))
            return "TopP must be greater than 0 and at most 1.";
        if (MaxOutputTokens is < 1 or > 65536)
            return "MaxOutputTokens must be between 1 and 65536.";
        return null;
    }
}
