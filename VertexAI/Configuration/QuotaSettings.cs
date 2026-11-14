namespace VertexAI.Configuration;

public sealed class QuotaSettings
{
    public bool Enabled { get; set; } = true;
    public int DailyRequestLimit { get; set; } = 100;
    public int DailyTokenLimit { get; set; } = 200000;
    public int DailySearchLimit { get; set; } = 30;
    public long DailyAttachmentBytesLimit { get; set; } = 25 * 1024 * 1024;
    /// <summary>Pictures from the picture model per day. Each is far dearer than a chat reply.</summary>
    public int DailyImageLimit { get; set; } = 20;
    public bool AdminBypassEnabled { get; set; } = true;
}
