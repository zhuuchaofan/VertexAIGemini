using System.Text.Json;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Api;

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/chat");

        group.MapPost("/stream", StreamChatAsync);
    }

    private static async Task StreamChatAsync(
        ApiChatSendRequest request,
        HttpContext context,
        IUserContext users,
        ChatOrchestrator chat)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" }, JsonOptions);
            return;
        }

        var attachments = request.Attachments.Count > 0 ? request.Attachments : request.Images;

        if (string.IsNullOrWhiteSpace(request.Message) && attachments.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Message or attachment is required." }, JsonOptions);
            return;
        }

        var attachmentError = ChatAttachmentValidator.Validate(attachments);
        if (attachmentError != null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = attachmentError }, JsonOptions);
            return;
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        var searchMode = SearchModes.Normalize(request.SearchMode, request.EnableSearch);

        var result = await chat.SendAsync(
            new ChatSendRequest(
                request.ConversationId,
                currentUser,
                request.Message,
                attachments,
                searchMode,
                new ChatSessionOptions(
                    request.ProviderId,
                    request.ModelName,
                    request.PresetId,
                    request.CustomPrompt,
                    request.ThinkingEnabled,
                    request.ThinkingLevel,
                    request.ThinkingBudget)),
            update => WriteEventAsync(context, "update", update));

        await WriteEventAsync(context, "final", new ApiChatFinalResponse(
            result.ConversationId,
            result.Content,
            result.ThinkingContent,
            result.Succeeded,
            result.ErrorMessage,
            result.Citations));
    }

    private static async Task WriteEventAsync<T>(HttpContext context, string eventName, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync($"event: {eventName}\n");
        await context.Response.WriteAsync($"data: {json}\n\n");
        await context.Response.Body.FlushAsync();
    }

    private sealed record ApiChatSendRequest(
        Guid? ConversationId,
        string Message,
        IReadOnlyCollection<ChatAttachment> Attachments,
        IReadOnlyCollection<ChatAttachment> Images,
        string? SearchMode,
        bool? EnableSearch,
        string? ProviderId,
        string? ModelName,
        string? PresetId,
        string? CustomPrompt,
        bool? ThinkingEnabled,
        string? ThinkingLevel,
        int? ThinkingBudget)
    {
        public IReadOnlyCollection<ChatAttachment> Attachments { get; init; } = Attachments ?? [];
        public IReadOnlyCollection<ChatAttachment> Images { get; init; } = Images ?? [];
    }

    private sealed record ApiChatFinalResponse(
        Guid? ConversationId,
        string Content,
        string? ThinkingContent,
        bool Succeeded,
        string? ErrorMessage,
        List<SearchCitation>? Citations);
}
