using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;
using VertexAI.Components.Chat;
using VertexAI.Services;

namespace VertexAI.Components.Pages;

public partial class Chat : ComponentBase
{
    [Inject] private ILogger<Chat> Logger { get; set; } = default!;

    private const int MaxMessageLength = 10000;
    private readonly List<ChatMessageModel> _messages = [];
    private List<ConversationItem> _conversationItems = [];
    private List<PresetItem> _presetItems = [];
    private List<ImageAttachment> _pendingImages = new();
    private Guid? _currentConversationId;
    private string _inputMessage = "";
    private string? _validationError;
    private bool _isLoading;
    private bool _initialized;
    private bool _showSidebar = true;
    private bool _showCustomDialog;
    private bool _showDeleteConfirm;
    private bool _showImagePreview;
    private Guid? _deleteTargetId;
    private string _customPromptInput = "";
    private string _previewImageUrl = "";
    private bool _showSwitchConfirm;
    private string? _pendingSwitchPresetId;
    private string? _pendingSwitchCustomPrompt;
    private ChatHeader? _headerRef;
    private VertexAI.Components.Chat.ChatInput? _chatInputRef;

    protected override async Task OnInitializedAsync()
    {
        // 初始化预设列表
        _presetItems = GeminiService.Presets.Select(p => new PresetItem
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description
        }).ToList();

        if (!Auth.IsAuthenticated)
        {
            await Auth.InitializeAsync();
        }

        _initialized = true;

        if (Auth.IsAuthenticated)
        {
            _ = LoadConversationsAsync().ContinueWith(_ => InvokeAsync(StateHasChanged));
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // 从 localStorage 恢复思考模式设置
            await RestoreThinkingLevelAsync();
        }
    }

    private async Task RestoreThinkingLevelAsync()
    {
        try
        {
            var savedLevel = await JS.InvokeAsync<string?>("localStorage.getItem", "thinkingLevel");
            if (!string.IsNullOrEmpty(savedLevel) && Enum.TryParse<Google.GenAI.Types.ThinkingLevel>(savedLevel, out var level))
            {
                Gemini.SetThinkingLevel(level);
                StateHasChanged();
            }
        }
        catch (Exception) { /* localStorage 在 SSR/预渲染阶段不可用，属预期行为 */ }
    }

    private async Task LoadConversationsAsync()
    {
        try
        {
            var convs = await ConversationSvc.GetUserConversationsAsync(Auth.CurrentUser.Id);
            _conversationItems = convs.Select(c => new ConversationItem { Id = c.Id, Title = c.Title }).ToList();
        }
        catch
        {
            _conversationItems = [];
        }
    }

    /// <summary>
    /// 开始新对话（只清空 UI，不创建数据库记录）
    /// </summary>
    private void StartNewChat()
    {
        _currentConversationId = null;
        _messages.Clear();
        Gemini.ClearHistory();
    }

    /// <summary>
    /// 创建数据库对话记录（仅在发送第一条消息时调用）
    /// </summary>
    private async Task<Guid?> EnsureConversationAsync()
    {
        if (_currentConversationId.HasValue) return _currentConversationId;

        var conv = await ConversationSvc.CreateConversationAsync(
            Auth.CurrentUser.Id, Gemini.CurrentPresetId, Gemini.CurrentCustomPrompt);
        _currentConversationId = conv?.Id;
        await LoadConversationsAsync();
        return _currentConversationId;
    }

    private async Task LoadConversation(Guid conversationId)
    {
        var conv = await ConversationSvc.GetConversationAsync(conversationId, Auth.CurrentUser.Id);
        if (conv == null) return;

        _currentConversationId = conversationId;
        _messages.Clear();

        // 1. 设置系统提示词
        Gemini.SetSystemPrompt(conv.PresetId, conv.CustomPrompt);

        // 2. 导入历史记录到 GeminiService (关键修复：恢复上下文)
        Gemini.ImportHistory(conv.Messages);

        // 3. 恢复 UI 显示
        foreach (var msg in conv.Messages)
        {
            _messages.Add(new ChatMessageModel
            {
                IsUser = msg.Role == "user",
                Content = msg.Content,
                ThinkingContent = msg.ThinkingContent
            });
        }

        // 4. 恢复 Token 计数
        if (conv.TokenCount > 0)
        {
            Gemini.SetTokenCount(conv.TokenCount);
        }
        else if (conv.Messages.Count > 0)
        {
            await Gemini.RecalculateTokenCountAsync();
            await ConversationSvc.UpdateTokenCountAsync(conversationId, Gemini.CurrentTokenCount);
        }
    }

    private async Task SendMessage()
    {
        _validationError = null;

        // 允许只发送图片，或文本+图片
        var hasText = !string.IsNullOrWhiteSpace(_inputMessage);
        var hasImages = _pendingImages.Count > 0;

        if (!hasText && !hasImages || _isLoading) return;

        // 消息长度验证
        if (hasText && _inputMessage.Length > MaxMessageLength)
        {
            _validationError = $"消息长度不能超过 {MaxMessageLength:N0} 个字符（当前 {_inputMessage.Length:N0} 个字符）";
            StateHasChanged();
            return;
        }

        if (_currentConversationId == null)
        {
            await EnsureConversationAsync();
        }

        var userMessage = _inputMessage.Trim();
        var imagesToSend = _pendingImages.ToList();
        _inputMessage = "";
        _pendingImages.Clear();
        _isLoading = true;

        // 构建用户消息（包含图片附件）
        _messages.Add(new ChatMessageModel
        {
            IsUser = true,
            Content = userMessage,
            Attachments = imagesToSend.Count > 0 ? imagesToSend : null
        });
        await ScrollToBottom();
        StateHasChanged();

        if (_currentConversationId.HasValue)
        {
            await ConversationSvc.AddMessageAsync(_currentConversationId.Value, "user", userMessage);
        }

        var aiMessage = new ChatMessageModel { IsUser = false, Content = "", IsStreaming = true };
        _messages.Add(aiMessage);

        try
        {
            var thinkingBuilder = new System.Text.StringBuilder();
            var responseBuilder = new System.Text.StringBuilder();

            // 构建 Parts 列表（文本 + 图片）
            var parts = new List<Google.GenAI.Types.Part>();

            if (hasText)
            {
                parts.Add(new Google.GenAI.Types.Part { Text = userMessage });
            }

            foreach (var img in imagesToSend)
            {
                parts.Add(new Google.GenAI.Types.Part
                {
                    InlineData = new Google.GenAI.Types.Blob
                    {
                        Data = Convert.FromBase64String(img.Base64Data),
                        MimeType = img.MimeType
                    }
                });
            }

            await foreach (var chunk in Gemini.StreamChatAsync(parts))
            {
                if (chunk.IsThinking)
                {
                    thinkingBuilder.Append(chunk.Text);
                    aiMessage.ThinkingContent = thinkingBuilder.ToString();
                }
                else
                {
                    responseBuilder.Append(chunk.Text);
                    aiMessage.Content = responseBuilder.ToString();
                }
                StateHasChanged();
                await ScrollToBottom();
            }

            if (_currentConversationId.HasValue)
            {
                await ConversationSvc.AddMessageAsync(
                    _currentConversationId.Value, "model", aiMessage.Content, aiMessage.ThinkingContent);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Chat 请求失败");
            aiMessage.Content = ex switch
            {
                TaskCanceledException => "请求超时，请重试",
                HttpRequestException => "网络连接异常，请检查网络后重试",
                _ when ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) => "API 配额已用尽，请稍后再试",
                _ when ex.Message.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) => "AI 服务暂时不可用，请稍后重试",
                _ when ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) => "权限不足，请联系管理员",
                _ => "服务暂时不可用，请稍后重试"
            };
        }
        finally
        {
            aiMessage.IsStreaming = false;
            _isLoading = false;
            await LoadConversationsAsync();
            StateHasChanged();

            // 保存 Token 计数到数据库
            if (_currentConversationId.HasValue)
            {
                await ConversationSvc.UpdateTokenCountAsync(
                    _currentConversationId.Value, Gemini.CurrentTokenCount);
            }

            // 短暂延迟后再次刷新，确保 Token 计数已更新
            await Task.Delay(100);
            StateHasChanged();

            // 发送完成后自动聚焦输入框
            if (_chatInputRef is not null)
            {
                await _chatInputRef.FocusAsync();
            }
        }
    }

    /// <summary>
    /// 处理文件选择事件
    /// </summary>
    private async Task HandleFilesSelected(InputFileChangeEventArgs e)
    {
        foreach (var file in e.GetMultipleFiles(5))
        {
            var result = await ImageSvc.ProcessImageAsync(file);

            if (result.IsSuccess)
            {
                _pendingImages.Add(new ImageAttachment
                {
                    Base64Data = result.Base64Data!,
                    MimeType = result.MimeType!,
                    FileName = result.FileName
                });
            }
            else
            {
                _chatInputRef?.SetError(result.ErrorMessage!);
                break;
            }
        }
        StateHasChanged();
    }


    private async Task SignOut()
    {
        try
        {
            // 使用 JS 调用登出 API（浏览器端处理 Cookie）
            await JS.InvokeVoidAsync("authLogout");
        }
        catch (Exception) { /* 登出 API 失败不阻止本地状态清理 */ }

        await Auth.SignOutAsync();
        // 刷新页面以确保 Cookie 被清除
        Navigation.NavigateTo("/login", forceLoad: true);
    }

    private void ToggleSidebar() => _showSidebar = !_showSidebar;

    private void ClearChat()
    {
        _messages.Clear();
        Gemini.ClearHistory();
        _currentConversationId = null;
    }

    private void DeleteConversation(Guid conversationId)
    {
        _deleteTargetId = conversationId;
        _showDeleteConfirm = true;
    }

    private void CancelDelete()
    {
        _showDeleteConfirm = false;
        _deleteTargetId = null;
    }

    private async Task ConfirmDelete()
    {
        if (_deleteTargetId.HasValue)
        {
            await ConversationSvc.DeleteConversationAsync(_deleteTargetId.Value, Auth.CurrentUser.Id);

            if (_currentConversationId == _deleteTargetId)
            {
                ClearChat();
            }

            await LoadConversationsAsync();
        }

        _showDeleteConfirm = false;
        _deleteTargetId = null;
        StateHasChanged();
    }

    private async Task ScrollToBottom()
    {
        try
        {
            await JS.InvokeVoidAsync("scrollToBottom", "messages-container");
        }
        catch { /* 忽略 JS 调用失败 */ }
    }

    private void SelectPreset(string presetId)
    {
        // 如果当前有对话内容，弹出确认框
        if (_messages.Count > 0)
        {
            _pendingSwitchPresetId = presetId;
            _pendingSwitchCustomPrompt = null;
            _showSwitchConfirm = true;
            return;
        }

        Gemini.SetSystemPrompt(presetId);
        _messages.Clear();
        _currentConversationId = null;
    }

    private void ShowCustomPromptDialog()
    {
        _showCustomDialog = true;
        _customPromptInput = Gemini.CurrentCustomPrompt;
    }

    private void CloseCustomDialog() => _showCustomDialog = false;
    private void CloseMenus() => _headerRef?.CloseMenus();

    private void ApplyCustomPrompt(string prompt)
    {
        // 如果当前有对话内容，弹出确认框
        if (_messages.Count > 0)
        {
            _pendingSwitchPresetId = "custom";
            _pendingSwitchCustomPrompt = prompt;
            _showSwitchConfirm = true;
            _showCustomDialog = false;
            return;
        }

        Gemini.SetSystemPrompt("custom", prompt);
        _messages.Clear();
        _showCustomDialog = false;
        _currentConversationId = null;
    }

    private void CancelSwitchPreset()
    {
        _showSwitchConfirm = false;
        _pendingSwitchPresetId = null;
        _pendingSwitchCustomPrompt = null;
    }

    private void ConfirmSwitchPreset()
    {
        if (_pendingSwitchPresetId != null)
        {
            Gemini.SetSystemPrompt(_pendingSwitchPresetId, _pendingSwitchCustomPrompt);
            _messages.Clear();
            _currentConversationId = null;
        }

        _showSwitchConfirm = false;
        _pendingSwitchPresetId = null;
        _pendingSwitchCustomPrompt = null;
    }

    private async Task SelectThinkingLevel(Google.GenAI.Types.ThinkingLevel level)
    {
        Gemini.SetThinkingLevel(level);
        // 持久化到 localStorage
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", "thinkingLevel", level.ToString());
        }
        catch (Exception) { /* localStorage 在 SSR/预渲染阶段不可用，属预期行为 */ }
    }

    private string GetCurrentPresetName() => GeminiService.Presets.FirstOrDefault(p => p.Id == Gemini.CurrentPresetId)?.Name ?? "默认助手";

    private void ShowImagePreview(string imageUrl)
    {
        _previewImageUrl = imageUrl;
        _showImagePreview = true;
    }

    private void CloseImagePreview()
    {
        _showImagePreview = false;
        _previewImageUrl = "";
    }
}
