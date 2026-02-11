using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Data.Entities;
using VertexAI.Services;

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
        IDbContextFactory<AppDbContext> dbFactory,
        AuthService authService)  // 这里的 AuthService 仅用于获取辅助方法，实际认证看 Cookie
    {
        var user = await GetUserFromCookieAsync(httpContext, dbFactory);
        if (user == null) return Results.Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null || conversation.UserId != user.Id)
        {
            return Results.NotFound("对话不存在或无权访问");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {conversation.Title ?? "未命名对话"}");
        sb.AppendLine($"> 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 模型: {conversation.PresetId}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in conversation.Messages.OrderBy(m => m.CreatedAt))
        {
            var roleName = msg.Role == "user" ? "**用户**" : "**Gemini**";
            sb.AppendLine($"{roleName}:");
            sb.AppendLine();
            sb.AppendLine(msg.Content);
            sb.AppendLine();

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
        IDbContextFactory<AppDbContext> dbFactory)
    {
        var user = await GetUserFromCookieAsync(httpContext, dbFactory);
        if (user == null) return Results.Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null || conversation.UserId != user.Id)
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
        var exportData = new
        {
            Metadata = new
            {
                Title = conversation.Title,
                ExportedAt = DateTime.UtcNow,
                TokenCount = conversation.TokenCount,
                PresetId = conversation.PresetId
            },
            Messages = conversation.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                m.Role,
                m.Content,
                m.ThinkingContent,
                m.CreatedAt
            })
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(exportData, options);
        var fileName = SanitizeFileName($"{conversation.Title ?? "conversation"}_{DateTime.Now:yyyyMMdd}.json");

        return Results.File(jsonBytes, "application/json", fileName);
    }

    // 辅助方法：从 Cookie 获取用户（复用 AuthEndpoints 的逻辑或直接查库）
    // 为了保持 Minimal API 的独立性，这里简单重新实现一个基于 Cookie 的查找
    private static async Task<User?> GetUserFromCookieAsync(HttpContext context, IDbContextFactory<AppDbContext> dbFactory)
    {
        if (!context.Request.Cookies.TryGetValue("gemini_auth", out var token)) return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

        return session?.User;
    }

    private static string SanitizeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
}
