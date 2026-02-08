using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services;

/// <summary>
/// 对话服务 - 管理用户对话和消息的持久化
/// 使用 IDbContextFactory 避免 Blazor Server 中的并发问题
/// </summary>
public class ConversationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<ConversationService> _logger;
    private bool _dbAvailable = true;

    public ConversationService(IDbContextFactory<AppDbContext> dbFactory, ILogger<ConversationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// 获取用户的所有对话（按更新时间倒序）
    /// </summary>
    public async Task<List<Conversation>> GetUserConversationsAsync(Guid userId)
    {
        if (!_dbAvailable) return [];

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Conversations
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.UpdatedAt)
                .AsNoTracking()
                .ToListAsync();
        }
        catch
        {
            _dbAvailable = false;
            return [];
        }
    }

    /// <summary>
    /// 获取对话详情（包含消息）
    /// </summary>
    public async Task<Conversation?> GetConversationAsync(Guid conversationId, Guid userId)
    {
        if (!_dbAvailable) return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Conversations
                .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);
        }
        catch
        {
            _dbAvailable = false;
            return null;
        }
    }

    /// <summary>
    /// 创建新对话
    /// </summary>
    public async Task<Conversation?> CreateConversationAsync(Guid userId, string presetId, string? customPrompt = null)
    {
        if (!_dbAvailable) return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var conversation = new Conversation
            {
                UserId = userId,
                PresetId = presetId,
                CustomPrompt = customPrompt
            };

            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();

            _logger.LogInformation("创建对话, ConversationId={ConversationId}, UserId={UserId}, PresetId={PresetId}",
                conversation.Id, userId, presetId);

            return conversation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建对话失败, UserId={UserId}", userId);
            _dbAvailable = false;
            return null;
        }
    }

    /// <summary>
    /// 更新对话标题
    /// </summary>
    public async Task UpdateTitleAsync(Guid conversationId, string title)
    {
        if (!_dbAvailable) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var conversation = await db.Conversations.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.Title = title;
                conversation.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch
        {
            _dbAvailable = false;
        }
    }

    /// <summary>
    /// 添加消息到对话
    /// </summary>
    public async Task<Message?> AddMessageAsync(
        Guid conversationId,
        string role,
        string content,
        string? thinkingContent = null)
    {
        if (!_dbAvailable) return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var message = new Message
            {
                ConversationId = conversationId,
                Role = role,
                Content = content,
                ThinkingContent = thinkingContent
            };

            db.Messages.Add(message);

            var conversation = await db.Conversations.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.UpdatedAt = DateTime.UtcNow;

                if (string.IsNullOrEmpty(conversation.Title) && role == "user")
                {
                    conversation.Title = content.Length > 50 ? content[..50] + "..." : content;
                }
            }

            await db.SaveChangesAsync();
            return message;
        }
        catch
        {
            _dbAvailable = false;
            return null;
        }
    }

    /// <summary>
    /// 删除对话
    /// </summary>
    public async Task DeleteConversationAsync(Guid conversationId, Guid userId)
    {
        if (!_dbAvailable) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);

            if (conversation != null)
            {
                db.Conversations.Remove(conversation);
                await db.SaveChangesAsync();

                _logger.LogInformation("删除对话, ConversationId={ConversationId}, UserId={UserId}",
                    conversationId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除对话失败, ConversationId={ConversationId}", conversationId);
            _dbAvailable = false;
        }
    }

    /// <summary>
    /// 清空对话消息
    /// </summary>
    public async Task ClearMessagesAsync(Guid conversationId)
    {
        if (!_dbAvailable) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var messages = await db.Messages
                .Where(m => m.ConversationId == conversationId)
                .ToListAsync();

            db.Messages.RemoveRange(messages);

            var conversation = await db.Conversations.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.HistorySummary = null;
                conversation.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }
        catch
        {
            _dbAvailable = false;
        }
    }

    /// <summary>
    /// 获取对话的 Token 计数
    /// </summary>
    public async Task<int> GetTokenCountAsync(Guid conversationId)
    {
        if (!_dbAvailable) return 0;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var conversation = await db.Conversations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == conversationId);
            return conversation?.TokenCount ?? 0;
        }
        catch
        {
            _dbAvailable = false;
            return 0;
        }
    }

    /// <summary>
    /// 更新对话的 Token 计数
    /// </summary>
    public async Task UpdateTokenCountAsync(Guid conversationId, int tokenCount)
    {
        if (!_dbAvailable) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var conversation = await db.Conversations.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.TokenCount = tokenCount;
                conversation.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch
        {
            _dbAvailable = false;
        }
    }
}

