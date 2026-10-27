namespace VertexAI.Services.Auth;

public sealed record AuthenticatedUser(
    Guid LocalUserId,
    string? FirebaseUid,
    string? Email,
    bool IsAdmin = false,
    bool EmailVerified = false);

public interface IUserContext
{
    Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context);
}
