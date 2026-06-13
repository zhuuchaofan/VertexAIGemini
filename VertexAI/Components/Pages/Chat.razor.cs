using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;
using VertexAI.Components.Chat;
using VertexAI.Services;
using VertexAI.Services.Chat;

namespace VertexAI.Components.Pages;

public partial class Chat : ComponentBase
{
    [Inject] private ILogger<Chat> Logger { get; set; } = default!;

    private const int MaxMessageLength = 10000;
    private readonly List<ChatMessageModel> _messages = [];
    private List<ConversationItem> _conversationItems = [];
    private List<PresetItem> _presetItems = [];
    private List<ChatModelOption> _modelItems = [];
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
    private bool _enableSearch;

    protected override async Task OnInitializedAsync()
    {
        // 初始化预设列表
        _presetItems = Gemini.Presets.Select(p => new PresetItem
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description
        }).ToList();
        _modelItems = Gemini.ModelOptions.ToList();

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
            // 检测是否为移动端视口，如果是，则默认关闭侧边栏以避免挤占或遮挡空间
            try
            {
                var isMobile = await JS.InvokeAsync<bool>("eval", "window.innerWidth < 768");
                if (isMobile)
                {
                    _showSidebar = false;
                    StateHasChanged();
                }
            }
            catch (Exception) { /* 忽略 SSR/预渲染期间 JS 报错 */ }

            // 从 localStorage 恢复思考模式设置
            await RestoreThinkingLevelAsync();
            await RestoreModelAsync();
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

    private async Task RestoreModelAsync()
    {
        try
        {
            var savedModel = await JS.InvokeAsync<string?>("localStorage.getItem", "geminiModel");
            if (!string.IsNullOrWhiteSpace(savedModel))
            {
                Gemini.SetModel(savedModel);
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
            await ConversationSvc.UpdateTokenCountAsync(conversationId, Auth.CurrentUser.Id, Gemini.CurrentTokenCount);
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

        var aiMessage = new ChatMessageModel { IsUser = false, Content = "", IsStreaming = true };
        _messages.Add(aiMessage);

        var request = new ChatSendRequest(
            _currentConversationId,
            Auth.CurrentUser.Id,
            userMessage,
            imagesToSend.Select(ToChatAttachment).ToList(),
            _enableSearch);

        var result = await ChatFlow.SendAsync(request, async update =>
        {
            aiMessage.Content = update.Content;
            aiMessage.ThinkingContent = update.ThinkingContent;
            aiMessage.Citations = update.Citations;
            StateHasChanged();
            await ScrollToBottom();
        });

        _currentConversationId = result.ConversationId;
        aiMessage.Content = result.Succeeded ? result.Content : result.ErrorMessage ?? result.Content;
        aiMessage.ThinkingContent = result.ThinkingContent;
        aiMessage.Citations = result.Citations;
        aiMessage.IsStreaming = false;
        _isLoading = false;

        await LoadConversationsAsync();
        StateHasChanged();

        await Task.Delay(100);
        StateHasChanged();

        if (_chatInputRef is not null)
        {
            await _chatInputRef.FocusAsync();
        }
    }

    private static ChatImageAttachment ToChatAttachment(ImageAttachment image) =>
        new(image.Base64Data, image.MimeType, image.FileName);

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
        catch (Exception) { /* JS 在预渲染阶段不可用，属预期行为 */ }
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

    private async Task SelectModel(string modelName)
    {
        Gemini.SetModel(modelName);
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", "geminiModel", modelName);
        }
        catch (Exception) { /* localStorage 在 SSR/预渲染阶段不可用，属预期行为 */ }

        StateHasChanged();
    }

    private string GetCurrentPresetName() => Gemini.Presets.FirstOrDefault(p => p.Id == Gemini.CurrentPresetId)?.Name ?? "默认助手";

    private async Task ExportConversation(string format)
    {
        if (_currentConversationId == null || _currentConversationId == Guid.Empty)
        {
            // 对于未保存的新对话，暂时不支持导出
            return;
        }

        try
        {
            var url = $"/api/export/{_currentConversationId}/{format}";
            await JS.InvokeVoidAsync("downloadFile", url, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "导出对话失败");
        }
    }

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
