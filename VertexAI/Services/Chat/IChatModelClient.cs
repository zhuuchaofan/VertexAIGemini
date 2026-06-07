using Google.GenAI.Types;

namespace VertexAI.Services.Chat;

public interface IChatModelClient
{
    int CurrentTokenCount { get; }
    string CurrentPresetId { get; }
    string CurrentCustomPrompt { get; }

    IAsyncEnumerable<ChatChunk> StreamChatAsync(List<Part> userParts);
}
