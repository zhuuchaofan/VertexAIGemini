using VertexAI.Services.Auth;

namespace VertexAI.Services.UserSettings;

public interface IUserSettingsStore
{
    Task<string?> GetDefaultAssistantPromptAsync(AuthenticatedUser user);

    Task<string?> UpdateDefaultAssistantPromptAsync(
        AuthenticatedUser user,
        string? defaultAssistantPrompt);
}
