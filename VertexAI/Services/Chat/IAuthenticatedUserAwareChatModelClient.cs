using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public interface IAuthenticatedUserAwareChatModelClient
{
    void SetAuthenticatedUser(AuthenticatedUser user);
}
