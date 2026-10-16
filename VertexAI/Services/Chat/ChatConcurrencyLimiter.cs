using Microsoft.Extensions.Options;
using VertexAI.Configuration;

namespace VertexAI.Services.Chat;

/// <summary>
/// Caps simultaneous streaming chats per user and per instance. It is in-memory, so it bounds one
/// Cloud Run instance only; the cross-instance bound is the Firestore-backed daily quota plus the
/// service's max-instances setting.
/// </summary>
public sealed class ChatConcurrencyLimiter
{
    private readonly int _perUser;
    private readonly int _perInstance;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _byUser = new(StringComparer.Ordinal);
    private int _total;

    public ChatConcurrencyLimiter(IOptions<QuotaSettings> settings)
    {
        _perUser = settings.Value.MaxConcurrentChatsPerUser;
        _perInstance = settings.Value.MaxConcurrentChatsPerInstance;
    }

    /// <summary>Returns a lease to dispose when the chat ends, or null when a limit is reached.</summary>
    public IDisposable? TryEnter(string userKey)
    {
        lock (_gate)
        {
            _byUser.TryGetValue(userKey, out var current);
            if ((_perUser > 0 && current >= _perUser) || (_perInstance > 0 && _total >= _perInstance))
            {
                return null;
            }

            _byUser[userKey] = current + 1;
            _total++;
        }

        return new Lease(this, userKey);
    }

    private void Release(string userKey)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userKey, out var current))
            {
                if (current <= 1)
                {
                    _byUser.Remove(userKey);
                }
                else
                {
                    _byUser[userKey] = current - 1;
                }

                _total = Math.Max(0, _total - 1);
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly ChatConcurrencyLimiter _owner;
        private readonly string _userKey;
        private int _released;

        public Lease(ChatConcurrencyLimiter owner, string userKey)
        {
            _owner = owner;
            _userKey = userKey;
        }

        public void Dispose()
        {
            // Idempotent: a lease may be disposed by both a cancellation path and the normal path.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(_userKey);
            }
        }
    }
}
