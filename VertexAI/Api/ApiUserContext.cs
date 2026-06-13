using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Services.Auth;

namespace VertexAI.Api;

internal static class ApiUserContext
{
    public static async Task<Guid?> GetCurrentUserIdAsync(
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies)
    {
        var token = cookies.ReadSessionToken(context);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

        return session?.UserId;
    }
}
