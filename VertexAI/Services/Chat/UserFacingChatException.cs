namespace VertexAI.Services.Chat;

/// <summary>
/// An error whose message is safe and meant to be shown to the end user as-is
/// (for example "the selected model is no longer available").
/// </summary>
public sealed class UserFacingChatException : Exception
{
    public UserFacingChatException(string message) : base(message)
    {
    }
}
