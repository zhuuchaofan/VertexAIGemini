namespace VertexAI.Services.Chat;

public sealed record ChatSessionOptions(
    string? ProviderId = null,
    string? ModelName = null,
    string? PresetId = null,
    string? CustomPrompt = null,
    bool? ThinkingEnabled = null,
    string? ThinkingLevel = null,
    int? ThinkingBudget = null);
