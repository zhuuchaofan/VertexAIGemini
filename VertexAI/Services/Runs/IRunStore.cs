using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>
/// Persistence of runs. Every method that decides who may do what (admission, provider-call marker, heartbeat,
/// finalization) is a single compare-and-set transaction, so concurrent callers cannot both win.
/// </summary>
public interface IRunStore
{
    /// <summary>
    /// One transaction decides: replay, conflict, busy, quota rejection, or admission. Throws
    /// <see cref="RunAdmissionUnavailableException"/> when the outcome could not be determined even after reading
    /// back; the caller may retry with the same run id.
    /// </summary>
    Task<AdmitResult> AdmitAsync(AdmitRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The execution-right check. True only for the lease holder of a still-running run whose provider call has not
    /// started, and only once. Nothing may talk to the provider before this returns true.
    /// </summary>
    Task<bool> BeginProviderCallAsync(AdmittedRun run, CancellationToken cancellationToken);

    Task<RunLeaseState> PollAsync(AdmittedRun run, CancellationToken cancellationToken);

    /// <summary>Renews the lease and optionally stores a checkpoint. False means the lease is lost.</summary>
    Task<bool> HeartbeatAsync(AdmittedRun run, RunCheckpoint? checkpoint, CancellationToken cancellationToken);

    Task<RunRecord?> RequestStopAsync(AuthenticatedUser user, string runId, CancellationToken cancellationToken);
    Task<RunRecord?> GetAsync(AuthenticatedUser user, string runId, CancellationToken cancellationToken);
    Task<FinalizeResult> FinalizeAsync(FinalizeRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunRecord>> ListAsync(AuthenticatedUser user, Guid conversationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        AuthenticatedUser user, Guid conversationId, int take, CancellationToken cancellationToken);
}

public sealed class RunAdmissionUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
