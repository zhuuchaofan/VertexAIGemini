using VertexAI.Services.Auth;

namespace VertexAI.Api;

internal static class ApiUserContext
{
    public static async Task<Guid?> GetCurrentUserIdAsync(
        HttpContext context,
        IUserContext users) =>
        await users.GetCurrentUserIdAsync(context);
}
