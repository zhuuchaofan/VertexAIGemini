using System.Collections.Concurrent;

namespace VertexAI.Services.Runs;

/// <summary>
/// In-process stop signals, so a stop that lands on the instance running the generation takes effect at once.
/// A stop that lands on another instance travels through the run document and is picked up by polling.
/// </summary>
public sealed class RunCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new();

    public Registration Register(string runId)
    {
        var source = new CancellationTokenSource();
        _sources[runId] = source;
        return new Registration(this, runId, source);
    }

    public bool Cancel(string runId)
    {
        if (!_sources.TryGetValue(runId, out var source)) return false;
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public sealed class Registration : IDisposable
    {
        private readonly RunCancellationRegistry _owner;
        private readonly string _runId;
        private readonly CancellationTokenSource _source;

        internal Registration(RunCancellationRegistry owner, string runId, CancellationTokenSource source)
        {
            _owner = owner;
            _runId = runId;
            _source = source;
        }

        public CancellationToken Token => _source.Token;
        public bool StopRequested => _source.IsCancellationRequested;

        public void Dispose()
        {
            _owner._sources.TryRemove(new KeyValuePair<string, CancellationTokenSource>(_runId, _source));
            _source.Dispose();
        }
    }
}
