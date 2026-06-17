using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Services.Auth;

namespace VertexAI.Services.UserSettings;

public sealed class PostgresUserSettingsStore : IUserSettingsStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PostgresUserSettingsStore(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string?> GetDefaultAssistantPromptAsync(AuthenticatedUser user)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users
            .AsNoTracking()
            .Where(item => item.Id == user.LocalUserId)
            .Select(item => item.DefaultAssistantPrompt)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> UpdateDefaultAssistantPromptAsync(
        AuthenticatedUser user,
        string? defaultAssistantPrompt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Users.FirstOrDefaultAsync(item => item.Id == user.LocalUserId);
        if (entity == null)
        {
            return null;
        }

        entity.DefaultAssistantPrompt = string.IsNullOrWhiteSpace(defaultAssistantPrompt)
            ? null
            : defaultAssistantPrompt;

        await db.SaveChangesAsync();
        return entity.DefaultAssistantPrompt;
    }
}
