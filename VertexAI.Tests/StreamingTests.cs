using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VertexAI.Api;
using VertexAI.Configuration;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Quota;

internal static class StreamingTests
{
    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Run(Task task) => task.GetAwaiter().GetResult();

    private static AuthenticatedUser User() => new(Guid.NewGuid(), "uid-stream", "u@example.com");

    private static ChatOrchestrator NewOrchestrator(
        FakeChatModelClient model,
        FakeConversationStore store,
        FakeQuotaService? quota = null,
        int maxGenerationSeconds = 240) =>
        new(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance,
            quota: quota,
            streamSettings: Options.Create(new ChatStreamSettings { MaxGenerationSeconds = maxGenerationSeconds }));

    private static List<ChatChunk> Chunks(params string[] texts) =>
        texts.Select(text => new ChatChunk { Text = text }).ToList();

    // ---- orchestrator: terminal states ---------------------------------------------------

    public static void CompletedStreamIsSavedOnceWithoutStatus()
    {
        var model = new FakeChatModelClient { CurrentTokenCount = 30, Chunks = Chunks("Hel", "lo") };
        var store = new FakeConversationStore();
        var quota = new FakeQuotaService();

        var result = Run(NewOrchestrator(model, store, quota).SendAsync(
            new ChatSendRequest(null, User(), "hi", []), _ => Task.CompletedTask));

        Assert.Equal(ChatStatuses.Completed, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal("Hello", result.Content);
        var saved = store.Messages.Where(m => m.Role == "model").ToList();
        Assert.Equal(1, saved.Count);
        Assert.Null(saved[0].Status);
        Assert.Equal(1, quota.RecordCount);
    }

    public static void LongRunningStreamCompletesAndKeepsOrder()
    {
        var model = new FakeChatModelClient
        {
            Chunks = Enumerable.Range(0, 25).Select(i => new ChatChunk { Text = $"{i};" }).ToList(),
            BeforeChunk = (_, token) => Task.Delay(20, token)
        };
        var updates = new List<ChatStreamUpdate>();

        var result = Run(NewOrchestrator(model, new FakeConversationStore()).SendAsync(
            new ChatSendRequest(null, User(), "hi", []),
            update =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            }));

        Assert.Equal(ChatStatuses.Completed, result.Status);
        Assert.Equal(25, updates.Count);
        Assert.True(result.Content.StartsWith("0;1;2;"));
        Assert.True(result.Content.EndsWith("24;"));
        Assert.True(updates.All(u => u.ConversationId.HasValue));
    }

    public static void OutputLimitIsReportedAsTruncatedNotSuccess()
    {
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "partial answer" }, new ChatChunk { Text = "", IsTruncated = true }]
        };
        var store = new FakeConversationStore();
        var updates = new List<ChatStreamUpdate>();

        var result = Run(NewOrchestrator(model, store).SendAsync(
            new ChatSendRequest(null, User(), "hi", []),
            update =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            }));

        Assert.Equal(ChatStatuses.Truncated, result.Status);
        Assert.True(result.Succeeded); // usable but incomplete
        Assert.Equal("partial answer", result.Content);
        Assert.Equal(1, updates.Count); // the marker chunk is not a content update
        Assert.Equal(ChatStatuses.Truncated, store.Messages.Single(m => m.Role == "model").Status);
    }

    public static void UserCancelKeepsPartialReplyAndSettlesOnce()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeChatModelClient
        {
            CurrentTokenCount = 0,
            Chunks = Chunks("one ", "two ", "three ", "four ")
        };
        var store = new FakeConversationStore();
        var quota = new FakeQuotaService();

        var result = Run(NewOrchestrator(model, store, quota).SendAsync(
            new ChatSendRequest(null, User(), "hi", []),
            update =>
            {
                if (update.Content.Contains("two"))
                {
                    cts.Cancel();
                    cts.Cancel(); // repeated stop must be harmless
                }

                return Task.CompletedTask;
            },
            cts.Token));

        Assert.Equal(ChatStatuses.Cancelled, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("one two ", result.Content);
        var saved = store.Messages.Where(m => m.Role == "model").ToList();
        Assert.Equal(1, saved.Count);
        Assert.Equal(ChatStatuses.Cancelled, saved[0].Status);
        Assert.Equal("one two ", saved[0].Content);
        Assert.Equal(1, quota.RecordCount);
        Assert.True(model.StreamWasDisposedEarly); // upstream enumerator released
    }

    public static void CancelBeforeAnyOutputSavesNoModelMessage()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeChatModelClient
        {
            Chunks = Chunks("never"),
            BeforeChunk = (_, token) =>
            {
                cts.Cancel();
                return Task.Delay(Timeout.Infinite, token);
            }
        };
        var store = new FakeConversationStore();

        var result = Run(NewOrchestrator(model, store).SendAsync(
            new ChatSendRequest(null, User(), "hi", []), _ => Task.CompletedTask, cts.Token));

        Assert.Equal(ChatStatuses.Cancelled, result.Status);
        Assert.Equal(1, store.Messages.Count);
        Assert.Equal("user", store.Messages[0].Role);
    }

    public static void ClientDisconnectDuringWriteIsCancelledNotFailed()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeChatModelClient { Chunks = Chunks("a", "b", "c") };
        var store = new FakeConversationStore();

        var result = Run(NewOrchestrator(model, store).SendAsync(
            new ChatSendRequest(null, User(), "hi", []),
            _ =>
            {
                cts.Cancel();
                throw new IOException("Broken pipe");
            },
            cts.Token));

        Assert.Equal(ChatStatuses.Cancelled, result.Status);
        Assert.Null(result.ErrorMessage);
    }

    public static void UpstreamErrorMidStreamIsFailedWithPartialContent()
    {
        var model = new FakeChatModelClient
        {
            Chunks = Chunks("some ", "text "),
            FailureAfterChunks = new IOException("Upstream stream ended before completion (no finish reason).")
        };
        var store = new FakeConversationStore();
        var quota = new FakeQuotaService();

        var result = Run(NewOrchestrator(model, store, quota).SendAsync(
            new ChatSendRequest(null, User(), "hi", []), _ => Task.CompletedTask));

        Assert.Equal(ChatStatuses.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal("some text ", result.Content);
        Assert.True(!string.IsNullOrWhiteSpace(result.ErrorMessage));
        var saved = store.Messages.Single(m => m.Role == "model");
        Assert.Equal(ChatStatuses.Failed, saved.Status);
        Assert.Equal(1, quota.RecordCount);
    }

    public static void GenerationCapEndsStreamAsFailedAndKeepsPartial()
    {
        var model = new FakeChatModelClient
        {
            Chunks = Chunks("started ", "never"),
            BeforeChunk = (index, token) => index == 0 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, token)
        };
        var store = new FakeConversationStore();

        var result = Run(NewOrchestrator(model, store, maxGenerationSeconds: 1).SendAsync(
            new ChatSendRequest(null, User(), "hi", []), _ => Task.CompletedTask));

        Assert.Equal(ChatStatuses.Failed, result.Status);
        Assert.Contains("时间过长", result.ErrorMessage ?? "");
        Assert.Equal("started ", result.Content);
        Assert.Equal(ChatStatuses.Failed, store.Messages.Single(m => m.Role == "model").Status);
    }

    public static void HistoryWithOrphanUserTurnsIsNormalised()
    {
        var history = new List<ChatHistoryEntry>
        {
            new("model", "leading model turn"),
            new("user", "first"),
            new("user", "second (previous reply failed)"),
            new("model", "answer"),
            new("model", "another part")
        };

        var normalised = ChatOrchestrator.NormalizeHistory(history);

        Assert.Equal(2, normalised.Count);
        Assert.Equal("user", normalised[0].Role);
        Assert.Contains("first", normalised[0].Content);
        Assert.Contains("second", normalised[0].Content);
        Assert.Equal("model", normalised[1].Role);
    }

    // ---- prompt cancellation helper ------------------------------------------------------

    public static void PromptCancellationReturnsWhileSourceIsBlocked()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BlockingSource(release.Task);
        using var cts = new CancellationTokenSource();
        var seen = new List<int>();

        var task = Task.Run(async () =>
        {
            await foreach (var item in source.Items().WithPromptCancellation(cts.Token))
            {
                seen.Add(item);
                if (item == 1)
                {
                    cts.CancelAfter(50);
                }
            }
        });

        var thrown = false;
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new InvalidOperationException("Cancellation was not prompt: the read is still blocked.");
            }
        }
        catch (AggregateException ex) when (ex.GetBaseException() is OperationCanceledException)
        {
            thrown = true;
        }

        Assert.True(thrown);
        Assert.SequenceEqual([1], seen);
        Assert.False(source.Disposed); // cannot dispose while a read is in flight...
        release.SetResult();           // ...the in-flight read finishes, then the source is released
        SpinWait.SpinUntil(() => source.Disposed, TimeSpan.FromSeconds(3));
        Assert.True(source.Disposed);
    }

    public static void PromptCancellationPassesItemsAndDisposesOnBreak()
    {
        var source = new BlockingSource(Task.CompletedTask);
        var seen = new List<int>();

        Run(Task.Run(async () =>
        {
            await foreach (var item in source.Items().WithPromptCancellation(CancellationToken.None))
            {
                seen.Add(item);
                if (seen.Count == 2)
                {
                    break;
                }
            }
        }));

        Assert.SequenceEqual([1, 2], seen);
        Assert.True(source.Disposed);
    }

    private sealed class BlockingSource
    {
        private readonly Task _gateAfterFirst;

        public BlockingSource(Task gateAfterFirst)
        {
            _gateAfterFirst = gateAfterFirst;
        }

        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<int> Items()
        {
            try
            {
                yield return 1;
                await _gateAfterFirst; // models a read that does not observe any cancellation token
                yield return 2;
                yield return 3;
            }
            finally
            {
                Disposed = true;
            }
        }
    }

    // ---- endpoint: SSE framing, single final event, broken connections -------------------

    private static ChatEndpoints.ApiChatSendRequest Body(string message = "hi") =>
        new(null, message, [], [], null, null, null, null, null, null, null, null, null);

    private static (DefaultHttpContext Context, MemoryStream Body) NewHttpContext(CancellationToken aborted = default)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestAborted = aborted };
        context.Response.Body = body;
        return (context, body);
    }

    private static string Text(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    public static void EndpointWritesUpdatesAndExactlyOneFinalEvent()
    {
        var model = new FakeChatModelClient { Chunks = Chunks("Hel", "lo") };
        var (context, body) = NewHttpContext();

        Run(ChatEndpoints.StreamChatAsync(
            Body(), context, new FakeUserContext(User()),
            NewOrchestrator(model, new FakeConversationStore())));

        var text = Text(body);
        Assert.Equal(2, Count(text, "event: update"));
        Assert.Equal(1, Count(text, "event: final"));
        Assert.Contains("\"status\":\"completed\"", text);
        Assert.Contains("\"succeeded\":true", text);
        Assert.True(text.StartsWith(": stream-open"));
        Assert.True(text.LastIndexOf("event: final", StringComparison.Ordinal) > text.LastIndexOf("event: update", StringComparison.Ordinal));
    }

    public static void EndpointReportsCancelledWhenClientAborts()
    {
        using var aborted = new CancellationTokenSource();
        var model = new FakeChatModelClient
        {
            Chunks = Chunks("a ", "b ", "c "),
            BeforeChunk = (index, _) =>
            {
                if (index == 1)
                {
                    aborted.Cancel();
                }

                return Task.CompletedTask;
            }
        };
        var (context, body) = NewHttpContext(aborted.Token);

        Run(ChatEndpoints.StreamChatAsync(
            Body(), context, new FakeUserContext(User()),
            NewOrchestrator(model, new FakeConversationStore())));

        var text = Text(body);
        Assert.Equal(1, Count(text, "event: final"));
        Assert.Contains("\"status\":\"cancelled\"", text);
        Assert.Contains("\"succeeded\":false", text);
        Assert.Contains("\"content\":\"a \"", text);
    }

    public static void EndpointSurvivesBrokenConnectionAndStopsGenerating()
    {
        var model = new FakeChatModelClient
        {
            Chunks = Enumerable.Range(0, 50).Select(i => new ChatChunk { Text = $"{i} " }).ToList()
        };
        var store = new FakeConversationStore();
        var failing = new FailingStream(failAfterWrites: 3);
        var context = new DefaultHttpContext();
        context.Response.Body = failing;

        Run(ChatEndpoints.StreamChatAsync(
            Body(), context, new FakeUserContext(User()),
            NewOrchestrator(model, store))); // must not throw

        Assert.True(failing.WritesAttemptedAfterFailure <= 1);
        Assert.True(model.StreamWasDisposedEarly);
        var saved = store.Messages.Single(m => m.Role == "model");
        Assert.Equal(ChatStatuses.Cancelled, saved.Status);
    }

    public static void EndpointSendsHeartbeatWhileModelIsSilent()
    {
        var model = new FakeChatModelClient
        {
            Chunks = Chunks("done"),
            BeforeChunk = (_, token) => Task.Delay(2600, token)
        };
        var (context, body) = NewHttpContext();

        Run(ChatEndpoints.StreamChatAsync(
            Body(), context, new FakeUserContext(User()),
            new ChatOrchestrator(
                new FakeProviderCatalog(model),
                new FakeConversationStore(),
                NullLogger<ChatOrchestrator>.Instance),
            Options.Create(new ChatStreamSettings { HeartbeatSeconds = 1 })));

        var text = Text(body);
        Assert.True(Count(text, ": ping") >= 2);
        Assert.Equal(1, Count(text, "event: final"));
    }

    public static void EndpointRejectsUnauthenticatedBeforeStreaming()
    {
        var (context, body) = NewHttpContext();

        Run(ChatEndpoints.StreamChatAsync(
            Body(), context, new FakeUserContext(null),
            NewOrchestrator(new FakeChatModelClient(), new FakeConversationStore())));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(Text(body).Contains("event:"));
    }

    private sealed class FailingStream : MemoryStream
    {
        private readonly int _failAfterWrites;
        private int _writes;

        public FailingStream(int failAfterWrites)
        {
            _failAfterWrites = failAfterWrites;
        }

        public int WritesAttemptedAfterFailure { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes > _failAfterWrites)
            {
                WritesAttemptedAfterFailure++;
                throw new IOException("Broken pipe");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    // ---- OpenAI-compatible client: truncation, abnormal end, cancellation ----------------

    private static OpenAICompatibleChatModelClient NewClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new OpenAICompatibleProviderSettings
            {
                Enabled = true,
                ProviderId = "test",
                Name = "Test",
                ApiKey = "test-key",
                Endpoint = "https://provider.example/v1/chat/completions",
                ModelName = "test-model"
            });

    private static async Task<List<ChatChunk>> ReadAll(IAsyncEnumerable<ChatChunk> chunks, CancellationToken token = default)
    {
        var list = new List<ChatChunk>();
        await foreach (var chunk in chunks.WithCancellation(token))
        {
            list.Add(chunk);
        }

        return list;
    }

    public static void OpenAiFinishReasonLengthYieldsTruncatedMarker()
    {
        var client = NewClient(new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"abc\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"));

        var chunks = Run(ReadAll(client.StreamChatAsync(new ChatModelRequest("hi", []))));

        Assert.Equal("abc", string.Concat(chunks.Select(c => c.Text)));
        Assert.True(chunks[^1].IsTruncated);
    }

    public static void OpenAiStreamEndingWithoutFinishIsAnError()
    {
        var client = NewClient(new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"half an ans\"}}]}\n\n"));

        Assert.Throws<IOException>(() => Run(ReadAll(client.StreamChatAsync(new ChatModelRequest("hi", [])))));
    }

    public static void OpenAiStopFinishWithoutDoneIsAccepted()
    {
        var client = NewClient(new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"));

        var chunks = Run(ReadAll(client.StreamChatAsync(new ChatModelRequest("hi", []))));

        Assert.Equal("ok", string.Concat(chunks.Select(c => c.Text)));
        Assert.False(chunks.Any(c => c.IsTruncated));
    }

    public static void OpenAiCancellationStopsReadingAndClosesUpstream()
    {
        var handler = new HangingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n");
        var client = NewClient(handler);
        using var cts = new CancellationTokenSource();
        var texts = new List<string>();

        var thrown = false;
        try
        {
            Run(Task.Run(async () =>
            {
                await foreach (var chunk in client.StreamChatAsync(new ChatModelRequest("hi", []), cts.Token))
                {
                    texts.Add(chunk.Text);
                    cts.CancelAfter(50);
                }
            }).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        catch (OperationCanceledException)
        {
            thrown = true;
        }

        Assert.True(thrown);
        Assert.SequenceEqual(["first"], texts);
        Assert.True(handler.BodyDisposed); // response stream (the upstream connection) was closed
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private readonly string _firstLine;

        public HangingHandler(string firstLine)
        {
            _firstLine = firstLine;
        }

        public bool BodyDisposed => _stream?.IsDisposed ?? false;
        private HangingStream? _stream;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _stream = new HangingStream(Encoding.UTF8.GetBytes(_firstLine));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(_stream)
            });
        }
    }

    private sealed class HangingStream : Stream
    {
        private readonly byte[] _first;
        private int _position;

        public HangingStream(byte[] first)
        {
            _first = first;
        }

        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _first.Length)
            {
                var count = Math.Min(buffer.Length, _first.Length - _position);
                _first.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken); // upstream stays silent
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
