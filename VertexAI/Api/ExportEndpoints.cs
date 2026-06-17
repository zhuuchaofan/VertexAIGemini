using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Api;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/export");

        group.MapGet("/{conversationId:guid}/markdown", ExportMarkdownAsync);
        group.MapGet("/{conversationId:guid}/json", ExportJsonAsync);
    }

    private static async Task<IResult> ExportMarkdownAsync(
        Guid conversationId,
        HttpContext httpContext,
        IConversationStore conversations,
        IUserContext users)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(httpContext, users);
        if (currentUser == null) return Results.Unauthorized();

        var conversation = await conversations.GetConversationAsync(conversationId, currentUser);
        if (conversation == null)
        {
            return Results.NotFound("对话不存在或无权访问");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {conversation.Title ?? "未命名对话"}");
        sb.AppendLine($"> 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 提供商: {conversation.ProviderId} | 模型: {conversation.ModelName} | 预设: {conversation.PresetId}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in conversation.Messages.OrderBy(m => m.CreatedAt))
        {
            var roleName = msg.Role == "user" ? "**用户**" : "**助手**";
            var attachments = DeserializeAttachments(msg.AttachmentsJson);
            sb.AppendLine($"{roleName}:");
            sb.AppendLine();
            sb.AppendLine(msg.Content);
            sb.AppendLine();

            if (attachments.Count > 0)
            {
                sb.AppendLine($"> 附件: {attachments.Count} 张图片");
                foreach (var attachment in attachments)
                {
                    sb.AppendLine($"> - {attachment.FileName} ({attachment.MimeType})");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(msg.ThinkingContent))
            {
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>思考过程</summary>");
                sb.AppendLine();
                sb.AppendLine(msg.ThinkingContent);
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        var fileName = SanitizeFileName($"{conversation.Title ?? "conversation"}_{DateTime.Now:yyyyMMdd}.md");
        var fileContent = Encoding.UTF8.GetBytes(sb.ToString());

        return Results.File(fileContent, "text/markdown", fileName);
    }

    private static async Task<IResult> ExportJsonAsync(
        Guid conversationId,
        HttpContext httpContext,
        IConversationStore conversations,
        IUserContext users)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(httpContext, users);
        if (currentUser == null) return Results.Unauthorized();

        var conversation = await conversations.GetConversationAsync(conversationId, currentUser);
        if (conversation == null)
        {
            return Results.NotFound("对话不存在或无权访问");
        }

        // 使用 System.Text.Json 进行序列化，并在导出时格式化
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // 构造一个导出对象，包含元数据
        var exportData = new ExportConversationResponse(
            new ExportMetadata(
                conversation.Title,
                DateTime.UtcNow,
                conversation.TokenCount,
                conversation.ProviderId,
                conversation.ModelName,
                conversation.PresetId),
            conversation.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(m => new ExportMessage(
                    m.Role,
                    m.Content,
                    m.ThinkingContent,
                    DeserializeAttachments(m.AttachmentsJson),
                    m.CreatedAt))
                .ToList());

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(exportData, options);
        var fileName = SanitizeFileName($"{conversation.Title ?? "conversation"}_{DateTime.Now:yyyyMMdd}.json");

        return Results.File(jsonBytes, "application/json", fileName);
    }

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

    private static string SanitizeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }

    private sealed record ExportConversationResponse(
        ExportMetadata Metadata,
        IReadOnlyList<ExportMessage> Messages);

    private sealed record ExportMetadata(
        string? Title,
        DateTime ExportedAt,
        int TokenCount,
        string ProviderId,
        string ModelName,
        string PresetId);

    private sealed record ExportMessage(
        string Role,
        string Content,
        string? ThinkingContent,
        IReadOnlyList<ChatImageAttachment> Attachments,
        DateTime CreatedAt);
}
