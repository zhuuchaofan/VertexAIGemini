using System.Text.RegularExpressions;

namespace VertexAI.Services.Auth;

public static class AuthInputValidator
{
    public static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email) =>
        Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    public static string? ValidatePasswordStrength(string password)
    {
        if (password.Length < 6)
        {
            return "\u5bc6\u7801\u957f\u5ea6\u81f3\u5c11\u9700\u8981 6 \u4e2a\u5b57\u7b26";
        }

        if (password.Length > 100)
        {
            return "\u5bc6\u7801\u957f\u5ea6\u4e0d\u80fd\u8d85\u8fc7 100 \u4e2a\u5b57\u7b26";
        }

        var hasLetter = false;
        var hasDigit = false;

        foreach (var c in password)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
            }

            if (char.IsDigit(c))
            {
                hasDigit = true;
            }

            if (hasLetter && hasDigit)
            {
                break;
            }
        }

        return hasLetter && hasDigit
            ? null
            : "\u5bc6\u7801\u9700\u8981\u540c\u65f6\u5305\u542b\u5b57\u6bcd\u548c\u6570\u5b57";
    }
}
