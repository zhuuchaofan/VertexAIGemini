namespace VertexAI.Services.Chat;

/// <summary>
/// Config-driven model lifecycle rules. Retirement dates come from configuration
/// (ChatModelOption.RetireDate) so a model can be hidden before its provider shuts it down
/// without a code change.
/// </summary>
internal static class ModelLifecycle
{
    public static bool IsRetired(ChatModelOption model, DateOnly today) =>
        TryParseRetireDate(model.RetireDate, out var retireDate) && today >= retireDate;

    /// <summary>
    /// Removes retired models. If every model is retired the list is returned unchanged so the
    /// provider still starts; requests then fail with an explicit upstream error instead of a crash loop.
    /// </summary>
    public static IReadOnlyList<ChatModelOption> FilterAvailable(
        IReadOnlyList<ChatModelOption> models,
        DateOnly? today = null)
    {
        var date = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var available = models.Where(model => !IsRetired(model, date)).ToList();
        return available.Count > 0 ? available : models;
    }

    private static bool TryParseRetireDate(string? value, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out date);
    }
}
