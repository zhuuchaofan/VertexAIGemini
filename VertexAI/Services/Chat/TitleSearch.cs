namespace VertexAI.Services.Chat;

/// <summary>
/// Finds conversations by a word in their title. Firestore has no substring search, so this reads the most recent
/// conversations a page at a time (newest first) and filters them here. That covers a person's recent history, which
/// is what a search box is used for, at a bounded cost: at most <see cref="MaxScanned"/> reads per search.
/// </summary>
public static class TitleSearch
{
    public const int MaxScanned = 300;
    public const int PageSize = 100;
    public const int MaxQueryLength = 100;

    /// <summary>The cleaned-up query, or null when there is nothing to search for.</summary>
    public static string? Normalize(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxQueryLength ? trimmed[..MaxQueryLength] : trimmed;
    }

    public static bool Matches(Conversation conversation, string query) =>
        !string.IsNullOrWhiteSpace(conversation.Title)
        && conversation.Title.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <param name="readPage">Reads one page of the person's conversations, newest first, after a cursor.</param>
    public static async Task<List<Conversation>> FindAsync(
        Func<string?, int, Task<List<Conversation>>> readPage,
        string query,
        int limit)
    {
        var found = new List<Conversation>();
        string? cursor = null;
        var scanned = 0;

        while (scanned < MaxScanned && found.Count < limit)
        {
            var page = await readPage(cursor, PageSize);
            if (page.Count == 0) break;

            scanned += page.Count;
            found.AddRange(page.Where(conversation => Matches(conversation, query)));

            if (page.Count < PageSize) break;
            cursor = page[^1].UpdatedAt.ToUniversalTime().ToString("O");
        }

        return found.Take(limit).ToList();
    }
}
