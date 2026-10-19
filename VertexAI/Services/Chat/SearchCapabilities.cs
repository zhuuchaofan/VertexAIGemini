namespace VertexAI.Services.Chat;

/// <summary>The client puts the current date in front of the question: the model has no clock of its own.</summary>
public interface ITimeAwareChatModelClient
{
}

/// <summary>
/// The client has no search of its own. When the person asks for the web, the run searches first (see
/// <c>WebSearchService</c>) and hands the findings to the model with the question.
/// </summary>
public interface IExternalSearchChatModelClient : ITimeAwareChatModelClient
{
}
