namespace VertexAI.Services.Auth;

public sealed record AuthenticatedUser(
    Guid LocalUserId,
    string? FirebaseUid,
    string? Email);

public interface IUserContext
{
    Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context);
}
