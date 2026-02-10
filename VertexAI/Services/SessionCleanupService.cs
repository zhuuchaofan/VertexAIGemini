using Microsoft.EntityFrameworkCore;
using VertexAI.Data;

namespace VertexAI.Services;

/// <summary>
/// 后台服务 - 定期清理过期 Session，防止数据库表膨胀
/// </summary>
public class SessionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public SessionCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<SessionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session 清理服务已启动，间隔: {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理过期 Session 时发生错误");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task CleanupExpiredSessionsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var cutoff = DateTime.UtcNow;
        var count = await db.Sessions
            .Where(s => s.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync();

        if (count > 0)
        {
            _logger.LogInformation("已清理 {Count} 个过期 Session", count);
        }
    }
}
