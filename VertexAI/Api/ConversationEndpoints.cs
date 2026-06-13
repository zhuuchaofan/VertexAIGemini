using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using VertexAI.Data;
using VertexAI.Data.Entities;
using VertexAI.Services;
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
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies,
        ConversationService conversations)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null) return Results.Unauthorized();

        var items = await conversations.GetUserConversationsAsync(userId.Value);
        return Results.Ok(items.Select(ToSummary));
    }

    private static async Task<IResult> GetAsync(
        Guid conversationId,
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies,
        ConversationService conversations)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null) return Results.Unauthorized();

        var conversation = await conversations.GetConversationAsync(conversationId, userId.Value);
        return conversation == null
            ? Results.NotFound(new { error = "Conversation not found." })
            : Results.Ok(ToDetail(conversation));
    }

    private static async Task<IResult> UpdateTitleAsync(
        Guid conversationId,
        ConversationTitleRequest request,
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies,
        ConversationService conversations)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null) return Results.Unauthorized();

        var title = request.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.BadRequest(new { error = "Title is required." });
        }

        await conversations.UpdateTitleAsync(conversationId, userId.Value, title);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        Guid conversationId,
        HttpContext context,
        IDbContextFactory<AppDbContext> dbFactory,
        IAuthCookieService cookies,
        ConversationService conversations)
    {
        var userId = await ApiUserContext.GetCurrentUserIdAsync(context, dbFactory, cookies);
        if (userId == null) return Results.Unauthorized();

        await conversations.DeleteConversationAsync(conversationId, userId.Value);
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
