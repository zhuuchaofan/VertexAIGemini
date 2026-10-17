using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Quota;

namespace VertexAI.Services.Runs;

public enum RunStatus { Running, Completed, Stopped, Interrupted, Failed }

public static class RunStatuses
{
    public static string ToWire(this RunStatus status) => status switch
    {
        RunStatus.Running => "running",
        RunStatus.Completed => "completed",
        RunStatus.Stopped => "stopped",
        RunStatus.Interrupted => "interrupted",
        RunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static RunStatus Parse(string? value) => value switch
    {
        "completed" => RunStatus.Completed,
        "stopped" => RunStatus.Stopped,
        "interrupted" => RunStatus.Interrupted,
        "failed" => RunStatus.Failed,
        _ => RunStatus.Running
    };

    public static bool IsTerminal(this RunStatus status) => status != RunStatus.Running;
}

public static class RunReasons
{
    public const string User = "user";
    public const string Connection = "connection";
    public const string ServerLost = "server_lost";
    public const string Quota = "quota";
    public const string Safety = "safety";
    public const string ProviderError = "provider_error";
    public const string EmptyResponse = "empty_response";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
    public const string Storage = "storage";
}

public sealed record RunError(string Code, string Message, bool Retryable);

/// <summary>What the browser asked for, normalized. Stored as the run's immutable request snapshot.</summary>
public sealed record RunRequested(
    string ProviderId,
    string ModelName,
    string PresetId,
    string? CustomPrompt,
    string SearchMode,
    bool? ThinkingEnabled,
    string? ThinkingLevel,
    int? ThinkingBudget,
    ChatAdvancedOptions? Advanced);

/// <summary>
/// Tokens as far as they are known. <c>Source</c> is "provider" when the provider reported them and "estimate" when
/// they are our own count (characters divided by four); a figure we do not have stays null.
/// </summary>
public sealed record RunUsage(int? InputTokens, int? OutputTokens, int? ThoughtsTokens, int? TotalTokens, string Source);

/// <summary>What was actually applied to the request, as resolved on the server. Written before the provider is called.</summary>
public sealed record RunEffective(
    string ProviderId,
    string ModelName,
    string PresetId,
    string SearchMode,
    bool? ThinkingEnabled,
    string? ThinkingLevel,
    int? ThinkingBudget,
    double? Temperature,
    double? TopP,
    int? MaxOutputTokens,
    string? SafetyThreshold);

/// <summary>Our own count of provider requests a run made, by kind.</summary>
public sealed record RunProviderCalls(int Generate, int Summarize, int CountTokens);

/// <summary>What a run learned while it ran. Everything here is optional: unknown stays unknown.</summary>
public sealed record RunMeta(
    RunUsage? Usage,
    string? FinishReason,
    string? ModelVersion,
    string? ResponseId,
    RunProviderCalls? ProviderCalls,
    int ThinkingChars,
    int CitationCount,
    DateTime? FirstOutputAt);

/// <summary>A persisted run. Lease and partial-output fields are internal bookkeeping and never sent to clients.</summary>
public sealed record RunRecord
{
    public required string RunId { get; init; }
    public required string Uid { get; init; }
    public required Guid ConversationId { get; init; }
    public required Guid UserMessageId { get; init; }
    public required Guid AssistantMessageId { get; init; }
    public int Attempt { get; init; } = 1;
    public string Trigger { get; init; } = "send";
    public RunStatus Status { get; init; }
    public string? StatusReason { get; init; }
    public string Fingerprint { get; init; } = "";
    public bool ProviderCallStarted { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTime? AdmittedAt { get; init; }
    public DateTime? HeartbeatAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public DateTime? StopRequestedAt { get; init; }
    public RunError? Error { get; init; }
    public int OutputChars { get; init; }
    public bool HasAssistantMessage { get; init; }
    public string? PartialContent { get; init; }
    public string? PartialThinking { get; init; }
    public ChatQuotaRequest? QuotaReservation { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? ProviderCallStartedAt { get; init; }
    public RunRequested? Requested { get; init; }
    public RunEffective? Effective { get; init; }
    public RunUsage? Usage { get; init; }
    public string? FinishReason { get; init; }
    public string? ModelVersion { get; init; }
    public string? ResponseId { get; init; }
    public RunProviderCalls? ProviderCalls { get; init; }
    public int ThinkingChars { get; init; }
    public int CitationCount { get; init; }
    public DateTime? FirstOutputAt { get; init; }
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = [];

    /// <summary>The failed run this one tries again, and the run that tried this one again. Both only for retries.</summary>
    public string? RetryOfRunId { get; init; }
    public string? RetriedByRunId { get; init; }
}

public sealed record AdmitRequest(
    AuthenticatedUser User,
    string RunId,
    Guid ConversationId,
    string Message,
    RunRequested Requested,
    string ModelName,
    string PresetId,
    string? CustomPrompt,
    string ExecutorId,
    string Fingerprint,
    ChatQuotaRequest Quota,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    string? RetryOfRunId = null);

/// <summary>
/// The only proof that a request may call the model. It can only be produced by <see cref="IRunStore.AdmitAsync"/>
/// (its constructor is internal to the store implementation), and the model-calling code accepts nothing else.
/// </summary>
public sealed class AdmittedRun
{
    internal AdmittedRun(AuthenticatedUser user, RunRecord record, string executorId)
    {
        User = user;
        Record = record;
        ExecutorId = executorId;
    }

    public AuthenticatedUser User { get; }
    public RunRecord Record { get; }
    public string ExecutorId { get; }
    public string RunId => Record.RunId;
    public Guid ConversationId => Record.ConversationId;
}

public abstract record AdmitResult
{
    public sealed record Admitted(AdmittedRun Run) : AdmitResult;
    /// <summary>The run already exists; nothing is executed.</summary>
    public sealed record Replay(RunRecord Run) : AdmitResult;
    /// <summary>Same run id with a different request, or an id owned by someone else. Never leaks which.</summary>
    public sealed record Conflict(string Code) : AdmitResult;
    public sealed record Busy(string ActiveRunId) : AdmitResult;
    /// <summary>Quota refused the request. A failed run and the user message were stored; the model was not called.</summary>
    public sealed record Rejected(RunRecord Run) : AdmitResult;
    /// <summary>A retry that the state of its source does not allow. Nothing was written.</summary>
    public sealed record Refused(string Code) : AdmitResult;
    /// <summary>The conversation's active run lost its lease; reconcile it, then admit again.</summary>
    public sealed record StaleActive(string RunId) : AdmitResult;
}

public sealed record RunLeaseState(RunStatus Status, bool OwnedByCaller, bool StopRequested);

public sealed record RunCheckpoint(string Content, string? Thinking);

public enum FinalizeOrigin { Executor, Reconciler }

public sealed record FinalizeRequest(
    AuthenticatedUser User,
    string RunId,
    FinalizeOrigin Origin,
    string? ExecutorId,
    RunStatus Status,
    string Reason,
    string? Content,
    string? Thinking,
    RunError? Error,
    int TokenCount,
    IReadOnlyList<SearchCitation>? Citations = null,
    RunMeta? Meta = null);

public sealed record FinalizeResult(bool Won, RunRecord Record);
