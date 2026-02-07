using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace VertexAI.Services;

/// <summary>
/// Gemini 聊天服务 - 封装与 Vertex AI 的流式交互
/// </summary>
public class GeminiService : IAsyncDisposable
{
    private readonly Client _client;
    private readonly string _modelName;
    private readonly ChatHistoryManager _historyManager;
    private readonly ILogger<GeminiService> _logger;
    private GenerateContentConfig _config;

    // 当前系统提示词状态
    private string _currentSystemPrompt;
    private string _currentPresetId = "default";
    private ThinkingLevel _thinkingLevel = ThinkingLevel.MEDIUM;

    // 公开属性 - 委托给 HistoryManager
    public int CurrentTokenCount => _historyManager.CurrentTokenCount;
    public int MaxTokens => _historyManager.MaxTokens;
    public bool HasSummary => _historyManager.HasSummary;

    // 当前预设信息
    public string CurrentPresetId => _currentPresetId;
    public string CurrentCustomPrompt => _currentPresetId == "custom" ? _currentSystemPrompt : "";
    public ThinkingLevel CurrentThinkingLevel => _thinkingLevel;

    // 预设列表（向后兼容）
    public static List<SystemPromptPreset> Presets => SystemPromptPresets.All;

    public GeminiService(IOptions<GeminiSettings> settings, ILogger<GeminiService> logger)
    {
        _logger = logger;
        var config = settings.Value;
        _modelName = config.ModelName;
        _currentSystemPrompt = config.SystemPrompt ?? SystemPromptPresets.All[0].Prompt;

        // 初始化 Vertex AI 客户端
        _client = new Client(
            project: config.ProjectId,
            location: config.Location,
            vertexAI: true
        );

        // 初始化历史管理器
        _historyManager = new ChatHistoryManager(_client, _modelName, config);

        // 配置生成参数
        _config = BuildConfig(_currentSystemPrompt, _thinkingLevel);

        _logger.LogInformation("GeminiService 初始化完成, Model={Model}, Project={Project}",
            _modelName, config.ProjectId);
    }

    /// <summary>
    /// 切换系统提示词
    /// </summary>
    public void SetSystemPrompt(string presetId, string? customPrompt = null)
    {
        _currentPresetId = presetId;

        _currentSystemPrompt = presetId == "custom" && !string.IsNullOrWhiteSpace(customPrompt)
            ? customPrompt
            : SystemPromptPresets.GetById(presetId).Prompt;

        _config = BuildConfig(_currentSystemPrompt, _thinkingLevel);
        ClearHistory();
    }

    /// <summary>
    /// 切换思考级别
    /// </summary>
    public void SetThinkingLevel(ThinkingLevel level)
    {
        _thinkingLevel = level;
        _config = BuildConfig(_currentSystemPrompt, _thinkingLevel);
    }

    /// <summary>
    /// 流式发送消息并返回响应（纯文本）
    /// </summary>
    public IAsyncEnumerable<ChatChunk> StreamChatAsync(string userMessage)
    {
        var parts = new List<Part> { new Part { Text = userMessage } };
        return StreamChatAsync(parts);
    }

    /// <summary>
    /// 流式发送消息并返回响应（多模态：文本+图片）
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(List<Part> userParts)
    {
        var stopwatch = Stopwatch.StartNew();
        var thinkingBudget = _config.ThinkingConfig?.ThinkingBudget;
        var thinkingLevel = _config.ThinkingConfig?.ThinkingLevel;

        // 提取文本用于日志
        var textPart = userParts.FirstOrDefault(p => !string.IsNullOrEmpty(p.Text))?.Text ?? "[多模态消息]";
        var hasImage = userParts.Any(p => p.InlineData != null);

        _logger.LogInformation(
            "Chat 请求开始, MessageLength={Length}, HasImage={HasImage}, ThinkingLevel={Level}, PresetId={Preset}",
            textPart.Length, hasImage, thinkingLevel, _currentPresetId);

        // 添加用户消息
        _historyManager.AddUserMessage(userParts);

        // 检查并修剪历史
        await _historyManager.TrimIfNeededAsync();

        // 获取发送内容
        var contentsToSend = _historyManager.GetContentsForSending();

        // 累积完整回复（流式响应每次只返回增量片段）
        var responseBuilder = new System.Text.StringBuilder();
        var thinkingBuilder = new System.Text.StringBuilder();

        await foreach (var response in _client.Models.GenerateContentStreamAsync(
            model: _modelName,
            contents: contentsToSend,
            config: _config))
        {
            if (response.Candidates is not { Count: > 0 }) continue;

            var candidate = response.Candidates[0];
            var parts = candidate.Content?.Parts;

            if (parts == null) continue;

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part.Text)) continue;

                // 累积内容
                if (part.Thought == true)
                {
                    thinkingBuilder.Append(part.Text);
                }
                else
                {
                    responseBuilder.Append(part.Text);
                }

                yield return new ChatChunk
                {
                    Text = part.Text,
                    IsThinking = part.Thought == true
                };
            }
        }

        // 构建完整的 AI 回复并加入历史
        var responseText = responseBuilder.ToString();
        if (!string.IsNullOrEmpty(responseText))
        {
            var responseParts = new List<Part> { new Part { Text = responseText } };

            // 如果有 thinking 内容，也加入（用于更准确的 Token 计数）
            var thinkingText = thinkingBuilder.ToString();
            if (!string.IsNullOrEmpty(thinkingText))
            {
                responseParts.Insert(0, new Part { Text = thinkingText, Thought = true });
            }

            var assistantContent = new Content
            {
                Role = "model",
                Parts = responseParts
            };
            _historyManager.AddAssistantMessage(assistantContent);
        }

        // 更新 Token 计数
        await _historyManager.UpdateTokenCountAsync();

        stopwatch.Stop();
        _logger.LogInformation(
            "Chat 请求完成, Duration={Duration}ms, TokenCount={Tokens}, HasThinking={HasThinking}",
            stopwatch.ElapsedMilliseconds, _historyManager.CurrentTokenCount, thinkingLevel != null && thinkingBudget != 0);
    }


    /// <summary>
    /// 清空聊天历史
    /// </summary>
    public void ClearHistory() => _historyManager.Clear();

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    /// <summary>
    /// 构建生成配置
    /// </summary>
    private static GenerateContentConfig BuildConfig(string systemPrompt, ThinkingLevel thinkingLevel)
    {
        // 禁用思考: ThinkingBudget=0, IncludeThoughts=false
        // 启用思考: 使用 ThinkingLevel 控制强度
        var isThinkingDisabled = thinkingLevel == ThinkingLevel.THINKING_LEVEL_UNSPECIFIED;

        return new GenerateContentConfig
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = systemPrompt }]
            },
            ThinkingConfig = new ThinkingConfig
            {
                ThinkingBudget = isThinkingDisabled ? 0 : null,  // 0=禁用, null=使用 ThinkingLevel
                ThinkingLevel = isThinkingDisabled ? null : thinkingLevel,
                IncludeThoughts = !isThinkingDisabled
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
}
