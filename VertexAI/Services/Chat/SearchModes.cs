namespace VertexAI.Services.Chat;

public static class SearchModes
{
    public const string Off = "off";
    public const string Auto = "auto";
    public const string Force = "force";

    public static string Normalize(string? mode, bool? legacyEnableSearch = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return legacyEnableSearch == false ? Off : Auto;
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            Off => Off,
            Force => Force,
            _ => Auto
        };
    }

    public static bool EnablesWebSearch(string mode) =>
        !string.Equals(Normalize(mode), Off, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresWebSearch(string mode) =>
        string.Equals(Normalize(mode), Force, StringComparison.OrdinalIgnoreCase);
}
