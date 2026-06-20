namespace VertexAI.Services.Quota;

public interface IChatQuotaService
{
    Task CheckAndReserveAsync(ChatQuotaRequest request, CancellationToken cancellationToken = default);

    Task RecordTokenUsageAsync(ChatQuotaRequest request, int actualTokens, CancellationToken cancellationToken = default);
}
