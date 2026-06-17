using Microsoft.EntityFrameworkCore;
using VertexAI.Data;

namespace VertexAI.Services.Auth;

public sealed class SessionUserContext : IUserContext
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAuthCookieService _cookies;

    public SessionUserContext(
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies)
    {
        _dbFactory = dbFactory;
        _cookies = cookies;
    }

    public async Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context)
    {
        var token = _cookies.ReadSessionToken(context);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .AsNoTracking()
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

        return session?.User == null
            ? null
            : new AuthenticatedUser(session.UserId, session.User.FirebaseUid, session.User.Email);
    }
}
