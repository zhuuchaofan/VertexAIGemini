using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;

namespace VertexAI.Services;

/// <summary>
/// 预设的系统提示词
/// </summary>
public class SystemPromptPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
}

/// <summary>
/// Gemini 聊天服务 - 封装与 Vertex AI 的交互
/// 支持滑动窗口、Token 计数和自动摘要的对话历史管理
/// </summary>
public class GeminiService : IAsyncDisposable
{
    private readonly Client _client;
    private readonly string _modelName;
    private readonly List<Content> _chatHistory = [];
    private readonly GeminiSettings _settings;
    private GenerateContentConfig _config;
    
    // 历史摘要（当历史被修剪时存储）
    private string? _historySummary;
    
    // 当前系统提示词
    private string _currentSystemPrompt;
    private string _currentPresetId = "default";
    
    // Token 使用量追踪
    public int CurrentTokenCount { get; private set; }
    public int MaxTokens => _settings.MaxHistoryTokens;
    public bool HasSummary => !string.IsNullOrEmpty(_historySummary);
    
    // 当前选中的预设 ID
    public string CurrentPresetId => _currentPresetId;
    
    // 预设提示词列表
    public static readonly List<SystemPromptPreset> Presets =
    [
        new SystemPromptPreset
        {
            Id = "default",
            Name = "默认助手",
            Prompt = "你是一个有帮助的AI助手。请用清晰、准确的语言回答问题。",
            Description = "通用对话助手",
            Icon = "🤖"
        },
        new SystemPromptPreset
        {
            Id = "shaanxi",
            Name = "陕西老哥",
            Prompt = "你是一个暴躁的陕西关中西安人，不管用户问什么，你都用关中方言话回答。你总是以中文回复，你说话的风格就像是文学大师季羡林一样，但是你是一个20来岁愤世嫉俗的小伙子。",
            Description = "暴躁陕西方言",
            Icon = "🌶️"
        },
        new SystemPromptPreset
        {
            Id = "programmer",
            Name = "编程专家",
            Prompt = "你是一位资深的全栈开发工程师，精通多种编程语言和框架。你的回答应该：1) 提供清晰的代码示例 2) 解释技术原理 3) 考虑最佳实践和性能优化 4) 指出潜在的陷阱和注意事项。",
            Description = "技术问答专家",
            Icon = "💻"
        },
        new SystemPromptPreset
        {
            Id = "translator",
            Name = "翻译官",
            Prompt = "你是一位专业的中英翻译官。当用户输入中文时，翻译成地道的英文；当用户输入英文时，翻译成流畅的中文。保持原文的语气和风格，必要时提供多种译法选择。",
            Description = "中英互译",
            Icon = "🌐"
        },
        new SystemPromptPreset
        {
            Id = "writer",
            Name = "文案写手",
            Prompt = "你是一位创意文案写手，擅长撰写各类文章、广告文案和社交媒体内容。你的文字富有感染力，能够根据不同场景调整风格。请根据用户需求创作引人入胜的内容。",
            Description = "创意写作",
            Icon = "✍️"
        },
        new SystemPromptPreset
        {
            Id = "custom",
            Name = "自定义",
            Prompt = "",
            Description = "输入自定义提示词",
            Icon = "⚙️"
        }
    ];

    public GeminiService(IOptions<GeminiSettings> settings)
    {
        _settings = settings.Value;
        _modelName = _settings.ModelName;
        _currentSystemPrompt = _settings.SystemPrompt ?? Presets[0].Prompt;
        
        // 初始化 Google.GenAI 客户端 (Vertex AI 模式)
        _client = new Client(
            project: _settings.ProjectId, 
            location: _settings.Location, 
            vertexAI: true
        );

        // 配置生成参数
        _config = BuildConfig(_currentSystemPrompt);
    }

    /// <summary>
    /// 构建生成配置
    /// </summary>
    private static GenerateContentConfig BuildConfig(string systemPrompt)
    {
        return new GenerateContentConfig
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = systemPrompt }]
            },
            ThinkingConfig = new ThinkingConfig
            {
                ThinkingLevel = ThinkingLevel.MEDIUM,
                IncludeThoughts = true
            },
            MaxOutputTokens = 4096,
            Temperature = 1,
            TopP = 0.9,
            SafetySettings =
            [
                new SafetySetting { Category = HarmCategory.HARM_CATEGORY_HARASSMENT, Threshold = HarmBlockThreshold.OFF },
                new SafetySetting { Category = HarmCategory.HARM_CATEGORY_HATE_SPEECH, Threshold = HarmBlockThreshold.OFF },
                new SafetySetting { Category = HarmCategory.HARM_CATEGORY_DANGEROUS_CONTENT, Threshold = HarmBlockThreshold.OFF },
                new SafetySetting { Category = HarmCategory.HARM_CATEGORY_IMAGE_SEXUALLY_EXPLICIT, Threshold = HarmBlockThreshold.OFF }
            ]
        };
    }

    /// <summary>
    /// 切换系统提示词
    /// </summary>
    public void SetSystemPrompt(string presetId, string? customPrompt = null)
    {
        _currentPresetId = presetId;
        
        if (presetId == "custom" && !string.IsNullOrWhiteSpace(customPrompt))
        {
            _currentSystemPrompt = customPrompt;
        }
        else
        {
            var preset = Presets.FirstOrDefault(p => p.Id == presetId) ?? Presets[0];
            _currentSystemPrompt = preset.Prompt;
        }
        
        // 重建配置
        _config = BuildConfig(_currentSystemPrompt);
        
        // 清空历史（切换人设需要重新开始对话）
        ClearHistory();
    }

    /// <summary>
    /// 流式发送消息并返回响应
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(string userMessage)
    {
        // 添加用户消息到历史
        _chatHistory.Add(new Content
        {
            Role = "user",
            Parts = [new Part { Text = userMessage }]
        });

        // 检查并修剪历史（如需要）
        await TrimHistoryIfNeededAsync();

        // 构建发送内容（如有摘要，注入系统上下文）
        var contentsToSend = BuildContentsWithSummary();

        Content? assistantContent = null;

        await foreach (var response in _client.Models.GenerateContentStreamAsync(
            model: _modelName,
            contents: contentsToSend,
            config: _config))
        {
            if (response.Candidates is not { Count: > 0 }) continue;

            var candidate = response.Candidates[0];
            var parts = candidate.Content?.Parts;
            
            if (parts == null) continue;

            // 保存最后一个完整的 Content 用于历史记录
            if (candidate.Content != null)
            {
                assistantContent = candidate.Content;
            }

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part.Text)) continue;

                yield return new ChatChunk
                {
                    Text = part.Text,
                    IsThinking = part.Thought == true
                };
            }
        }

        // 将 AI 回复加入历史
        if (assistantContent != null)
        {
            _chatHistory.Add(assistantContent);
        }

        // 更新 Token 计数
        await UpdateTokenCountAsync();
    }

    /// <summary>
    /// 计算当前历史的 Token 数量
    /// </summary>
    private async Task<int> CountHistoryTokensAsync()
    {
        if (_chatHistory.Count == 0) return 0;

        try
        {
            var response = await _client.Models.CountTokensAsync(
                model: _modelName,
                contents: _chatHistory
            );
            return response.TotalTokens ?? 0;
        }
        catch
        {
            // 如果计数失败，使用估算（每字符约 1.5 token）
            var totalChars = _chatHistory
                .SelectMany(c => c.Parts ?? [])
                .Sum(p => p.Text?.Length ?? 0);
            return (int)(totalChars * 1.5);
        }
    }

    /// <summary>
    /// 更新 Token 计数
    /// </summary>
    private async Task UpdateTokenCountAsync()
    {
        CurrentTokenCount = await CountHistoryTokensAsync();
    }

    /// <summary>
    /// 检查并在必要时修剪历史
    /// </summary>
    private async Task TrimHistoryIfNeededAsync()
    {
        // 1. 硬性限制：轮数超限
        if (_chatHistory.Count > _settings.MaxHistoryRounds * 2)
        {
            await TrimByRoundsAsync();
        }

        // 2. Token 超限检查
        var tokenCount = await CountHistoryTokensAsync();
        if (tokenCount >= _settings.SummaryThreshold)
        {
            await TrimByTokensAsync();
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

        // 提取要删除的消息
        var oldMessages = _chatHistory.Take(removeCount).ToList();
        
        // 生成摘要
        await GenerateSummaryAsync(oldMessages);
        
        // 删除旧消息
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

        // 提取要删除的消息
        var oldMessages = _chatHistory.Take(removeCount).ToList();
        
        // 生成摘要
        await GenerateSummaryAsync(oldMessages);
        
        // 删除旧消息
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
            // 构建摘要请求
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

            // 从 Candidates 中提取文本
            var summaryText = response.Candidates?.FirstOrDefault()?.Content?.Parts?
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .FirstOrDefault() ?? "";
            
            // 合并现有摘要
            if (!string.IsNullOrEmpty(_historySummary))
            {
                _historySummary = $"{_historySummary}\n\n{summaryText}";
            }
            else
            {
                _historySummary = summaryText;
            }
        }
        catch
        {
            // 摘要生成失败时，使用简单描述
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
            // 限制每条消息长度
            if (text.Length > 200)
            {
                text = text[..200] + "...";
            }
            lines.Add($"{role}: {text}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 构建包含摘要的发送内容
    /// </summary>
    private List<Content> BuildContentsWithSummary()
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
    /// 清空聊天历史
    /// </summary>
    public void ClearHistory()
    {
        _chatHistory.Clear();
        _historySummary = null;
        CurrentTokenCount = 0;
    }

    public ValueTask DisposeAsync()
    {
        return _client.DisposeAsync();
    }
}

/// <summary>
/// 聊天响应块
/// </summary>
public record ChatChunk
{
    public required string Text { get; init; }
    public bool IsThinking { get; init; }
}

/// <summary>
/// Gemini 配置
/// </summary>
public class GeminiSettings
{
    public string ProjectId { get; set; } = "";
    public string Location { get; set; } = "global";
    public string ModelName { get; set; } = "gemini-3-flash-preview";
    public string? SystemPrompt { get; set; }
    
    // 历史管理配置
    public int MaxHistoryTokens { get; set; } = 100000;   // 最大历史 Token 数
    public int MaxHistoryRounds { get; set; } = 20;        // 最大对话轮数
    public int SummaryThreshold { get; set; } = 80000;     // 触发摘要的 Token 阈值
}
