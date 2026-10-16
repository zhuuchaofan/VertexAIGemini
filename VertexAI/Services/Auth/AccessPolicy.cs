using Microsoft.Extensions.Options;
using VertexAI.Configuration;

namespace VertexAI.Services.Auth;

public sealed class AccessPolicy
{
    private readonly AccessSettings _settings;

    public AccessPolicy(IOptions<AccessSettings> settings)
    {
        _settings = settings.Value;
    }

    public bool IsAllowed(AuthenticatedUser user, out string? reason)
    {
        reason = null;
        if (user.IsAdmin)
        {
            return true;
        }

        if (_settings.Uids().Contains(user.FirebaseUid ?? "", StringComparer.Ordinal))
        {
            return true;
        }

        if (_settings.RequireVerifiedEmail && !user.EmailVerified)
        {
            reason = "请先验证邮箱后再使用。";
            return false;
        }

        if (!_settings.HasAllowList)
        {
            return true;
        }

        // An allow-list match is only trustworthy for a verified e-mail: Firebase lets anyone sign up
        // with an e-mail they do not own until it is verified.
        if (user.EmailVerified && MatchesEmail(user.Email))
        {
            return true;
        }

        reason = "该账号未被授权使用本站。";
        return false;
    }

    private bool MatchesEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var normalized = email.Trim();
        if (_settings.Emails().Any(item => string.Equals(item.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var at = normalized.LastIndexOf('@');
        if (at < 0)
        {
            return false;
        }

        var domain = normalized[(at + 1)..];
        return _settings.Domains().Any(item =>
            string.Equals(item.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }
}
