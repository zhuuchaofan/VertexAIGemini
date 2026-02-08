using Google.GenAI;
using Google.GenAI.Types;

namespace VertexAI.Services;

/// <summary>
/// 聊天历史管理器 - 负责对话历史的存储、修剪和摘要
/// </summary>
public class ChatHistoryManager
{
    private readonly List<Content> _chatHistory = [];
    private readonly Client _client;
    private readonly string _modelName;
    private readonly GeminiSettings _settings;

    // 历史摘要（当历史被修剪时存储）
    private string? _historySummary;

    // Token 使用量追踪
    public int CurrentTokenCount { get; private set; }
    public int MaxTokens => _settings.MaxHistoryTokens;
    public bool HasSummary => !string.IsNullOrEmpty(_historySummary);

    public ChatHistoryManager(Client client, string modelName, GeminiSettings settings)
    {
        _client = client;
        _modelName = modelName;
        _settings = settings;
    }

    /// <summary>
    /// 添加用户消息到历史
    /// </summary>
    public void AddUserMessage(string message)
    {
        _chatHistory.Add(new Content
        {
            Role = "user",
            Parts = [new Part { Text = message }]
        });
    }

    /// <summary>
    /// 添加包含多个 Part（文本+图片）的用户消息到历史
    /// </summary>
    public void AddUserMessage(List<Part> parts)
    {
        _chatHistory.Add(new Content
        {
            Role = "user",
            Parts = parts
        });
    }

    /// <summary>
    /// 添加 AI 回复到历史
    /// </summary>
    public void AddAssistantMessage(Content content)
    {
        _chatHistory.Add(content);
    }

    /// <summary>
    /// 获取用于发送的内容（包含摘要上下文）
    /// </summary>
    public List<Content> GetContentsForSending()
    {
        if (string.IsNullOrEmpty(_historySummary))
        {
            return _chatHistory;
        }

        // 在历史开头插入上下文摘要
        var contentsWithSummary = new List<Content>
        {
            new Content
            {
                Role = "user",
                Parts = [new Part { Text = $"[对话背景：{_historySummary}]" }]
            },
            new Content
            {
                Role = "model",
                Parts = [new Part { Text = "好的，我已了解之前的对话背景。" }]
            }
        };

        contentsWithSummary.AddRange(_chatHistory);
        return contentsWithSummary;
    }

    /// <summary>
    /// 检查并在必要时修剪历史
    /// </summary>
    public async Task TrimIfNeededAsync()
    {
        // 1. 硬性限制：轮数超限
        if (_chatHistory.Count > _settings.MaxHistoryRounds * 2)
        {
            await TrimByRoundsAsync();
        }

        // 2. Token 超限检查
        var tokenCount = await CountTokensAsync();
        if (tokenCount >= _settings.SummaryThreshold)
        {
            await TrimByTokensAsync();
        }
    }

    /// <summary>
    /// 更新 Token 计数
    /// </summary>
    public async Task UpdateTokenCountAsync()
    {
        CurrentTokenCount = await CountTokensAsync();
    }

    /// <summary>
    /// 直接设置 Token 计数 (用于从数据库恢复)
    /// </summary>
    public void SetTokenCount(int count)
    {
        CurrentTokenCount = count;
    }

    /// <summary>
    /// 清空聊天历史
    /// </summary>
    public void Clear()
    {
        _chatHistory.Clear();
        _historySummary = null;
        CurrentTokenCount = 0;
    }

    /// <summary>
    /// 计算当前历史的 Token 数量（包含摘要上下文）
    /// </summary>
    private async Task<int> CountTokensAsync()
    {
        if (_chatHistory.Count == 0) return 0;

        try
        {
            // 使用 GetContentsForSending() 获取完整内容（包含摘要上下文）
            var contentsToCount = GetContentsForSending();

            var response = await _client.Models.CountTokensAsync(
                model: _modelName,
                contents: contentsToCount
            );
            return response.TotalTokens ?? 0;
        }
        catch
        {
            // 如果计数失败，使用估算（每字符约 1.5 token）
            var contentsToCount = GetContentsForSending();
            var totalChars = contentsToCount
                .SelectMany(c => c.Parts ?? [])
                .Sum(p => p.Text?.Length ?? 0);
            return (int)(totalChars * 1.5);
        }
    }

    /// <summary>
    /// 按轮数修剪（保留最近 N 轮）
    /// </summary>
    private async Task TrimByRoundsAsync()
    {
        var keepCount = _settings.MaxHistoryRounds * 2; // 每轮 = user + assistant
        var removeCount = _chatHistory.Count - keepCount;

        if (removeCount <= 0) return;

        var oldMessages = _chatHistory.Take(removeCount).ToList();
        await GenerateSummaryAsync(oldMessages);
        _chatHistory.RemoveRange(0, removeCount);
    }

    /// <summary>
    /// 按 Token 数修剪（删除最旧的 50%）
    /// </summary>
    private async Task TrimByTokensAsync()
    {
        var removeCount = _chatHistory.Count / 2;
        if (removeCount <= 0) return;

        // 确保删除的是完整的对话轮（偶数个消息）
        if (removeCount % 2 != 0) removeCount++;

        var oldMessages = _chatHistory.Take(removeCount).ToList();
        await GenerateSummaryAsync(oldMessages);
        _chatHistory.RemoveRange(0, removeCount);
    }

    /// <summary>
    /// 为旧消息生成摘要
    /// </summary>
    private async Task GenerateSummaryAsync(List<Content> oldMessages)
    {
        if (oldMessages.Count == 0) return;

        try
        {
            var summaryPrompt = new Content
            {
                Role = "user",
                Parts = [new Part
                {
                    Text = "请用简洁的中文总结以下对话的关键信息（不超过 200 字）：\n\n" +
                           FormatMessagesForSummary(oldMessages)
                }]
            };

            var summaryConfig = new GenerateContentConfig
            {
                MaxOutputTokens = 512,
                Temperature = 0.3
            };

            var response = await _client.Models.GenerateContentAsync(
                model: _modelName,
                contents: [summaryPrompt],
                config: summaryConfig
            );

            var summaryText = response.Candidates?.FirstOrDefault()?.Content?.Parts?
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .FirstOrDefault() ?? "";

            // 合并现有摘要
            _historySummary = !string.IsNullOrEmpty(_historySummary)
                ? $"{_historySummary}\n\n{summaryText}"
                : summaryText;
        }
        catch
        {
            _historySummary = $"[之前进行了 {oldMessages.Count / 2} 轮对话]";
        }
    }

    /// <summary>
    /// 格式化消息用于摘要生成
    /// </summary>
    private static string FormatMessagesForSummary(List<Content> messages)
    {
        var lines = new List<string>();
        foreach (var msg in messages)
        {
            var role = msg.Role == "user" ? "用户" : "AI";
            var text = string.Join("", msg.Parts?.Select(p => p.Text) ?? []);
            if (text.Length > 200)
            {
                text = text[..200] + "...";
            }
            lines.Add($"{role}: {text}");
        }
        return string.Join("\n", lines);
    }
}
