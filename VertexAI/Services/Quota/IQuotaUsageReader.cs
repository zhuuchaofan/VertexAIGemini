namespace VertexAI.Services.Quota;

public interface IQuotaUsageReader
{
    Task<QuotaUsageReport> GetDailyUsageAsync(
        string date,
        int limit,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
