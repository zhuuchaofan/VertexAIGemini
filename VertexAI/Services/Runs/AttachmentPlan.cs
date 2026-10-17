using System.Text.Json;
using VertexAI.Services.Attachments;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>
/// One attachment of a send, decided once at admission: what is recorded with the message (metadata, or the content
/// itself when no bucket is configured) and, if a bucket is used, the exact object name and the bytes to write there.
/// </summary>
public sealed record PlannedAttachment(ChatAttachment Recorded, string? ObjectName, byte[]? Bytes);

public static class AttachmentPlanner
{
    /// <summary>
    /// With a bucket the message records only metadata in state "pending" and the winner of admission uploads later
    /// under a name derived from the content hash. Without one the content is stored inline and is "stored" at once.
    /// </summary>
    public static IReadOnlyList<PlannedAttachment> Plan(
        string runId, Guid conversationId, IReadOnlyCollection<ChatAttachment>? incoming, bool storageEnabled)
    {
        if (incoming is not { Count: > 0 }) return [];

        var messageId = RunIds.UserMessageId(runId);
        var planned = new List<PlannedAttachment>(incoming.Count);
        var index = 0;
        foreach (var attachment in incoming)
        {
            var bytes = Convert.FromBase64String(attachment.Base64Data!);
            var hash = AttachmentNames.Sha256Hex(bytes);
            var mime = attachment.MimeType.Trim().ToLowerInvariant();
            var name = AttachmentNames.CleanFileName(attachment.FileName);

            if (storageEnabled)
            {
                var objectName = AttachmentNames.ObjectName(conversationId, messageId, index, name, mime, hash);
                planned.Add(new PlannedAttachment(
                    new ChatAttachment(null, mime, name, objectName, bytes.Length, hash, AttachmentStates.Pending),
                    objectName,
                    bytes));
            }
            else
            {
                planned.Add(new PlannedAttachment(
                    new ChatAttachment(attachment.Base64Data, mime, name, null, bytes.Length, hash, AttachmentStates.Stored),
                    null,
                    null));
            }

            index++;
        }

        return planned;
    }

    /// <summary>What the model is given for this send: the content, whether it was stored or kept inline.</summary>
    public static IReadOnlyList<ChatAttachment> ForModel(IReadOnlyList<PlannedAttachment> planned, IReadOnlyCollection<ChatAttachment>? incoming) =>
        incoming?.Select(a => a with { MimeType = a.MimeType.Trim().ToLowerInvariant(), FileName = AttachmentNames.CleanFileName(a.FileName) }).ToList()
        ?? [];

    /// <summary>The same-send check: contents, not just names, decide whether a repeated run id is the same request.</summary>
    public static string FingerprintPart(IReadOnlyList<PlannedAttachment> planned) =>
        planned.Count == 0
            ? ""
            : JsonSerializer.Serialize(planned.Select(p => new { p.Recorded.FileName, p.Recorded.MimeType, p.Recorded.SizeBytes, p.Recorded.Sha256 }));
}
