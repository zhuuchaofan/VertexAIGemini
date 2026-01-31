using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;

namespace VertexAI.Services;

/// <summary>
/// Gemini 聊天服务 - 封装与 Vertex AI 的交互
/// </summary>
public class GeminiService : IAsyncDisposable
{
    private readonly Client _client;
    private readonly string _modelName;
    private readonly List<Content> _chatHistory = [];
    private readonly GenerateContentConfig _config;

    public GeminiService(IOptions<GeminiSettings> settings)
    {
        var config = settings.Value;
        _modelName = config.ModelName;
        
        // 初始化 Google.GenAI 客户端 (Vertex AI 模式)
        _client = new Client(
            project: config.ProjectId, 
            location: config.Location, 
            vertexAI: true
        );

        // 配置生成参数
        _config = new GenerateContentConfig
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = config.SystemPrompt ?? "你是一个有帮助的AI助手。" }]
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

        Content? assistantContent = null;

        await foreach (var response in _client.Models.GenerateContentStreamAsync(
            model: _modelName,
            contents: _chatHistory,
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
    }

    /// <summary>
    /// 清空聊天历史
    /// </summary>
    public void ClearHistory()
    {
        _chatHistory.Clear();
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
}
