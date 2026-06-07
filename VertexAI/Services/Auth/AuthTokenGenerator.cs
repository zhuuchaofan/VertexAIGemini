using System.Security.Cryptography;

namespace VertexAI.Services.Auth;

public interface IAuthTokenGenerator
{
    string Generate();
}

public sealed class AuthTokenGenerator : IAuthTokenGenerator
{
    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
