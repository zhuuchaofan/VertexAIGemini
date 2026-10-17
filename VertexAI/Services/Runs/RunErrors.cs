using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

public static class RunErrors
{
    /// <summary>Turns a provider or infrastructure exception into the run's error code and a message safe to show.</summary>
    public static RunError Classify(Exception exception)
    {
        var message = ChatErrorMapper.ToUserMessage(exception);
        var text = exception.Message;

        if (Contains(text, "blocked by safety"))
            return new RunError(RunReasons.Safety, message, false);
        if (Contains(text, "response was empty"))
            return new RunError(RunReasons.EmptyResponse, message, true);
        if (Contains(text, "quota") || Contains(text, "RESOURCE_EXHAUSTED"))
            return new RunError(RunReasons.Quota, message, false);
        if (exception is TaskCanceledException or TimeoutException)
            return new RunError(RunReasons.Timeout, message, true);
        if (exception is HttpRequestException || Contains(text, "Unavailable") || Contains(text, "ServerError"))
            return new RunError(RunReasons.ProviderError, message, true);

        return new RunError(RunReasons.Internal, message, false);
    }

    private static bool Contains(string value, string text) =>
        value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
