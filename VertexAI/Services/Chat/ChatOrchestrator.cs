using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAI.Services.Quota;

namespace VertexAI.Services.Chat;

public class ChatOrchestrator
{
    private const int DefaultMaxHistoryMessages = 40;

    private readonly IChatProviderCatalog _providers;
    private readonly IConversationStore _conversations;
    private readonly ILogger<ChatOrchestrator> _logger;
    private readonly IReadOnlyList<IChatRequestAugmenter> _augmenters;
    private readonly IChatQuotaService _quota;
    private readonly int _maxHistoryMessages;

    public ChatOrchestrator(
        IChatProviderCatalog providers,
        IConversationStore conversations,
        ILogger<ChatOrchestrator> logger,
        IOptions<GeminiSettings>? geminiSettings = null,
        IEnumerable<IChatRequestAugmenter>? augmenters = null,
        IChatQuotaService? quota = null)
    {
        _providers = providers;
        _conversations = conversations;
        _logger = logger;
        _augmenters = (augmenters ?? [new WebSearchInstructionAugmenter()]).ToArray();
        _quota = quota ?? new NoOpChatQuotaService();
        _maxHistoryMessages = ResolveMaxHistoryMessages(geminiSettings?.Value);
    }

    public async Task<ChatSendResult> SendAsync(
        ChatSendRequest request,
        Func<ChatStreamUpdate, Task> onUpdate)
    {
        IChatModelClient? model = null;
        Guid? conversationId = request.ConversationId;
        var responseBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        var allCitations = new List<SearchCitation>();
        var quotaRequest = CreateQuotaRequest(request);

        try
        {
            await _quota.CheckAndReserveAsync(quotaRequest);

            var providerId = _providers.ResolveProviderId(request.Options?.ProviderId);
            model = _providers.CreateClient(providerId);
            await model.ConfigureAsync(request.Options);

            conversationId = await EnsureConversationAsync(request, providerId, model);
            var message = request.Message.Trim();
            IReadOnlyList<ChatHistoryEntry> history = [];

            if (conversationId.HasValue && request.ConversationId.HasValue)
            {
                history = await _conversations.GetHistoryAsync(
                    conversationId.Value,
                    request.User,
                    _maxHistoryMessages);
                await model.LoadHistoryAsync(history);
            }

            if (conversationId.HasValue)
            {
                await _conversations.AddMessageAsync(
                    conversationId.Value,
                    request.User,
                    "user",
                    message,
                    attachments: request.Attachments);
            }

            var augmentation = await AugmentRequestAsync(
                new ChatRequestContext(
                    conversationId,
                    request.User,
                    message,
                    request.Attachments,
                    request.SearchMode,
                    history,
                    request.Options));
            AddCitations(allCitations, augmentation.Citations);

            var modelRequest = new ChatModelRequest(
                augmentation.Message,
                request.Attachments,
                request.SearchMode);

            await foreach (var chunk in model.StreamChatAsync(modelRequest))
            {
                if (chunk.IsThinking)
                {
                    thinkingBuilder.Append(chunk.Text);
                }
                else
                {
                    responseBuilder.Append(chunk.Text);
                }

                if (chunk.Citations != null && chunk.Citations.Count > 0)
                {
                    AddCitations(allCitations, chunk.Citations);
                }

                await onUpdate(new ChatStreamUpdate(
                    responseBuilder.ToString(),
                    thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null,
                    allCitations.Count > 0 ? allCitations : null));
            }

            var response = responseBuilder.ToString();
            var thinking = thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null;

            if (conversationId.HasValue)
            {
                await _conversations.AddMessageAsync(
                    conversationId.Value,
                    request.User,
                    "model",
                    response,
                    thinking);
            }

            return new ChatSendResult(conversationId, response, thinking, true, null, allCitations.Count > 0 ? allCitations : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat request failed");

            return new ChatSendResult(
                conversationId,
                responseBuilder.ToString(),
                thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null,
                false,
                ChatErrorMapper.ToUserMessage(ex),
                allCitations.Count > 0 ? allCitations : null);
        }
        finally
        {
            if (conversationId.HasValue && model != null)
            {
                await _conversations.UpdateTokenCountAsync(
                    conversationId.Value,
                    request.User,
                    model.CurrentTokenCount);
                await _quota.RecordTokenUsageAsync(
                    quotaRequest,
                    Math.Max(0, model.CurrentTokenCount));
            }
        }
    }

    private static ChatQuotaRequest CreateQuotaRequest(ChatSendRequest request) =>
        new(
            request.User,
            RequestCount: 1,
            EstimatedTokens: EstimateTokens(request.Message, request.Attachments),
            SearchCount: SearchModes.EnablesWebSearch(request.SearchMode) ? 1 : 0,
            AttachmentBytes: request.Attachments.Sum(EstimateAttachmentBytes));

    private static int EstimateTokens(string message, IReadOnlyCollection<ChatAttachment> attachments)
    {
        var textChars = message.Length + attachments.Sum(attachment => attachment.Base64Data?.Length ?? 0);
        return Math.Max(1, textChars / 4);
    }

    private static long EstimateAttachmentBytes(ChatAttachment attachment)
    {
        if (attachment.SizeBytes is > 0)
        {
            return attachment.SizeBytes.Value;
        }

        if (string.IsNullOrWhiteSpace(attachment.Base64Data))
        {
            return 0;
        }

        return attachment.Base64Data.Length * 3L / 4L;
    }

    private async Task<Guid?> EnsureConversationAsync(
        ChatSendRequest request,
        string providerId,
        IChatModelClient model)
    {
        if (request.ConversationId.HasValue)
        {
            return request.ConversationId;
        }

        var conversation = await _conversations.CreateConversationAsync(
            request.User,
            providerId,
            model.CurrentModelName,
            model.CurrentPresetId,
            model.CurrentCustomPrompt);

        return conversation?.Id;
    }

    private static int ResolveMaxHistoryMessages(GeminiSettings? settings)
    {
        if (settings?.MaxHistoryRounds is > 0)
        {
            return settings.MaxHistoryRounds * 2;
        }

        return DefaultMaxHistoryMessages;
    }

    private async Task<ChatRequestAugmentation> AugmentRequestAsync(ChatRequestContext context)
    {
        var current = new ChatRequestAugmentation(context.OriginalMessage);
        foreach (var augmenter in _augmenters)
        {
            current = await augmenter.AugmentAsync(context, current);
        }

        return current;
    }

    private static void AddCitations(
        List<SearchCitation> citations,
        IReadOnlyCollection<SearchCitation>? additions)
    {
        if (additions == null || additions.Count == 0)
        {
            return;
        }

        foreach (var cite in additions)
        {
            if (!citations.Any(c => c.Uri == cite.Uri))
            {
                citations.Add(cite);
            }
        }
    }
}
