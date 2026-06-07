using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using VertexAI.Services.Chat;

namespace VertexAI.Services;

/// <summary>
/// Gemini 聊天服务 - 封装与 Vertex AI 的流式交互
/// </summary>
public class GeminiService : IChatModelClient, IAsyncDisposable
{
    private readonly Client _client;
    private readonly string _projectId;
    private readonly ChatHistoryManager _historyManager;
    private readonly ILogger<GeminiService> _logger;
    private string _modelName;
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
    public string CurrentModelName => _modelName;

    // 预设列表与模型选项（已从 appsettings.json 配置解耦并动态加载）
    public List<PresetItemConfig> Presets { get; } = [];
    public IReadOnlyList<GeminiModelOption> ModelOptions { get; } = [];

    public GeminiService(IOptions<GeminiSettings> settings, ILogger<GeminiService> logger)
    {
        _logger = logger;
        var config = settings.Value;
        _projectId = config.ProjectId;
        _modelName = config.ModelName;

        // 绑定并初始化动态配置的 Presets
        Presets = config.Presets ?? [];
        if (Presets.Count == 0)
        {
            Presets = SystemPromptPresets.All.Select(p => new PresetItemConfig
            {
                Id = p.Id,
                Name = p.Name,
                Prompt = p.Prompt,
                Description = p.Description ?? ""
            }).ToList();
        }

        // 绑定并初始化动态配置的 ModelOptions
        ModelOptions = config.Models ?? [];
        if (ModelOptions.Count == 0)
        {
            ModelOptions = new List<GeminiModelOption>
            {
                new() { Name = "Gemini 3.5 Flash", ModelName = "gemini-3.5-flash", Description = "旗舰速度模型，提供极佳的响应速度与多模态能力", SupportsThinking = false, MaxTokens = 1048576 },
                new() { Name = "Gemini 3.1 Flash Lite", ModelName = "gemini-3.1-flash-lite", Description = "超低延迟、极度轻量级，适合日常高频交互", SupportsThinking = false, MaxTokens = 1048576 },
                new() { Name = "Gemini 3 Flash (Preview)", ModelName = "gemini-3-flash-preview", Description = "第三代快速原型预览，均衡的多模态多任务模型", SupportsThinking = false, MaxTokens = 1048576 },
                new() { Name = "Gemini 3.1 Pro (Preview)", ModelName = "gemini-3.1-pro-preview", Description = "深度推理版预览，适合复杂的代码逻辑和长文本深度思考", SupportsThinking = true, MaxTokens = 2097152 }
            };
        }

        var defaultPreset = Presets.FirstOrDefault(p => p.Id == "default") ?? Presets.FirstOrDefault();
        _currentSystemPrompt = config.SystemPrompt ?? defaultPreset?.Prompt ?? "";

        // 初始化 Vertex AI 客户端
        _client = new Client(
            project: _projectId,
            location: config.Location,
            vertexAI: true
        );

        // 初始化历史管理器
        _historyManager = new ChatHistoryManager(_client, _modelName, config);

        // 配置生成参数
        _config = BuildConfig(_currentSystemPrompt, _thinkingLevel);

        _logger.LogInformation("GeminiService 初始化完成, Model={Model}, Project={Project}",
            _modelName, _projectId);
    }

    /// <summary>
    /// 切换系统提示词
    /// </summary>
    public void SetSystemPrompt(string presetId, string? customPrompt = null)
    {
        _currentPresetId = presetId;

        var matchedPreset = Presets.FirstOrDefault(p => p.Id == presetId);
        _currentSystemPrompt = presetId == "custom" && !string.IsNullOrWhiteSpace(customPrompt)
            ? customPrompt
            : matchedPreset?.Prompt ?? "";

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
    /// 切换模型，下一次请求立即生效。
    /// </summary>
    public void SetModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || _modelName == modelName)
        {
            return;
        }

        _modelName = ModelOptions.Any(m => m.ModelName == modelName)
            ? modelName
            : ModelOptions[0].ModelName;

        _historyManager.SetModel(_modelName);
        _logger.LogInformation("Gemini model switched, Model={Model}", _modelName);
    }

    /// <summary>
    /// 流式发送消息并返回响应（纯文本）
    /// </summary>
    public IAsyncEnumerable<ChatChunk> StreamChatAsync(string userMessage)
    {
        return StreamChatAsync(userMessage, false);
    }

    /// <summary>
    /// 流式发送消息并返回响应（纯文本，可选联网搜索）
    /// </summary>
    public IAsyncEnumerable<ChatChunk> StreamChatAsync(string userMessage, bool enableSearch)
    {
        var parts = new List<Part> { new Part { Text = userMessage } };
        return StreamChatAsync(parts, enableSearch);
    }

    /// <summary>
    /// 兼容接口：流式发送消息并返回响应（多模态：文本+图片）
    /// </summary>
    public IAsyncEnumerable<ChatChunk> StreamChatAsync(List<Part> userParts)
    {
        return StreamChatAsync(userParts, false);
    }

    /// <summary>
    /// 流式发送消息并返回响应（多模态：文本+图片，包含 Google 联网搜索支持）
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(List<Part> userParts, bool enableSearch)
    {
        EnsureConfigured();

        var stopwatch = Stopwatch.StartNew();
        var thinkingBudget = _config.ThinkingConfig?.ThinkingBudget;
        var thinkingLevel = _config.ThinkingConfig?.ThinkingLevel;

        // 提取文本用于日志
        var textPart = userParts.FirstOrDefault(p => !string.IsNullOrEmpty(p.Text))?.Text ?? "[多模态消息]";
        var hasImage = userParts.Any(p => p.InlineData != null);

        _logger.LogInformation(
            "Chat 请求开始, MessageLength={Length}, HasImage={HasImage}, ThinkingLevel={Level}, PresetId={Preset}, EnableSearch={EnableSearch}",
            textPart.Length, hasImage, thinkingLevel, _currentPresetId, enableSearch);

        // 添加用户消息
        _historyManager.AddUserMessage(userParts);

        // 检查并修剪历史
        await _historyManager.TrimIfNeededAsync();

        // 获取发送内容
        var contentsToSend = _historyManager.GetContentsForSending();

        // 累积完整回复（流式响应每次只返回增量片段）
        var responseBuilder = new System.Text.StringBuilder();
        var thinkingBuilder = new System.Text.StringBuilder();

        // 动态克隆或构建本次请求的 Config，不污染全局的 _config 实例，保障线程安全性
        var requestConfig = new GenerateContentConfig
        {
            SystemInstruction = _config.SystemInstruction,
            ThinkingConfig = _config.ThinkingConfig,
            MaxOutputTokens = _config.MaxOutputTokens,
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            SafetySettings = _config.SafetySettings
        };

        if (enableSearch)
        {
            requestConfig.Tools = new List<Tool>
            {
                new Tool { GoogleSearch = new GoogleSearch() }
            };
        }

        await foreach (var response in _client.Models.GenerateContentStreamAsync(
            model: _modelName,
            contents: contentsToSend,
            config: requestConfig))
        {
            if (response.Candidates is not { Count: > 0 }) continue;

            var candidate = response.Candidates[0];
            var parts = candidate.Content?.Parts;

            // 提取 Grounding 引用信息 (Google Search Citations)
            List<SearchCitation>? citations = null;
            if (candidate.GroundingMetadata?.GroundingChunks != null)
            {
                foreach (var chunkItem in candidate.GroundingMetadata.GroundingChunks)
                {
                    if (chunkItem.Web != null)
                    {
                        citations ??= new List<SearchCitation>();
                        citations.Add(new SearchCitation
                        {
                            Title = chunkItem.Web.Title ?? "",
                            Uri = chunkItem.Web.Uri ?? ""
                        });
                    }
                }
            }

            if (parts == null && citations != null)
            {
                yield return new ChatChunk
                {
                    Text = "",
                    IsThinking = false,
                    Citations = citations
                };
                continue;
            }

            if (parts == null) continue;

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part.Text) && citations == null) continue;

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
                    Text = part.Text ?? "",
                    IsThinking = part.Thought == true,
                    Citations = citations
                };

                // 每次用完，把 citations 清空以防后续重复发送
                citations = null;
            }
        }

        // 构建完整的 AI 回复并加入历史
        var responseText = responseBuilder.ToString();
        if (!string.IsNullOrEmpty(responseText))
        {
            var responseParts = new List<Part> { new Part { Text = responseText } };

            // 如果有 thinking内容，也加入（用于更准确的 Token 计数）
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

    /// <summary>
    /// 设置 Token 计数 (用于从数据库恢复)
    /// </summary>
    public void SetTokenCount(int count) => _historyManager.SetTokenCount(count);

    /// <summary>
    /// 重新计算 Token 计数 (用于历史对话)
    /// </summary>
    public async Task RecalculateTokenCountAsync() => await _historyManager.UpdateTokenCountAsync();

    /// <summary>
    /// 导入历史消息 (用于页面加载时恢复上下文)
    /// </summary>
    public void ImportHistory(IEnumerable<VertexAI.Data.Entities.Message> messages)
    {
        _historyManager.Clear();
        foreach (var msg in messages)
        {
            if (msg.Role == "user")
            {
                _historyManager.AddUserMessage(msg.Content);
            }
            else if (msg.Role == "model")
            {
                var parts = new List<Part>();
                if (!string.IsNullOrEmpty(msg.ThinkingContent))
                {
                    parts.Add(new Part { Text = msg.ThinkingContent, Thought = true });
                }
                parts.Add(new Part { Text = msg.Content });

                _historyManager.AddAssistantMessage(new Content
                {
                    Role = "model",
                    Parts = parts
                });
            }
        }
    }

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

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_modelName))
        {
            throw new InvalidOperationException("Vertex AI model is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_projectId))
        {
            throw new InvalidOperationException("Vertex AI project is not configured.");
        }
    }
}
