using VertexAI.Services.Auth;

namespace VertexAI.Services.Quota;

public sealed record ChatQuotaRequest(
    AuthenticatedUser User,
    int RequestCount,
    int EstimatedTokens,
    int SearchCount,
    long AttachmentBytes);
