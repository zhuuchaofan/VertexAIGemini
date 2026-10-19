using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Search;

public static class WebSearchStatus
{
    /// <summary>Results were fetched and given to the model.</summary>
    public const string Used = "used";
    /// <summary>The question did not need the web (or search was off).</summary>
    public const string Skipped = "skipped";
    /// <summary>Search was wanted but did not work; the model answered without it.</summary>
    public const string Failed = "failed";
    /// <summary>Search is not configured here.</summary>
    public const string Unavailable = "unavailable";
}

public sealed record WebSearchRequest(
    string Message,
    IReadOnlyList<ChatHistoryEntry> History,
    string Mode,
    DateTimeOffset Now);

/// <param name="ProviderCalls">How many requests to the search backend were made (the router and the search itself).</param>
public sealed record WebSearchOutcome(
    string Status,
    string? Query,
    string? Digest,
    IReadOnlyList<SearchCitation> Citations,
    string? Reason,
    int ProviderCalls)
{
    public static WebSearchOutcome None(string status, string? reason, int calls = 0) =>
        new(status, null, null, [], reason, calls);
}

/// <summary>Decides whether a question needs the web, and finds what it needs.</summary>
public interface IWebSearchService
{
    Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>Used when search is switched off in configuration: says so instead of pretending.</summary>
public sealed class UnavailableWebSearchService : IWebSearchService
{
    public Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(SearchModes.Normalize(request.Mode) == SearchModes.Off
            ? WebSearchOutcome.None(WebSearchStatus.Skipped, "联网已关闭")
            : WebSearchOutcome.None(WebSearchStatus.Unavailable, "这个部署没有启用联网检索"));
}

/// <summary>The two model calls behind a search. The Gemini implementation is the real one.</summary>
public interface IWebSearchBackend
{
    /// <summary>Asks a small model for {"search": bool, "query": "..."} and returns its raw text.</summary>
    Task<string> DecideAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>Searches the web for the query and returns a digest with its sources.</summary>
    Task<WebSearchFindings> SearchAsync(string query, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record WebSearchFindings(string Digest, IReadOnlyList<SearchCitation> Citations);

public sealed class WebSearchSettings
{
    /// <summary>"gemini" (default) or "fake" (development and tests only).</summary>
    public string Backend { get; set; } = "gemini";
    public bool Enabled { get; set; } = true;
    /// <summary>Model used for both the router and the search; empty means the default Gemini model.</summary>
    public string ModelName { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 25;
    /// <summary>Hours east of UTC for the date the model is told. Beijing time by default.</summary>
    public int UtcOffsetHours { get; set; } = 8;
}

/// <summary>Decides, searches, and never lets a search problem break the answer itself.</summary>
public sealed class WebSearchService : IWebSearchService
{
    private readonly IWebSearchBackend _backend;
    private readonly TimeSpan _timeout;

    public WebSearchService(IWebSearchBackend backend, TimeSpan timeout)
    {
        _backend = backend;
        _timeout = timeout;
    }

    public async Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        var mode = SearchModes.Normalize(request.Mode);
        if (mode == SearchModes.Off) return WebSearchOutcome.None(WebSearchStatus.Skipped, "联网已关闭");

        var calls = 0;
        var query = Clip(request.Message, 400);
        var wanted = mode == SearchModes.Force;
        string? note = null;

        // Router: needed in auto mode to decide, and in force mode to turn the question into a good search query.
        try
        {
            calls++;
            var raw = await WithTimeout(token => _backend.DecideAsync(WebSearchPrompt.Router(request), token), cancellationToken);
            var decision = SearchDecision.Parse(raw);
            if (decision is null) throw new FormatException("The router did not return JSON.");
            if (mode == SearchModes.Auto) wanted = decision.Search;
            if (!string.IsNullOrWhiteSpace(decision.Query)) query = Clip(decision.Query, 400);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The router is a convenience. Without it, fall back to plain rules.
            note = $"判断是否联网时出错（{ex.GetType().Name}），改用关键词规则";
            if (mode == SearchModes.Auto) wanted = TimeSensitivity.LooksTimeSensitive(request.Message);
        }

        if (!wanted) return WebSearchOutcome.None(WebSearchStatus.Skipped, note ?? "判断无需联网", calls);

        try
        {
            calls++;
            var findings = await WithTimeout(token => _backend.SearchAsync(query, request.Now, token), cancellationToken);
            if (string.IsNullOrWhiteSpace(findings.Digest))
            {
                return new WebSearchOutcome(WebSearchStatus.Failed, query, null, [], "检索没有返回内容", calls);
            }

            return new WebSearchOutcome(WebSearchStatus.Used, query, Clip(findings.Digest, 8000), Dedupe(findings.Citations), note, calls);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebSearchOutcome(WebSearchStatus.Failed, query, null, [], $"检索失败（{ex.GetType().Name}）", calls);
        }
    }

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            return await action(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer within {_timeout.TotalSeconds:0} seconds.");
        }
    }

    private static List<SearchCitation> Dedupe(IEnumerable<SearchCitation> citations)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return citations.Where(c => !string.IsNullOrWhiteSpace(c.Uri) && seen.Add(c.Uri)).Take(12).ToList();
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}

public sealed record SearchDecision(bool Search, string? Query)
{
    /// <summary>Reads the router's answer, tolerating text around the JSON object. Null when there is none.</summary>
    public static SearchDecision? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = document.RootElement;
            if (!root.TryGetProperty("search", out var search) || search.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var query = root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
            return new SearchDecision(search.GetBoolean(), query);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Plain rules for when the router is unavailable: things that change over time or ask for the present.</summary>
public static partial class TimeSensitivity
{
    [GeneratedRegex(
        @"今天|今日|今晚|明天|昨天|现在|目前|当前|最新|最近|近期|刚刚|实时|本周|本月|今年|几号|星期几|" +
        @"天气|气温|股价|股票|汇率|油价|金价|价格|新闻|热搜|比分|赛程|排名|榜单|发布|上市|版本|" +
        @"搜索|搜一下|查一下|查查|联网|" +
        @"\b(today|tonight|tomorrow|yesterday|latest|current|currently|news|weather|price|stock|score|release)\b|20[2-9]\d\s*年?",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool LooksTimeSensitive(string message) => Pattern().IsMatch(message);
}

public static class ChatTime
{
    private static readonly string[] Weekdays = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];

    /// <summary>"2026年10月19日 星期一 09:48（UTC+8）".</summary>
    public static string Describe(DateTimeOffset now, int utcOffsetHours)
    {
        var local = now.ToOffset(TimeSpan.FromHours(utcOffsetHours));
        var sign = utcOffsetHours >= 0 ? "+" : "-";
        return $"{local:yyyy年M月d日} {Weekdays[(int)local.DayOfWeek]} {local:HH:mm}（UTC{sign}{Math.Abs(utcOffsetHours)}）";
    }
}

public static class WebSearchPrompt
{
    public const int OffsetHours = 8;

    public static string Router(WebSearchRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是联网检索路由器。判断要回答用户的最后一个问题，是否需要联网获取最新的或可核实的外部信息。");
        builder.AppendLine("需要：新闻、天气、价格、汇率、股价、比分、赛程、软件或产品的当前版本、近期发生的事件、法规政策、具体公司或人物的最新情况、用户明确要求搜索或查证。");
        builder.AppendLine("不需要：写作、润色、翻译、改写、总结用户给出的文字、数学、编程、解释通用概念、闲聊、用户已经提供了全部材料的任务。");
        builder.AppendLine("如果需要，给出一条适合搜索引擎的简短查询：补全指代（结合上下文），保留关键的名称、地点和时间，不超过 40 个字。");
        builder.AppendLine("只输出一个 JSON 对象，不要其他文字：{\"search\": true 或 false, \"query\": \"查询\"}（不需要时 query 留空）。");
        builder.AppendLine($"现在是：{ChatTime.Describe(request.Now, OffsetHours)}。");

        var recent = request.History.TakeLast(4).ToList();
        if (recent.Count > 0)
        {
            builder.AppendLine("最近的对话（供理解指代）：");
            foreach (var entry in recent)
            {
                var text = entry.Content.Length > 300 ? entry.Content[..300] + "…" : entry.Content;
                builder.AppendLine($"{(entry.Role == "user" ? "用户" : "助手")}：{text}");
            }
        }

        builder.AppendLine($"用户的最后一个问题：{request.Message}");
        return builder.ToString();
    }

    public static string Search(string query, DateTimeOffset now) =>
        $"现在是：{ChatTime.Describe(now, OffsetHours)}。\n" +
        "请联网检索并整理回答下面查询所需的事实。用中文，按要点列出，包含具体的数字、日期和信息来源的名称；" +
        "只写与查询直接相关的内容；搜不到或有出入的地方明确写出，不要猜测。\n" +
        $"查询：{query}";

    /// <summary>The question as the model sees it: the date, the search results with numbered sources, then the question.</summary>
    public static string Compose(string message, DateTimeOffset now, WebSearchOutcome? outcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[当前时间：{ChatTime.Describe(now, OffsetHours)}]");

        if (outcome is { Status: WebSearchStatus.Used, Digest: not null })
        {
            builder.AppendLine();
            builder.AppendLine($"[联网检索结果｜查询：{outcome.Query}]");
            builder.AppendLine(outcome.Digest);
            if (outcome.Citations.Count > 0)
            {
                builder.AppendLine("来源：");
                for (var i = 0; i < outcome.Citations.Count; i++)
                {
                    var title = string.IsNullOrWhiteSpace(outcome.Citations[i].Title) ? outcome.Citations[i].Uri : outcome.Citations[i].Title;
                    builder.AppendLine($"[{i + 1}] {title}");
                }
            }

            builder.AppendLine("[检索结果结束]");
            builder.AppendLine();
            builder.AppendLine("回答时以上面的检索结果为依据，引用时用 [1]、[2] 这样的编号；结果不足以回答的部分要明说，不要编造。");
        }
        else if (outcome is { Status: WebSearchStatus.Failed or WebSearchStatus.Unavailable })
        {
            builder.AppendLine();
            builder.AppendLine("[说明：本次联网检索没有成功，你没有最新的外部信息。涉及时效性的内容请明说无法确认，不要编造。]");
        }

        builder.AppendLine();
        builder.Append(message);
        return builder.ToString();
    }
}
