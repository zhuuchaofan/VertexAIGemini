using VertexAI.Services.Auth;

namespace VertexAI.Api;

internal static class ApiUserContext
{
    public static async Task<AuthenticatedUser?> GetCurrentUserAsync(
        HttpContext context,
        IUserContext users) =>
        await users.GetCurrentUserAsync(context);
}
