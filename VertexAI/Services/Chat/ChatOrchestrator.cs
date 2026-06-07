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

        try
        {
            var parts = GeminiPartFactory.CreateParts(message, request.Images);

            await foreach (var chunk in _gemini.StreamChatAsync(parts))
            {
                if (chunk.IsThinking)
                {
                    thinkingBuilder.Append(chunk.Text);
                }
                else
                {
                    responseBuilder.Append(chunk.Text);
                }

                await onUpdate(new ChatStreamUpdate(
                    responseBuilder.ToString(),
                    thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null));
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

            return new ChatSendResult(conversationId, response, thinking, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat request failed");

            return new ChatSendResult(
                conversationId,
                responseBuilder.ToString(),
                thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null,
                false,
                ChatErrorMapper.ToUserMessage(ex));
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
