using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>Events a run emits while it executes. The HTTP layer turns them into server-sent events.</summary>
public abstract record RunEvent
{
    public sealed record Started(RunRecord Run, bool Replayed) : RunEvent;
    public sealed record Delta(int Offset, string? Content, string? Thinking) : RunEvent;
    public sealed record Citations(IReadOnlyList<SearchCitation> Items) : RunEvent;
    public sealed record Done(RunRecord Run) : RunEvent;
    public sealed record End(bool Replayed) : RunEvent;
}
