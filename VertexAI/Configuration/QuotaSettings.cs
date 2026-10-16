namespace VertexAI.Configuration;

public sealed class QuotaSettings
{
    public bool Enabled { get; set; } = true;
    public int DailyRequestLimit { get; set; } = 100;
    public int DailyTokenLimit { get; set; } = 200000;
    public int DailySearchLimit { get; set; } = 30;
    public long DailyAttachmentBytesLimit { get; set; } = 25 * 1024 * 1024;
    public bool AdminBypassEnabled { get; set; } = true;

    /// <summary>
    /// Site-wide daily caps shared by every user and every Cloud Run instance (0 = unlimited).
    /// They bound the worst case of open registration: N accounts can never exceed these totals.
    /// </summary>
    public int GlobalDailyRequestLimit { get; set; } = 500;
    public int GlobalDailyTokenLimit { get; set; } = 2_000_000;
    public int GlobalDailySearchLimit { get; set; } = 150;

    /// <summary>Concurrent streaming chats allowed per user on one instance (0 = unlimited).</summary>
    public int MaxConcurrentChatsPerUser { get; set; } = 2;

    /// <summary>Concurrent streaming chats allowed on one instance (0 = unlimited).</summary>
    public int MaxConcurrentChatsPerInstance { get; set; } = 12;

    public bool GlobalLimitsEnabled =>
        GlobalDailyRequestLimit > 0 || GlobalDailyTokenLimit > 0 || GlobalDailySearchLimit > 0;
}
