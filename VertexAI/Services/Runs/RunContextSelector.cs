using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>
/// Chooses which stored messages become model context. Only turns that have a real assistant reply count, so a
/// message whose run failed without producing anything never reaches the model as an unanswered question.
/// </summary>
public static class RunContextSelector
{
    /// <summary>Attachments that reached storage (or are inline). Pending and failed ones were never available to a model.</summary>
    private static IReadOnlyCollection<ChatAttachment>? UsableAttachments(Message message)
    {
        var usable = ChatAttachmentSerializer.Deserialize(message.AttachmentsJson).Where(a => AttachmentStates.IsUsable(a.State)).ToList();
        return usable.Count > 0 ? usable : null;
    }

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
                // A question answered again by a regenerate counts once, with its newest reply. Only replies written
                // by runs count this way; in conversations from before runs existed, a stray extra reply is ignored.
                var answered = turns.Count > 0 && ReferenceEquals(turns[^1].User, pendingUser);
                if (!answered) turns.Add((pendingUser, message));
                else if (message.RunId != null && turns[^1].Reply.RunId != null) turns[^1] = (pendingUser, message);
            }
        }

        // A trailing user message is the one being answered now (or an orphan) and is never context.
        return turns
            .TakeLast(Math.Max(1, maxTurns))
            .SelectMany(turn => new[]
            {
                new ChatHistoryEntry(turn.User.Role, turn.User.Content, null, UsableAttachments(turn.User)),
                new ChatHistoryEntry(turn.Reply.Role, turn.Reply.Content, turn.Reply.ThinkingContent)
            })
            .ToList();
    }
}
