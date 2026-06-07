using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services.Auth;

public interface IAuthSessionStore
{
    Task<Session> CreateSessionAsync(AppDbContext db, Guid userId);
    Task DeleteSessionAsync(string token);
    Task<User?> GetUserBySessionTokenAsync(string token);
    Task ClearExpiredSessionsAsync(AppDbContext db, Guid userId);
    Task ClearUserSessionsAsync(AppDbContext db, Guid userId);
}

public sealed class AuthSessionStore : IAuthSessionStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAuthTokenGenerator _tokens;

    public AuthSessionStore(
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthTokenGenerator tokens)
    {
        _dbFactory = dbFactory;
        _tokens = tokens;
    }

    public async Task<Session> CreateSessionAsync(AppDbContext db, Guid userId)
    {
        var session = new Session
        {
            UserId = userId,
            Token = _tokens.Generate(),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return session;
    }

    public async Task DeleteSessionAsync(string token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session == null)
        {
            return;
        }

        db.Sessions.Remove(session);
        await db.SaveChangesAsync();
    }

    public async Task<User?> GetUserBySessionTokenAsync(string token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

        return session?.User;
    }

    public async Task ClearExpiredSessionsAsync(AppDbContext db, Guid userId)
    {
        var expiredSessions = await db.Sessions
            .Where(s => s.UserId == userId && s.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync();

        if (expiredSessions.Count == 0)
        {
            return;
        }

        db.Sessions.RemoveRange(expiredSessions);
    }

    public async Task ClearUserSessionsAsync(AppDbContext db, Guid userId)
    {
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId)
            .ToListAsync();

        db.Sessions.RemoveRange(sessions);
    }
}
