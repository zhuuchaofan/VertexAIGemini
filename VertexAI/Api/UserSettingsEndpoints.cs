using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Services;
using VertexAI.Services.Auth;

namespace VertexAI.Api;

public static class UserSettingsEndpoints
{
    private const int MaxDefaultAssistantPromptLength = 12000;

    public static void MapUserSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/user/settings");

        group.MapGet("/", GetSettingsAsync);
        group.MapPatch("/", UpdateSettingsAsync);
    }

    private static async Task<IResult> GetSettingsAsync(
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null) return Results.Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync();
        var prompt = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value)
            .Select(user => user.DefaultAssistantPrompt)
            .FirstOrDefaultAsync();

        return Results.Ok(ToResponse(prompt));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        UserSettingsUpdateRequest request,
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null) return Results.Unauthorized();

        if (request.DefaultAssistantPrompt?.Length > MaxDefaultAssistantPromptLength)
        {
            return Results.BadRequest(new { error = $"默认助手提示词不能超过 {MaxDefaultAssistantPromptLength} 个字符" });
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == userId.Value);
        if (user == null) return Results.Unauthorized();

        user.DefaultAssistantPrompt = string.IsNullOrWhiteSpace(request.DefaultAssistantPrompt)
            ? null
            : request.DefaultAssistantPrompt;

        await db.SaveChangesAsync();

        return Results.Ok(ToResponse(user.DefaultAssistantPrompt));
    }

    private static UserSettingsResponse ToResponse(string? prompt) =>
        new(
            prompt,
            SystemPromptPresets.GetById("default").Prompt);

    private sealed record UserSettingsUpdateRequest(string? DefaultAssistantPrompt);

    private sealed record UserSettingsResponse(
        string? DefaultAssistantPrompt,
        string SystemDefaultAssistantPrompt);
}
