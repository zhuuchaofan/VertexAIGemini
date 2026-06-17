namespace VertexAI.Services.Auth;

public interface IUserContext
{
    Task<Guid?> GetCurrentUserIdAsync(HttpContext context);
}
