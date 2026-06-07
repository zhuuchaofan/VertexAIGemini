using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Google.GenAI.Types;
using VertexAI.Data.Entities;

var tests = new (string Name, Action Test)[]
{
    ("AuthInputValidator normalizes emails", AuthInputValidatorTests.NormalizeEmail),
    ("AuthInputValidator validates email shape", AuthInputValidatorTests.ValidateEmail),
    ("AuthInputValidator validates password strength", AuthInputValidatorTests.ValidatePasswordStrength),
    ("AuthTokenGenerator creates url-safe tokens", AuthTokenGeneratorTests.GenerateUrlSafeToken),
    ("AuthRateLimiter limits and resets client attempts", AuthRateLimiterTests.LimitAndResetAttempts),
    ("AuthRateLimiter honors forwarded client IP", AuthRateLimiterTests.HonorForwardedClientIp),
    ("AuthWorkflowResult maps common statuses", AuthWorkflowResultTests.MapStatuses),
    ("GeminiPartFactory creates text and image parts", GeminiPartFactoryTests.CreateTextAndImageParts),
    ("ChatErrorMapper maps common exceptions", ChatErrorMapperTests.MapCommonExceptions),
    ("ChatOrchestrator streams and persists successful responses", ChatOrchestratorTests.StreamsAndPersistsSuccess),
    ("ChatOrchestrator maps model failures", ChatOrchestratorTests.MapsModelFailures)
};

var failures = new List<string>();

foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures.Count} test(s) failed:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine();
Console.WriteLine($"{tests.Length} tests passed.");
return 0;

internal static class AuthInputValidatorTests
{
    public static void NormalizeEmail()
    {
        Assert.Equal("user@example.com", AuthInputValidator.NormalizeEmail("  User@Example.COM "));
    }

    public static void ValidateEmail()
    {
        Assert.True(AuthInputValidator.IsValidEmail("user@example.com"));
        Assert.False(AuthInputValidator.IsValidEmail("missing-at.example.com"));
        Assert.False(AuthInputValidator.IsValidEmail("missing-domain@"));
    }

    public static void ValidatePasswordStrength()
    {
        Assert.Null(AuthInputValidator.ValidatePasswordStrength("abc123"));
        Assert.NotNull(AuthInputValidator.ValidatePasswordStrength("abc"));
        Assert.NotNull(AuthInputValidator.ValidatePasswordStrength("abcdef"));
        Assert.NotNull(AuthInputValidator.ValidatePasswordStrength("123456"));
        Assert.NotNull(AuthInputValidator.ValidatePasswordStrength(new string('a', 101) + "1"));
    }
}

internal static class AuthTokenGeneratorTests
{
    public static void GenerateUrlSafeToken()
    {
        var generator = new AuthTokenGenerator();
        var token = generator.Generate();

        Assert.Equal(43, token.Length);
        Assert.False(token.Contains('+'));
        Assert.False(token.Contains('/'));
        Assert.False(token.Contains('='));
        Assert.NotEqual(token, generator.Generate());
    }
}

internal static class AuthRateLimiterTests
{
    public static void LimitAndResetAttempts()
    {
        var limiter = new AuthRateLimiter();
        var context = CreateHttpContext("10.0.0.1");

        for (var i = 0; i < 5; i++)
        {
            Assert.False(limiter.IsLimited(context));
            limiter.RecordFailure(context);
        }

        Assert.True(limiter.IsLimited(context));
        limiter.Reset(context);
        Assert.False(limiter.IsLimited(context));
    }

    public static void HonorForwardedClientIp()
    {
        var limiter = new AuthRateLimiter();
        var first = CreateHttpContext("10.0.0.1", "203.0.113.10, 10.0.0.1");
        var second = CreateHttpContext("10.0.0.2", "203.0.113.10");

        for (var i = 0; i < 5; i++)
        {
            limiter.RecordFailure(first);
        }

        Assert.True(limiter.IsLimited(second));
    }

    private static DefaultHttpContext CreateHttpContext(string remoteIp, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);

        if (!string.IsNullOrEmpty(forwardedFor))
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }
}

internal static class AuthWorkflowResultTests
{
    public static void MapStatuses()
    {
        var user = new UserInfo(Guid.NewGuid(), "user@example.com", true);

        var ok = AuthWorkflowResult.Ok(user: user);
        Assert.Equal(AuthWorkflowStatus.Ok, ok.Status);
        Assert.True(ok.Response.Success);
        Assert.Equal(user, ok.Response.User);

        var badRequest = AuthWorkflowResult.BadRequest("bad");
        Assert.Equal(AuthWorkflowStatus.BadRequest, badRequest.Status);
        Assert.False(badRequest.Response.Success);
        Assert.Equal("bad", badRequest.Response.Error);

        Assert.Equal(AuthWorkflowStatus.Unauthorized, AuthWorkflowResult.Unauthorized().Status);
        Assert.Equal(AuthWorkflowStatus.RateLimited, AuthWorkflowResult.RateLimited().Status);
    }
}

internal static class GeminiPartFactoryTests
{
    public static void CreateTextAndImageParts()
    {
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var image = new ChatImageAttachment(
            Convert.ToBase64String(imageBytes),
            "image/png",
            "sample.png");

        var parts = GeminiPartFactory.CreateParts("  hello  ", [image]);

        Assert.Equal(2, parts.Count);
        Assert.Equal("hello", parts[0].Text);
        Assert.Equal("image/png", parts[1].InlineData?.MimeType);
        Assert.SequenceEqual(imageBytes, parts[1].InlineData?.Data);

        var imageOnly = GeminiPartFactory.CreateParts("   ", [image]);
        Assert.Equal(1, imageOnly.Count);
        Assert.NotNull(imageOnly[0].InlineData);
    }
}

internal static class ChatErrorMapperTests
{
    public static void MapCommonExceptions()
    {
        Assert.Equal("请求超时，请重试", ChatErrorMapper.ToUserMessage(new TaskCanceledException()));
        Assert.Equal("网络连接异常，请检查网络后重试", ChatErrorMapper.ToUserMessage(new HttpRequestException()));
        Assert.Contains("Vertex AI", ChatErrorMapper.ToUserMessage(new InvalidOperationException("Vertex AI project is not configured.")));
        Assert.Contains("配额", ChatErrorMapper.ToUserMessage(new Exception("quota exceeded")));
        Assert.Contains("权限", ChatErrorMapper.ToUserMessage(new Exception("permission denied")));
    }
}

internal static class ChatOrchestratorTests
{
    public static void StreamsAndPersistsSuccess()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var model = new FakeChatModelClient
        {
            CurrentTokenCount = 42,
            Chunks =
            [
                new ChatChunk { Text = "thinking", IsThinking = true },
                new ChatChunk { Text = "Hello", IsThinking = false },
                new ChatChunk { Text = " world", IsThinking = false }
            ]
        };
        var store = new FakeConversationStore { CreatedConversationId = conversationId };
        var orchestrator = new ChatOrchestrator(
            model,
            store,
            NullLogger<ChatOrchestrator>.Instance);
        var updates = new List<ChatStreamUpdate>();

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, userId, "  hi  ", []),
            update =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            }));

        Assert.True(result.Succeeded);
        Assert.Equal(conversationId, result.ConversationId);
        Assert.Equal("Hello world", result.Content);
        Assert.Equal("thinking", result.ThinkingContent);
        Assert.Equal(3, updates.Count);
        Assert.Equal("Hello world", updates[^1].Content);
        Assert.Equal("thinking", updates[^1].ThinkingContent);
        Assert.Equal(2, store.Messages.Count);
        Assert.Equal(("user", "hi", null), store.Messages[0]);
        Assert.Equal(("model", "Hello world", "thinking"), store.Messages[1]);
        Assert.Equal((conversationId, userId, 42), store.TokenUpdates.Single());
        Assert.Equal("default", store.CreatedPresetId);
    }

    public static void MapsModelFailures()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var model = new FakeChatModelClient
        {
            CurrentTokenCount = 7,
            Failure = new InvalidOperationException("Vertex AI project is not configured.")
        };
        var store = new FakeConversationStore { CreatedConversationId = conversationId };
        var orchestrator = new ChatOrchestrator(
            model,
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, userId, "hi", []),
            _ => Task.CompletedTask));

        Assert.False(result.Succeeded);
        Assert.Contains("Vertex AI", result.ErrorMessage ?? "");
        Assert.Equal(1, store.Messages.Count);
        Assert.Equal("user", store.Messages[0].Role);
        Assert.Equal((conversationId, userId, 7), store.TokenUpdates.Single());
    }

    private static T Run<T>(Task<T> task) =>
        task.GetAwaiter().GetResult();
}

internal sealed class FakeChatModelClient : IChatModelClient
{
    public int CurrentTokenCount { get; set; }
    public string CurrentPresetId { get; set; } = "default";
    public string CurrentCustomPrompt { get; set; } = "";
    public List<ChatChunk> Chunks { get; set; } = [];
    public Exception? Failure { get; set; }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(List<Part> userParts)
    {
        await Task.Yield();

        if (Failure != null)
        {
            throw Failure;
        }

        foreach (var chunk in Chunks)
        {
            yield return chunk;
        }
    }
}

internal sealed class FakeConversationStore : IConversationStore
{
    public Guid CreatedConversationId { get; set; } = Guid.NewGuid();
    public string? CreatedPresetId { get; private set; }
    public string? CreatedCustomPrompt { get; private set; }
    public List<(string Role, string Content, string? ThinkingContent)> Messages { get; } = [];
    public List<(Guid ConversationId, Guid UserId, int TokenCount)> TokenUpdates { get; } = [];

    public Task<Conversation?> CreateConversationAsync(Guid userId, string presetId, string? customPrompt = null)
    {
        CreatedPresetId = presetId;
        CreatedCustomPrompt = customPrompt;

        return Task.FromResult<Conversation?>(new Conversation
        {
            Id = CreatedConversationId,
            UserId = userId,
            PresetId = presetId,
            CustomPrompt = customPrompt
        });
    }

    public Task<Message?> AddMessageAsync(
        Guid conversationId,
        Guid userId,
        string role,
        string content,
        string? thinkingContent = null)
    {
        Messages.Add((role, content, thinkingContent));
        return Task.FromResult<Message?>(new Message
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent
        });
    }

    public Task UpdateTokenCountAsync(Guid conversationId, Guid userId, int tokenCount)
    {
        TokenUpdates.Add((conversationId, userId, tokenCount));
        return Task.CompletedTask;
    }
}

internal static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected false.");
        }
    }

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got {value}.");
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected non-null.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new InvalidOperationException($"Expected value different from {notExpected}.");
        }
    }

    public static void Contains(string expectedFragment, string actual)
    {
        if (!actual.Contains(expectedFragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expectedFragment}'.");
        }
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T>? actual)
    {
        if (actual is null)
        {
            throw new InvalidOperationException("Expected sequence, got null.");
        }

        if (expected.Count != actual.Count)
        {
            throw new InvalidOperationException($"Expected {expected.Count} items, got {actual.Count}.");
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
            {
                throw new InvalidOperationException($"Expected item {i} to be {expected[i]}, got {actual[i]}.");
            }
        }
    }
}
