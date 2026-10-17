using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VertexAI.Services.Runs;

/// <summary>Hash of everything that defines a send. The same run id with a different fingerprint is a conflict.</summary>
public static class RunFingerprint
{
    public static string Compute(Guid conversationId, string message, RunRequested requested)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            conversationId = conversationId.ToString("D"),
            message,
            requested.ProviderId,
            requested.ModelName,
            requested.PresetId,
            requested.CustomPrompt,
            requested.SearchMode,
            requested.ThinkingEnabled,
            requested.ThinkingLevel,
            requested.ThinkingBudget,
            advanced = requested.Advanced
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
