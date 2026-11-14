namespace VertexAI.Services.Chat;

public sealed record ChatSessionOptions(
    string? ProviderId = null,
    string? ModelName = null,
    string? PresetId = null,
    string? CustomPrompt = null,
    bool? ThinkingEnabled = null,
    string? ThinkingLevel = null,
    int? ThinkingBudget = null,
    ChatAdvancedOptions? Advanced = null,
    // Added after the preset or custom system prompt for this request only (the person's saved memory).
    // Never stored with the run or the conversation, and not part of the run fingerprint.
    string? SystemAddendum = null);
