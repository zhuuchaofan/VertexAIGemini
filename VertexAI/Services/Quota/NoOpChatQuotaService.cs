namespace VertexAI.Services.Quota;

public sealed class NoOpChatQuotaService : IChatQuotaService
{
    public Task CheckAndReserveAsync(ChatQuotaRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RecordTokenUsageAsync(ChatQuotaRequest request, int actualTokens, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
