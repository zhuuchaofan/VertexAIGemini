using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
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
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies,
        ChatOrchestrator chat)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" }, JsonOptions);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Message) && request.Images.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Message or image attachment is required." }, JsonOptions);
            return;
        }

        var attachmentError = ChatAttachmentValidator.Validate(request.Images);
        if (attachmentError != null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = attachmentError }, JsonOptions);
            return;
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        var defaultAssistantPrompt = await GetDefaultAssistantPromptAsync(dbFactory, userId.Value);

        var result = await chat.SendAsync(
            new ChatSendRequest(
                request.ConversationId,
                userId.Value,
                request.Message,
                request.Images,
                request.EnableSearch,
                new ChatSessionOptions(
                    request.ProviderId,
                    request.ModelName,
                    request.PresetId,
                    request.CustomPrompt,
                    request.ThinkingEnabled,
                    request.ThinkingLevel,
                    request.ThinkingBudget,
                    defaultAssistantPrompt)),
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

    private static async Task<string?> GetDefaultAssistantPromptAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        Guid userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.DefaultAssistantPrompt)
            .FirstOrDefaultAsync();
    }

    private sealed record ApiChatSendRequest(
        Guid? ConversationId,
        string Message,
        IReadOnlyCollection<ChatImageAttachment> Images,
        bool EnableSearch,
        string? ProviderId,
        string? ModelName,
        string? PresetId,
        string? CustomPrompt,
        bool? ThinkingEnabled,
        string? ThinkingLevel,
        int? ThinkingBudget)
    {
        public IReadOnlyCollection<ChatImageAttachment> Images { get; init; } = Images ?? [];
    }

    private sealed record ApiChatFinalResponse(
        Guid? ConversationId,
        string Content,
        string? ThinkingContent,
        bool Succeeded,
        string? ErrorMessage,
        List<SearchCitation>? Citations);
}
