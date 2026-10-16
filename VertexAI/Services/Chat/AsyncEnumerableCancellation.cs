using System.Runtime.CompilerServices;

namespace VertexAI.Services.Chat;

internal static class AsyncEnumerableCancellation
{
    /// <summary>
    /// Makes cancellation prompt for a source that ignores cancellation tokens (Google.GenAI 0.14.0
    /// ignores <c>WithCancellation</c>; verified experimentally). The wait for the next item is raced
    /// against the token, so the caller regains control immediately even while the model is silent.
    /// The source can only be released between items: when cancelled mid-wait, the enumerator is
    /// disposed as soon as the in-flight read completes, which is what closes the upstream connection.
    /// </summary>
    public static async IAsyncEnumerable<T> WithPromptCancellation<T>(
        this IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = new PromptEnumerator<T>(source.GetAsyncEnumerator(cancellationToken));
        using var cancelled = new CancellationSignal(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = enumerator.MoveNextAsync();
            var finished = await Task.WhenAny(next, cancelled.Task);
            if (finished != next)
            {
                enumerator.Abandon(next);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!await next)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    private sealed class CancellationSignal : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public CancellationSignal(CancellationToken token)
        {
            _registration = token.Register(() => _completion.TrySetResult());
        }

        public Task Task => _completion.Task;

        public void Dispose() => _registration.Dispose();
    }

    private sealed class PromptEnumerator<T> : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<T> _inner;
        private int _abandoned;

        public PromptEnumerator(IAsyncEnumerator<T> inner)
        {
            _inner = inner;
        }

        public T Current => _inner.Current;

        public Task<bool> MoveNextAsync() => _inner.MoveNextAsync().AsTask();

        /// <summary>Disposes the enumerator once the read that is still in flight has finished.</summary>
        public void Abandon(Task inFlight)
        {
            Interlocked.Exchange(ref _abandoned, 1);
            _ = inFlight.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await _inner.DisposeAsync();
                    }
                    catch
                    {
                        // Nothing left to report: the caller already left.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        public async ValueTask DisposeAsync()
        {
            // After Abandon the continuation owns disposal (disposing during a pending read is invalid).
            if (Interlocked.CompareExchange(ref _abandoned, 1, 0) == 0)
            {
                await _inner.DisposeAsync();
            }
        }
    }
}
