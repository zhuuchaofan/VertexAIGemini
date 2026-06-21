using Microsoft.AspNetCore.Mvc;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Api;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapGet("/", ListAsync);
        group.MapGet("/{conversationId:guid}", GetAsync);
        group.MapPatch("/{conversationId:guid}/title", UpdateTitleAsync);
        group.MapDelete("/{conversationId:guid}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        HttpContext context,
        IUserContext users,
        IConversationStore conversations)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        var pageOffset = Math.Max(0, offset ?? 0);
        var pageLimit = Math.Clamp(limit ?? 30, 1, 100);
        var items = offset is > 0
            ? await conversations.GetUserConversationsAsync(currentUser, pageOffset, pageLimit + 1)
            : await conversations.GetUserConversationsPageAsync(currentUser, cursor, pageLimit + 1);
        var hasMore = items.Count > pageLimit;
        var pageItems = items.Take(pageLimit).ToList();
        var nextCursor = hasMore
            ? pageItems.LastOrDefault()?.UpdatedAt.ToUniversalTime().ToString("O")
            : null;

        return Results.Ok(new ConversationListResponse(
            pageItems.Select(ToSummary).ToList(),
            pageOffset,
            pageLimit,
            hasMore,
            nextCursor));
    }

    private static async Task<IResult> GetAsync(
        Guid conversationId,
        HttpContext context,
        IUserContext users,
        IConversationStore conversations)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        var conversation = await conversations.GetConversationAsync(conversationId, currentUser);
        return conversation == null
            ? Results.NotFound(new { error = "Conversation not found." })
            : Results.Ok(ToDetail(conversation));
    }

    private static async Task<IResult> UpdateTitleAsync(
        Guid conversationId,
        ConversationTitleRequest request,
        HttpContext context,
        IUserContext users,
        IConversationStore conversations)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        var title = request.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.BadRequest(new { error = "Title is required." });
        }

        await conversations.UpdateTitleAsync(conversationId, currentUser, title);
        return Results.NoContent();
    }

    internal static async Task<IResult> DeleteAsync(
        Guid conversationId,
        HttpContext context,
        IUserContext users,
        IConversationStore conversations)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        await conversations.DeleteConversationAsync(conversationId, currentUser);
        return Results.NoContent();
    }

    private static ConversationSummaryResponse ToSummary(Conversation conversation) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.ProviderId,
            conversation.ModelName,
            conversation.PresetId,
            conversation.TokenCount,
            conversation.CreatedAt,
            conversation.UpdatedAt);

    private static ConversationDetailResponse ToDetail(Conversation conversation) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.ProviderId,
            conversation.ModelName,
            conversation.PresetId,
            conversation.CustomPrompt,
            conversation.TokenCount,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(ToMessage)
                .ToList());

    private static ConversationMessageResponse ToMessage(Message message) =>
        new(
            message.Id,
            message.Role,
            message.Content,
            message.ThinkingContent,
            ChatAttachmentSerializer.Deserialize(message.AttachmentsJson),
            message.CreatedAt);

    private sealed record ConversationTitleRequest(string Title);

    private sealed record ConversationSummaryResponse(
        Guid Id,
        string? Title,
        string ProviderId,
        string ModelName,
        string PresetId,
        int TokenCount,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    private sealed record ConversationListResponse(
        IReadOnlyList<ConversationSummaryResponse> Items,
        int Offset,
        int Limit,
        bool HasMore,
        string? NextCursor);

    private sealed record ConversationDetailResponse(
        Guid Id,
        string? Title,
        string ProviderId,
        string ModelName,
        string PresetId,
        string? CustomPrompt,
        int TokenCount,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        IReadOnlyList<ConversationMessageResponse> Messages);

    private sealed record ConversationMessageResponse(
        Guid Id,
        string Role,
        string Content,
        string? ThinkingContent,
        IReadOnlyList<ChatAttachment> Attachments,
        DateTime CreatedAt);
}
