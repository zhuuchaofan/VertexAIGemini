using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
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
    private readonly ChatConcurrencyLimiter? _concurrency;
    private readonly int _maxHistoryMessages;
    private readonly TimeSpan _maxGeneration;

    public ChatOrchestrator(
        IChatProviderCatalog providers,
        IConversationStore conversations,
        ILogger<ChatOrchestrator> logger,
        IOptions<GeminiSettings>? geminiSettings = null,
        IEnumerable<IChatRequestAugmenter>? augmenters = null,
        IChatQuotaService? quota = null,
        ChatConcurrencyLimiter? concurrency = null,
        IOptions<ChatStreamSettings>? streamSettings = null)
    {
        _concurrency = concurrency;
        _maxGeneration = TimeSpan.FromSeconds(
            streamSettings?.Value.MaxGenerationSeconds is > 0 ? streamSettings.Value.MaxGenerationSeconds : 240);
        _providers = providers;
        _conversations = conversations;
        _logger = logger;
        _augmenters = (augmenters ?? [new WebSearchInstructionAugmenter()]).ToArray();
        _quota = quota ?? new NoOpChatQuotaService();
        _maxHistoryMessages = ResolveMaxHistoryMessages(geminiSettings?.Value);
    }

    public async Task<ChatSendResult> SendAsync(
        ChatSendRequest request,
        Func<ChatStreamUpdate, Task> onUpdate,
        CancellationToken cancellationToken = default)
    {
        IChatModelClient? model = null;
        Guid? conversationId = request.ConversationId;
        var responseBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        var allCitations = new List<SearchCitation>();
        var quotaRequest = CreateQuotaRequest(request);
        var quotaReserved = false;
        var streamStarted = false;
        var truncated = false;
        var status = ChatStatuses.Completed;
        string? errorMessage = null;

        using var concurrencyLease = _concurrency?.TryEnter(
            request.User.FirebaseUid ?? request.User.LocalUserId.ToString("D"));
        if (_concurrency is not null && concurrencyLease is null)
        {
            return new ChatSendResult(
                request.ConversationId,
                "",
                null,
                false,
                "当前有多个对话正在生成，请等待完成后再试。",
                Status: ChatStatuses.Failed);
        }

        // One token for everything below: the caller's (client gone / user pressed stop) plus a hard
        // generation cap that stays below the platform request timeout.
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_maxGeneration > TimeSpan.Zero)
        {
            generation.CancelAfter(_maxGeneration);
        }

        var token = generation.Token;

        try
        {
            await _quota.CheckAndReserveAsync(quotaRequest, token);
            quotaReserved = true;

            var providerId = _providers.ResolveProviderId(request.Options?.ProviderId);
            ValidateModelSelection(providerId, request.Options?.ModelName);
            model = _providers.CreateClient(providerId);
            if (model is IAuthenticatedUserAwareChatModelClient userAwareModel)
            {
                userAwareModel.SetAuthenticatedUser(request.User);
            }

            await model.ConfigureAsync(request.Options);

            conversationId = await EnsureConversationAsync(request, providerId, model);
            var message = request.Message.Trim();
            IReadOnlyList<ChatHistoryEntry> history = [];

            if (conversationId.HasValue && request.ConversationId.HasValue)
            {
                history = NormalizeHistory(await _conversations.GetHistoryAsync(
                    conversationId.Value,
                    request.User,
                    _maxHistoryMessages));
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

            streamStarted = true;
            await foreach (var chunk in model.StreamChatAsync(modelRequest, token).WithCancellation(token))
            {
                token.ThrowIfCancellationRequested();

                if (chunk.IsTruncated)
                {
                    truncated = true;
                }

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

                if (chunk.IsTruncated && string.IsNullOrEmpty(chunk.Text))
                {
                    continue;
                }

                await onUpdate(new ChatStreamUpdate(
                    responseBuilder.ToString(),
                    thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null,
                    allCitations.Count > 0 ? allCitations : null,
                    conversationId));
            }

            if (truncated)
            {
                status = ChatStatuses.Truncated;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = ChatStatuses.Cancelled;
            }
            else
            {
                _logger.LogWarning("Chat generation exceeded the {Seconds}s cap", _maxGeneration.TotalSeconds);
                status = ChatStatuses.Failed;
                errorMessage = "生成时间过长已被终止，已保留已生成的内容。";
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // Typically a write to a client that already left; nothing to report to anyone.
            _logger.LogInformation(ex, "Chat request ended after the caller cancelled");
            status = ChatStatuses.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat request failed");
            status = ChatStatuses.Failed;
            errorMessage = ChatErrorMapper.ToUserMessage(ex);
        }
        finally
        {
            // Runs exactly once per request on every path (completed, truncated, cancelled, failed), so a
            // reply is saved at most once and a quota reservation is settled exactly once. Persistence
            // deliberately ignores the cancellation token: the client may already be gone.
            if (conversationId.HasValue && (status == ChatStatuses.Completed || responseBuilder.Length > 0))
            {
                var finalConversationId = conversationId.Value;
                var content = responseBuilder.ToString();
                var thinking = thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null;
                var persistedStatus = status == ChatStatuses.Completed ? null : status;
                await TryAsync(
                    () => _conversations.AddMessageAsync(
                        finalConversationId,
                        request.User,
                        "model",
                        content,
                        thinking,
                        status: persistedStatus),
                    "model message save");
            }

            if (conversationId.HasValue && model != null)
            {
                await TryAsync(
                    () => _conversations.UpdateTokenCountAsync(
                        conversationId.Value,
                        request.User,
                        model.CurrentTokenCount),
                    "token count update");
            }

            if (quotaReserved)
            {
                // Always settle a reservation, even when the model was never reached: the estimate is
                // released and only real usage (0 when nothing ran) stays on the books.
                var actualTokens = EstimateActualTokens(
                    model,
                    quotaRequest,
                    streamStarted,
                    responseBuilder.Length + thinkingBuilder.Length);
                await TryAsync(
                    () => _quota.RecordTokenUsageAsync(quotaRequest, actualTokens),
                    "quota settlement");
            }
        }

        return new ChatSendResult(
            conversationId,
            responseBuilder.ToString(),
            thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null,
            ChatStatuses.IsSuccessful(status),
            errorMessage,
            allCitations.Count > 0 ? allCitations : null,
            status);
    }

    /// <summary>
    /// Tokens to charge once the request ended. A request that never reached the model costs nothing;
    /// otherwise the model's own count is used, with a floor of the pre-flight estimate plus what was
    /// actually produced, so cancelled and failed generations are not free.
    /// </summary>
    private static int EstimateActualTokens(
        IChatModelClient? model,
        ChatQuotaRequest quotaRequest,
        bool streamStarted,
        int producedChars)
    {
        if (!streamStarted)
        {
            return 0;
        }

        var floor = quotaRequest.EstimatedTokens + producedChars / 4;
        return Math.Max(Math.Max(0, model?.CurrentTokenCount ?? 0), floor);
    }

    /// <summary>
    /// Providers expect alternating turns. After a failed or cancelled request the stored history can hold
    /// two user turns in a row (or start with a model turn after truncation); merge/drop those so the next
    /// request is still valid.
    /// </summary>
    internal static IReadOnlyList<ChatHistoryEntry> NormalizeHistory(IReadOnlyList<ChatHistoryEntry> history)
    {
        var result = new List<ChatHistoryEntry>(history.Count);
        foreach (var entry in history)
        {
            if (result.Count == 0 && entry.Role != "user")
            {
                continue;
            }

            if (result.Count > 0 && result[^1].Role == entry.Role)
            {
                var previous = result[^1];
                result[^1] = previous with
                {
                    Content = string.Join("\n\n", new[] { previous.Content, entry.Content }.Where(text => !string.IsNullOrWhiteSpace(text))),
                    ThinkingContent = previous.ThinkingContent ?? entry.ThinkingContent,
                    Attachments = (previous.Attachments ?? []).Concat(entry.Attachments ?? []).ToList()
                };
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Rejects unknown or retired models explicitly. Without this check a stale model id would be
    /// silently replaced by the provider's first model (different capabilities and price).
    /// </summary>
    private void ValidateModelSelection(string providerId, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        var provider = _providers.Providers.FirstOrDefault(p =>
            string.Equals(p.Info.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null || provider.ModelOptions.Count == 0)
        {
            return;
        }

        if (!provider.ModelOptions.Any(option => option.ModelName == modelName))
        {
            throw new UserFacingChatException(
                $"所选模型「{modelName}」已不可用或已下线，请重新选择模型。");
        }
    }

    private static ChatQuotaRequest CreateQuotaRequest(ChatSendRequest request) =>
        new(
            request.User,
            RequestCount: 1,
            EstimatedTokens: QuotaEstimator.EstimateTokens(request.Message, request.Attachments),
            SearchCount: SearchModes.EnablesWebSearch(request.SearchMode) ? 1 : 0,
            AttachmentBytes: request.Attachments.Sum(QuotaEstimator.EstimateAttachmentBytes),
            DateKey: QuotaAccounting.TodayKey());

    private async Task TryAsync(Func<Task> action, string description)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat post-processing failed: {Step}", description);
        }
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
