using VertexAI.Services.Chat;

internal static class TitleSearchTests
{
    // Newest first, like the store returns them.
    private static List<Conversation> History(int count, Func<int, string?> title)
    {
        var start = new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(i => new Conversation { Title = title(i), UpdatedAt = start.AddMinutes(-i) })
            .ToList();
    }

    private static Func<string?, int, Task<List<Conversation>>> Reader(List<Conversation> all, List<string?> cursors)
    {
        return (cursor, size) =>
        {
            cursors.Add(cursor);
            var after = cursor is null ? DateTime.MaxValue : DateTime.Parse(cursor, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return Task.FromResult(all.Where(item => item.UpdatedAt < after).Take(size).ToList());
        };
    }

    public static void FindsTitlesIgnoringCaseAndSkipsUntitled()
    {
        var all = History(5, i => i switch { 1 => "Python 入门", 3 => "python 进阶", 4 => null, _ => "别的" });
        var found = Run(TitleSearch.FindAsync(Reader(all, []), "PYTHON", 30));
        Assert.Equal(2, found.Count);
        Assert.True(found.All(item => item.Title!.Contains("ython", StringComparison.OrdinalIgnoreCase)));
    }

    public static void ReadsPageByPageAndStopsAtTheBound()
    {
        var all = History(1000, i => i % 2 == 0 ? "想找的" : "无关");
        var cursors = new List<string?>();
        var found = Run(TitleSearch.FindAsync(Reader(all, cursors), "想找", 1000));
        Assert.Equal(3, cursors.Count);
        Assert.Equal(TitleSearch.MaxScanned / 2, found.Count);
    }

    public static void StopsEarlyOnceThereAreEnough()
    {
        var all = History(1000, _ => "全都匹配");
        var cursors = new List<string?>();
        var found = Run(TitleSearch.FindAsync(Reader(all, cursors), "匹配", 20));
        Assert.Equal(20, found.Count);
        Assert.Equal(1, cursors.Count);
    }

    public static void NormalizeTrimsAndBoundsTheQuery()
    {
        Assert.Null(TitleSearch.Normalize("   "));
        Assert.Null(TitleSearch.Normalize(null));
        Assert.Equal("复利", TitleSearch.Normalize("  复利 "));
        Assert.Equal(TitleSearch.MaxQueryLength, TitleSearch.Normalize(new string('a', 500))!.Length);
    }

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
}
