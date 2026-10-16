namespace VertexAI.Services.Chat;

/// <summary>Terminal state of one chat generation, sent in the SSE "final" event and stored on the message.</summary>
public static class ChatStatuses
{
    /// <summary>The model finished normally.</summary>
    public const string Completed = "completed";

    /// <summary>The model hit its output limit; the content is usable but incomplete.</summary>
    public const string Truncated = "truncated";

    /// <summary>The user stopped generation or the client disconnected; the content is partial.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Generation failed (upstream error, timeout, policy block); any content is partial.</summary>
    public const string Failed = "failed";

    /// <summary>A result the user can read as an answer (possibly incomplete) rather than an error.</summary>
    public static bool IsSuccessful(string status) => status is Completed or Truncated;
}
