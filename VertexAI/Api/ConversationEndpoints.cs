using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using VertexAI.Data.Entities;
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
        HttpContext context,
        IUserContext users,
        IConversationStore conversations)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null) return Results.Unauthorized();

        var pageOffset = Math.Max(0, offset ?? 0);
        var pageLimit = Math.Clamp(limit ?? 30, 1, 100);
        var items = await conversations.GetUserConversationsAsync(currentUser, pageOffset, pageLimit + 1);
        var hasMore = items.Count > pageLimit;

        return Results.Ok(new ConversationListResponse(
            items.Take(pageLimit).Select(ToSummary).ToList(),
            pageOffset,
            pageLimit,
            hasMore));
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

    private static async Task<IResult> DeleteAsync(
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
            DeserializeAttachments(message.AttachmentsJson),
            message.CreatedAt);

    private static IReadOnlyList<ChatImageAttachment> DeserializeAttachments(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChatImageAttachment>>(attachmentsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
        bool HasMore);

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
        IReadOnlyList<ChatImageAttachment> Attachments,
        DateTime CreatedAt);
}
