namespace VertexAI.Services.Chat;

/// <summary>
/// An attachment as sent by the browser (with <c>Base64Data</c>) or as stored (metadata only when a bucket is configured).
/// <c>Sha256</c> and <c>State</c> are only present on attachments written by the run API: <c>State</c> is "pending"
/// (admitted, not yet in storage), "stored" or "failed"; absent means stored, which is what every older message is.
/// </summary>
public sealed record ChatAttachment(
    string? Base64Data,
    string MimeType,
    string? FileName,
    string? StorageObjectName = null,
    long? SizeBytes = null,
    string? Sha256 = null,
    string? State = null);

public static class AttachmentStates
{
    public const string Pending = "pending";
    public const string Stored = "stored";
    public const string Failed = "failed";

    /// <summary>Only a stored attachment may be shown or sent to a model. Older messages have no state and are stored.</summary>
    public static bool IsUsable(string? state) => state is null or Stored;
}
