using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>
/// Chooses which stored messages become model context. Only turns that have a real assistant reply count, so a
/// message whose run failed without producing anything never reaches the model as an unanswered question.
/// </summary>
public static class RunContextSelector
{
    public static IReadOnlyList<ChatHistoryEntry> Select(IReadOnlyList<Message> messagesOldestFirst, int maxTurns)
    {
        var turns = new List<(Message User, Message Reply)>();
        Message? pendingUser = null;

        foreach (var message in messagesOldestFirst)
        {
            if (message.Role == "user")
            {
                // A user message that is followed by another user message never got a reply: drop it.
                pendingUser = message;
                continue;
            }

            if (pendingUser != null && !string.IsNullOrWhiteSpace(message.Content))
            {
                turns.Add((pendingUser, message));
            }

            pendingUser = null;
        }

        // A trailing user message is the one being answered now (or an orphan) and is never context.
        return turns
            .TakeLast(Math.Max(1, maxTurns))
            .SelectMany(turn => new[]
            {
                new ChatHistoryEntry(turn.User.Role, turn.User.Content),
                new ChatHistoryEntry(turn.Reply.Role, turn.Reply.Content, turn.Reply.ThinkingContent)
            })
            .ToList();
    }
}
