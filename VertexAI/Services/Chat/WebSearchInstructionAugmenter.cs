namespace VertexAI.Services.Chat;

public sealed class WebSearchInstructionAugmenter : IChatRequestAugmenter
{
    public Task<ChatRequestAugmentation> AugmentAsync(
        ChatRequestContext context,
        ChatRequestAugmentation current,
        CancellationToken cancellationToken = default)
    {
        if (!SearchModes.RequiresWebSearch(context.SearchMode))
        {
            return Task.FromResult(current);
        }

        const string instruction = "请先联网查证最新信息，并在回答中优先引用可验证来源。";
        var message = string.IsNullOrWhiteSpace(current.Message)
            ? instruction
            : $"{instruction}\n\n用户问题：{current.Message}";

        return Task.FromResult(current with { Message = message });
    }
}
