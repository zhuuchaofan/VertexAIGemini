namespace VertexAI.Services.Chat;

/// <summary>Token counts as the provider reported them. A field the provider did not report stays null (unknown, not zero).</summary>
public sealed record ChatUsage(int? InputTokens, int? OutputTokens, int? ThoughtsTokens, int? TotalTokens);

/// <summary>What the client actually applied for a request, after names and options were resolved.</summary>
public sealed record ChatEffectiveOptions(
    string ModelName,
    string PresetId,
    bool? ThinkingEnabled,
    string? ThinkingLevel,
    int? ThinkingBudget,
    double? Temperature,
    double? TopP,
    int? MaxOutputTokens,
    string? SafetyThreshold);

/// <summary>What a client learned about a request from the provider. Filled in while the reply streams.</summary>
public sealed class ChatRunTelemetry
{
    public ChatUsage? Usage { get; set; }
    public string? FinishReason { get; set; }
    public string? ModelVersion { get; set; }
    public string? ResponseId { get; set; }

    /// <summary>Requests this client made to the provider, by kind. Our own counts, not the provider's.</summary>
    public int GenerateCalls { get; set; }
    public int SummarizeCalls { get; set; }
    public int CountTokensCalls { get; set; }
}

/// <summary>Implemented by model clients that can report what they applied and what the provider told them.</summary>
public interface IChatRunTelemetrySource
{
    ChatRunTelemetry Telemetry { get; }
    ChatEffectiveOptions Effective { get; }
}
