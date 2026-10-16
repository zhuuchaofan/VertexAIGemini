using System.Text.Json;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
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

    internal static async Task StreamChatAsync(
        ApiChatSendRequest request,
        HttpContext context,
        IUserContext users,
        ChatOrchestrator chat,
        IOptions<ChatStreamSettings>? streamSettings = null)
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
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        var settings = streamSettings?.Value ?? new ChatStreamSettings();

        // Disconnects (RequestAborted) and failed writes both cancel the generation through this source.
        using var generationAbort = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        await using var sse = new SseResponseWriter(context.Response, generationAbort);

        // Open the stream immediately and keep it alive while the model is silent (long thinking).
        await sse.WriteCommentAsync("stream-open");
        sse.StartHeartbeat(TimeSpan.FromSeconds(settings.HeartbeatSeconds));

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
            update => sse.WriteEventAsync("update", update),
            generationAbort.Token);

        await sse.TryWriteFinalAsync(new ApiChatFinalResponse(
            result.ConversationId,
            result.Content,
            result.ThinkingContent,
            result.Succeeded,
            result.ErrorMessage,
            result.Citations,
            result.Status));
    }

    internal sealed record ApiChatSendRequest(
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
        List<SearchCitation>? Citations,
        string Status);
}
