using System.Security.Cryptography;
using System.Text;

namespace VertexAI.Services.Auth;

/// <summary>
/// Development only: trusts an <c>X-Dev-User</c> header so the app can run locally without Firebase. It is only
/// ever registered by <see cref="VertexAI.Configuration.DevelopmentExtensions"/>, which refuses to do so outside
/// the Development environment.
/// </summary>
public sealed class DevUserContext : IUserContext
{
    public const string HeaderName = "X-Dev-User";

    public Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context)
    {
        var name = context.Request.Headers[HeaderName].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64)
        {
            return Task.FromResult<AuthenticatedUser?>(null);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dev-user:" + name));
        var id = new Guid(hash.AsSpan(0, 16));
        return Task.FromResult<AuthenticatedUser?>(new AuthenticatedUser(id, "dev-" + name, name + "@dev.local"));
    }
}
