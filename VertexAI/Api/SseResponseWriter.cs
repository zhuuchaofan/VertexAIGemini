using System.Text;
using System.Text.Json;

namespace VertexAI.Api;

/// <summary>
/// Serialises writes to a Server-Sent-Events response (events and keep-alive comments can come from
/// different tasks) and turns a broken connection into a cancellation instead of an exception storm.
/// </summary>
internal sealed class SseResponseWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpResponse _response;
    private readonly CancellationTokenSource _abort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _heartbeatStop = new();
    private Task? _heartbeat;
    private int _broken;
    private int _finalWritten;

    /// <param name="abort">Cancelled as soon as a write fails, which tells the generation to stop.</param>
    public SseResponseWriter(HttpResponse response, CancellationTokenSource abort)
    {
        _response = response;
        _abort = abort;
    }

    public bool IsBroken => Volatile.Read(ref _broken) == 1;

    public Task WriteEventAsync<T>(string eventName, T payload) =>
        WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n");

    /// <summary>Writes the terminal event at most once, no matter how many paths try to end the stream.</summary>
    public async Task<bool> TryWriteFinalAsync<T>(T payload)
    {
        if (Interlocked.Exchange(ref _finalWritten, 1) == 1)
        {
            return false;
        }

        await WriteEventAsync("final", payload);
        return !IsBroken;
    }

    public Task WriteCommentAsync(string text) => WriteAsync($": {text}\n\n");

    public void StartHeartbeat(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || _heartbeat is not null)
        {
            return;
        }

        _heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(_heartbeatStop.Token))
                {
                    await WriteCommentAsync("ping");
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped by DisposeAsync.
            }
        });
    }

    private async Task WriteAsync(string text)
    {
        if (IsBroken)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (IsBroken)
            {
                return;
            }

            await _response.WriteAsync(text, Encoding.UTF8);
            await _response.Body.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The client is gone. Stop generating; never throw from here into the model loop.
            if (Interlocked.Exchange(ref _broken, 1) == 0)
            {
                try
                {
                    _abort.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatStop.Cancel();
        if (_heartbeat is not null)
        {
            await _heartbeat;
        }

        _heartbeatStop.Dispose();
        _gate.Dispose();
    }
}
