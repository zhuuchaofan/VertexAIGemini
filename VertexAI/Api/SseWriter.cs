using System.Text.Json;
using VertexAI.Services.Runs;

namespace VertexAI.Api;

/// <summary>Writes run events as server-sent events. One writer per response; writes are serialized.</summary>
public sealed class SseWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpContext _context;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SseWriter(HttpContext context)
    {
        _context = context;
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream; charset=utf-8";
    }

    public async Task WriteAsync(RunEvent @event)
    {
        switch (@event)
        {
            case RunEvent.Started started:
                await WriteAsync("run", RunSnapshot.From(started.Run, started.Replayed));
                break;
            case RunEvent.Delta delta:
                await WriteAsync("delta", new { offset = delta.Offset, content = delta.Content, thinking = delta.Thinking });
                break;
            case RunEvent.Citations citations:
                await WriteAsync("citations", new { items = citations.Items });
                break;
            case RunEvent.Done done:
                await WriteAsync("done", RunSnapshot.From(done.Run));
                break;
            case RunEvent.End end:
                await WriteAsync("end", new { replayed = end.Replayed });
                break;
        }
    }

    public async Task PingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _context.Response.WriteAsync(": ping\n\n");
            await _context.Response.Body.FlushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync<T>(string name, T payload)
    {
        await _gate.WaitAsync();
        try
        {
            await _context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n");
            await _context.Response.Body.FlushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>What clients see of a run. Lease, fingerprint and checkpoint bookkeeping stay server side.</summary>
public sealed record RunSnapshot(
    string RunId,
    Guid ConversationId,
    Guid UserMessageId,
    Guid AssistantMessageId,
    string Status,
    string? StatusReason,
    int Attempt,
    RunError? Error,
    int OutputChars,
    bool HasAssistantMessage,
    bool ProviderCallStarted,
    DateTime? AdmittedAt,
    DateTime? EndedAt,
    bool? Replayed = null,
    RunRequested? Requested = null,
    RunEffective? Effective = null,
    RunUsage? Usage = null,
    string? FinishReason = null,
    string? ModelVersion = null,
    RunProviderCalls? ProviderCalls = null,
    RunTiming? Timing = null,
    RunOutput? Output = null,
    string? RetryOfRunId = null,
    string? RetriedByRunId = null,
    string? ResponseId = null,
    RunSearch? Search = null)
{
    public static RunSnapshot From(RunRecord run, bool? replayed = null) => new(
        run.RunId, run.ConversationId, run.UserMessageId, run.AssistantMessageId,
        run.Status.ToWire(), run.StatusReason, run.Attempt, run.Error, run.OutputChars,
        run.HasAssistantMessage, run.ProviderCallStarted, run.AdmittedAt, run.EndedAt, replayed,
        run.Requested, run.Effective, run.Usage, run.FinishReason, run.ModelVersion, run.ProviderCalls,
        new RunTiming(run.CreatedAt, run.ProviderCallStartedAt, run.FirstOutputAt, run.EndedAt),
        run.Status.IsTerminal() ? new RunOutput(run.OutputChars, run.ThinkingChars, run.CitationCount) : null,
        run.RetryOfRunId, run.RetriedByRunId, run.ResponseId, run.Search);
}

/// <summary>Server-side times. Browser-side durations are measured in the browser and never stored here.</summary>
public sealed record RunTiming(DateTime? CreatedAt, DateTime? StartedAt, DateTime? FirstOutputAt, DateTime? EndedAt);

public sealed record RunOutput(int Chars, int ThinkingChars, int Citations);
