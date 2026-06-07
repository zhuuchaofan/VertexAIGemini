using System.Text;
using Microsoft.Extensions.Logging;

namespace VertexAI.Services.Chat;

public class ChatOrchestrator
{
    private readonly IChatModelClient _gemini;
    private readonly IConversationStore _conversations;
    private readonly ILogger<ChatOrchestrator> _logger;

    public ChatOrchestrator(
        IChatModelClient gemini,
        IConversationStore conversations,
        ILogger<ChatOrchestrator> logger)
    {
        _gemini = gemini;
        _conversations = conversations;
        _logger = logger;
    }

    public async Task<ChatSendResult> SendAsync(
        ChatSendRequest request,
        Func<ChatStreamUpdate, Task> onUpdate)
    {
        var conversationId = await EnsureConversationAsync(request);
        var message = request.Message.Trim();

        if (conversationId.HasValue)
        {
            await _conversations.AddMessageAsync(
                conversationId.Value,
                request.UserId,
                "user",
                message);
        }

        var responseBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        var allCitations = new List<SearchCitation>();

        try
        {
            var parts = GeminiPartFactory.CreateParts(message, request.Images);

            await foreach (var chunk in _gemini.StreamChatAsync(parts, request.EnableSearch))
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
                    foreach (var cite in chunk.Citations)
                    {
                        if (!allCitations.Any(c => c.Uri == cite.Uri))
                        {
                            allCitations.Add(cite);
                        }
                    }
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
                    request.UserId,
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
            if (conversationId.HasValue)
            {
                await _conversations.UpdateTokenCountAsync(
                    conversationId.Value,
                    request.UserId,
                    _gemini.CurrentTokenCount);
            }
        }
    }

    private async Task<Guid?> EnsureConversationAsync(ChatSendRequest request)
    {
        if (request.ConversationId.HasValue)
        {
            return request.ConversationId;
        }

        var conversation = await _conversations.CreateConversationAsync(
            request.UserId,
            _gemini.CurrentPresetId,
            _gemini.CurrentCustomPrompt);

        return conversation?.Id;
    }
}
