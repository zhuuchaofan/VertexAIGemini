using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services;

/// <summary>
/// 对话服务 - 管理用户对话和消息的持久化
/// </summary>
public class ConversationService
{
    private readonly AppDbContext _db;

    public ConversationService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 获取用户的所有对话（按更新时间倒序）
    /// </summary>
    public async Task<List<Conversation>> GetUserConversationsAsync(Guid userId)
    {
        return await _db.Conversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <summary>
    /// 获取对话详情（包含消息）
    /// </summary>
    public async Task<Conversation?> GetConversationAsync(Guid conversationId, Guid userId)
    {
        return await _db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);
    }

    /// <summary>
    /// 创建新对话
    /// </summary>
    public async Task<Conversation> CreateConversationAsync(Guid userId, string presetId, string? customPrompt = null)
    {
        var conversation = new Conversation
        {
            UserId = userId,
            PresetId = presetId,
            CustomPrompt = customPrompt
        };

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync();
        return conversation;
    }

    /// <summary>
    /// 更新对话标题
    /// </summary>
    public async Task UpdateTitleAsync(Guid conversationId, string title)
    {
        var conversation = await _db.Conversations.FindAsync(conversationId);
        if (conversation != null)
        {
            conversation.Title = title;
            conversation.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 更新历史摘要
    /// </summary>
    public async Task UpdateSummaryAsync(Guid conversationId, string? summary)
    {
        var conversation = await _db.Conversations.FindAsync(conversationId);
        if (conversation != null)
        {
            conversation.HistorySummary = summary;
            conversation.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 添加消息到对话
    /// </summary>
    public async Task<Message> AddMessageAsync(
        Guid conversationId,
        string role,
        string content,
        string? thinkingContent = null)
    {
        var message = new Message
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent
        };

        _db.Messages.Add(message);

        // 同时更新对话的更新时间
        var conversation = await _db.Conversations.FindAsync(conversationId);
        if (conversation != null)
        {
            conversation.UpdatedAt = DateTime.UtcNow;

            // 如果没有标题，用第一条用户消息生成
            if (string.IsNullOrEmpty(conversation.Title) && role == "user")
            {
                conversation.Title = content.Length > 50 ? content[..50] + "..." : content;
            }
        }

        await _db.SaveChangesAsync();
        return message;
    }

    /// <summary>
    /// 删除对话
    /// </summary>
    public async Task DeleteConversationAsync(Guid conversationId, Guid userId)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);

        if (conversation != null)
        {
            _db.Conversations.Remove(conversation);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 清空对话消息（保留对话本身）
    /// </summary>
    public async Task ClearMessagesAsync(Guid conversationId)
    {
        var messages = await _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync();

        _db.Messages.RemoveRange(messages);

        var conversation = await _db.Conversations.FindAsync(conversationId);
        if (conversation != null)
        {
            conversation.HistorySummary = null;
            conversation.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }
}
