using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.UserSettings;

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
        IUserContext users,
        IUserSettingsStore settingsStore)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        var prompt = await settingsStore.GetDefaultAssistantPromptAsync(currentUser);

        return Results.Ok(ToResponse(prompt));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        UserSettingsUpdateRequest request,
        HttpContext context,
        IUserContext users,
        IUserSettingsStore settingsStore)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        if (request.DefaultAssistantPrompt?.Length > MaxDefaultAssistantPromptLength)
        {
            return Results.BadRequest(new { error = $"默认助手提示词不能超过 {MaxDefaultAssistantPromptLength} 个字符" });
        }

        var prompt = await settingsStore.UpdateDefaultAssistantPromptAsync(
            currentUser,
            request.DefaultAssistantPrompt);

        return Results.Ok(ToResponse(prompt));
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
