using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VertexAI.Data.Entities;

var tests = new (string Name, Action Test)[]
{
    ("AuthInputValidator normalizes emails", AuthInputValidatorTests.NormalizeEmail),
    ("AuthInputValidator validates email shape", AuthInputValidatorTests.ValidateEmail),
    ("AuthInputValidator validates password strength", AuthInputValidatorTests.ValidatePasswordStrength),
    ("AuthTokenGenerator creates url-safe tokens", AuthTokenGeneratorTests.GenerateUrlSafeToken),
    ("AuthRateLimiter limits and resets client attempts", AuthRateLimiterTests.LimitAndResetAttempts),
    ("AuthRateLimiter honors forwarded client IP", AuthRateLimiterTests.HonorForwardedClientIp),
    ("AuthCookieService uses workspace cookie and reads legacy cookie", AuthCookieServiceTests.WorkspaceAndLegacyCookies),
    ("AuthWorkflowResult maps common statuses", AuthWorkflowResultTests.MapStatuses),
    ("GeminiPartFactory creates text and image parts", GeminiPartFactoryTests.CreateTextAndImageParts),
    ("ChatAttachmentValidator validates image payloads", ChatAttachmentValidatorTests.ValidatePayloads),
    ("ChatErrorMapper maps common exceptions", ChatErrorMapperTests.MapCommonExceptions),
    ("ChatOrchestrator streams and persists successful responses", ChatOrchestratorTests.StreamsAndPersistsSuccess),
    ("ChatOrchestrator passes multimodal model request", ChatOrchestratorTests.PassesMultimodalModelRequest),
    ("ChatOrchestrator loads existing conversation history", ChatOrchestratorTests.LoadsExistingConversationHistory),
    ("ChatOrchestrator applies model session options", ChatOrchestratorTests.AppliesModelSessionOptions),
    ("ChatOrchestrator maps model failures", ChatOrchestratorTests.MapsModelFailures),
    ("ChatProviderCatalog selects registered provider", ChatProviderCatalogTests.SelectsRegisteredProvider),
    ("ChatProviderCatalog falls back from invalid default provider", ChatProviderCatalogTests.FallsBackFromInvalidDefaultProvider),
    ("MockChatModelClient streams local multimodal response", MockChatModelClientTests.StreamsMultimodalResponse),
    ("OpenAICompatibleChatModelClient streams SSE response", OpenAICompatibleChatModelClientTests.StreamsSseResponse)
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

internal static class AuthCookieServiceTests
{
    public static void WorkspaceAndLegacyCookies()
    {
        var cookies = new AuthCookieService();
        var signInContext = new DefaultHttpContext();

        cookies.SignIn(signInContext, "new-token");

        var setCookie = signInContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("vertex_auth=new-token", setCookie);
        Assert.Contains("gemini_auth=", setCookie);

        var readContext = new DefaultHttpContext();
        readContext.Request.Headers.Cookie = "gemini_auth=legacy-token";
        Assert.Equal("legacy-token", cookies.ReadSessionToken(readContext));

        var signOutContext = new DefaultHttpContext();
        cookies.SignOut(signOutContext);
        var signOutCookie = signOutContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("vertex_auth=", signOutCookie);
        Assert.Contains("gemini_auth=", signOutCookie);
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

internal static class ChatAttachmentValidatorTests
{
    public static void ValidatePayloads()
    {
        var validImage = new ChatImageAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "ok.png");

        Assert.Null(ChatAttachmentValidator.Validate([validImage]));
        Assert.NotNull(ChatAttachmentValidator.Validate([
            validImage,
            validImage,
            validImage,
            validImage,
            validImage,
            validImage
        ]));
        Assert.NotNull(ChatAttachmentValidator.Validate([validImage with { MimeType = "text/plain" }]));
        Assert.NotNull(ChatAttachmentValidator.Validate([validImage with { Base64Data = "not-base64" }]));
        Assert.NotNull(ChatAttachmentValidator.Validate([
            validImage with { Base64Data = Convert.ToBase64String(new byte[4 * 1024 * 1024 + 1]) }
        ]));
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
            new FakeProviderCatalog(model),
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
        Assert.Equal(("user", "hi", null), (store.Messages[0].Role, store.Messages[0].Content, store.Messages[0].ThinkingContent));
        Assert.Equal(("model", "Hello world", "thinking"), (store.Messages[1].Role, store.Messages[1].Content, store.Messages[1].ThinkingContent));
        Assert.Equal((conversationId, userId, 42), store.TokenUpdates.Single());
        Assert.Equal("default", store.CreatedPresetId);
        var lastRequest = model.LastRequest;
        Assert.NotNull(lastRequest);
        Assert.Equal("hi", lastRequest!.Message);
        Assert.Equal(0, lastRequest.Images.Count);
        Assert.False(lastRequest.EnableSearch);
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
            new FakeProviderCatalog(model),
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

    public static void PassesMultimodalModelRequest()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var image = new ChatImageAttachment(
            Convert.ToBase64String([1, 2, 3]),
            "image/png",
            "diagram.png");
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "ok" }]
        };
        var store = new FakeConversationStore { CreatedConversationId = conversationId };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, userId, "  explain this  ", [image], EnableSearch: true),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        var lastRequest = model.LastRequest;
        Assert.NotNull(lastRequest);
        Assert.Equal("explain this", lastRequest!.Message);
        Assert.Equal(1, lastRequest.Images.Count);
        Assert.Equal(image, lastRequest.Images.Single());
        Assert.True(lastRequest.EnableSearch);
        Assert.Equal(1, store.Messages[0].Attachments.Count);
        Assert.Equal(image, store.Messages[0].Attachments.Single());
    }

    public static void LoadsExistingConversationHistory()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var image = new ChatImageAttachment(Convert.ToBase64String([4, 5, 6]), "image/png", "history.png");
        var history = new[]
        {
            new ChatHistoryEntry("user", "hello", Attachments: [image]),
            new ChatHistoryEntry("model", "hi", "thinking")
        };
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "again" }]
        };
        var store = new FakeConversationStore { History = history };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(conversationId, userId, "continue", []),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal(2, model.LoadedHistory.Count);
        Assert.Equal(history[0], model.LoadedHistory[0]);
        Assert.Equal(history[1], model.LoadedHistory[1]);
        Assert.Equal(1, model.LoadedHistory[0].Attachments?.Count ?? 0);
        Assert.Equal(image, model.LoadedHistory[0].Attachments!.Single());
        Assert.Null(store.CreatedPresetId);
        Assert.Equal(("user", "continue", null), (store.Messages[0].Role, store.Messages[0].Content, store.Messages[0].ThinkingContent));
    }

    public static void AppliesModelSessionOptions()
    {
        var userId = Guid.NewGuid();
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "configured" }]
        };
        var store = new FakeConversationStore();
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(
                null,
                userId,
                "hi",
                [],
                Options: new ChatSessionOptions("fake", "fast-model", "custom", "Stay concise.")),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal(new ChatSessionOptions("fake", "fast-model", "custom", "Stay concise."), model.LastOptions);
        Assert.Equal("fake", store.CreatedProviderId);
        Assert.Equal("fast-model", store.CreatedModelName);
        Assert.Equal("custom", store.CreatedPresetId);
        Assert.Equal("Stay concise.", store.CreatedCustomPrompt);
    }

    private static T Run<T>(Task<T> task) =>
        task.GetAwaiter().GetResult();
}

internal sealed class FakeProviderCatalog : IChatProviderCatalog
{
    private readonly FakeChatModelClient _client;

    public FakeProviderCatalog(FakeChatModelClient client)
    {
        _client = client;
    }

    public IReadOnlyList<IChatModelProvider> Providers { get; } =
    [
        new FakeProvider("fake", new FakeChatModelClient())
    ];

    public string ResolveProviderId(string? providerId) => providerId ?? "fake";

    public IChatModelClient CreateClient(string? providerId)
    {
        _client.LastProviderId = providerId;
        return _client;
    }
}

internal static class ChatProviderCatalogTests
{
    public static void SelectsRegisteredProvider()
    {
        var first = new FakeProvider("first", new FakeChatModelClient());
        var secondClient = new FakeChatModelClient();
        var second = new FakeProvider("second", secondClient);
        var catalog = new ChatProviderCatalog(
            [first, second],
            Options.Create(new WorkspaceSettings { DefaultProviderId = "first" }));

        Assert.Equal("first", catalog.Providers[0].Info.Id);
        Assert.Equal(secondClient, catalog.CreateClient("second"));
        Assert.Equal(first.Client, catalog.CreateClient(null));
    }

    public static void FallsBackFromInvalidDefaultProvider()
    {
        var first = new FakeProvider("first", new FakeChatModelClient());
        var catalog = new ChatProviderCatalog(
            [first],
            Options.Create(new WorkspaceSettings { DefaultProviderId = "missing" }));

        Assert.Equal("first", catalog.ResolveProviderId(null));
        Assert.Equal(first.Client, catalog.CreateClient(null));
        Assert.Throws<InvalidOperationException>(() => catalog.CreateClient("missing"));
    }
}

internal static class MockChatModelClientTests
{
    public static void StreamsMultimodalResponse()
    {
        var client = new MockChatModelClient();
        var image = new ChatImageAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "pixel.png");

        Run(client.ConfigureAsync(new ChatSessionOptions("mock", "mock-detailed", "custom", "Be brief.")));
        Run(client.LoadHistoryAsync([new ChatHistoryEntry("user", "previous")]));
        var chunks = Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest("", [image], EnableSearch: true))));

        Assert.True(chunks.Count > 0);
        Assert.Contains("Mock provider mock-detailed", string.Concat(chunks.Select(c => c.Text)));
        Assert.True(client.CurrentTokenCount > 0);
    }

    private static async Task<List<ChatChunk>> ReadAllAsync(IAsyncEnumerable<ChatChunk> chunks)
    {
        var result = new List<ChatChunk>();
        await foreach (var chunk in chunks)
        {
            result.Add(chunk);
        }

        return result;
    }

    private static T Run<T>(Task<T> task) =>
        task.GetAwaiter().GetResult();

    private static void Run(Task task) =>
        task.GetAwaiter().GetResult();
}

internal static class OpenAICompatibleChatModelClientTests
{
    public static void StreamsSseResponse()
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var client = new OpenAICompatibleChatModelClient(
            new HttpClient(handler),
            Options.Create(new OpenAICompatibleSettings
            {
                Enabled = true,
                ApiKey = "test-key",
                Endpoint = "https://provider.example/v1/chat/completions",
                ModelName = "test-model"
            }));

        Run(client.LoadHistoryAsync([
            new ChatHistoryEntry(
                "user",
                "Previous image",
                Attachments: [new ChatImageAttachment(Convert.ToBase64String([9, 8, 7]), "image/png", "history.png")]),
            new ChatHistoryEntry("model", "Previous answer")
        ]));
        var chunks = Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest(
            "Describe",
            [new ChatImageAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "image.png")]))));

        Assert.Equal("Hello", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
        Assert.Contains("\"model\":\"test-model\"", handler.RequestBody ?? "");
        Assert.Contains("\"role\":\"assistant\"", handler.RequestBody ?? "");
        Assert.Contains("CQgH", handler.RequestBody ?? "");
        Assert.Contains("\"image_url\"", handler.RequestBody ?? "");
        Assert.True(client.CurrentTokenCount > 0);
    }

    private static async Task<List<ChatChunk>> ReadAllAsync(IAsyncEnumerable<ChatChunk> chunks)
    {
        var result = new List<ChatChunk>();
        await foreach (var chunk in chunks)
        {
            result.Add(chunk);
        }

        return result;
    }

    private static T Run<T>(Task<T> task) =>
        task.GetAwaiter().GetResult();

    private static void Run(Task task) =>
        task.GetAwaiter().GetResult();
}

internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly string _response;

    public CapturingHandler(string response)
    {
        _response = response;
    }

    public string? RequestBody { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? AuthorizationParameter { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(_response)
        };
    }
}

internal sealed class FakeProvider : IChatModelProvider
{
    public FakeProvider(string id, FakeChatModelClient client)
    {
        Client = client;
        Info = new ChatProviderInfo(id, id, "Fake provider");
    }

    public FakeChatModelClient Client { get; }
    public ChatProviderInfo Info { get; }
    public IReadOnlyList<ChatModelOption> ModelOptions => Client.ModelOptions;
    public IReadOnlyList<PromptPresetConfig> Presets => Client.Presets;
    public string DefaultModelName => Client.CurrentModelName;
    public string DefaultPresetId => Client.CurrentPresetId;
    public IChatModelClient CreateClient() => Client;
}

internal sealed class FakeChatModelClient : IChatModelClient
{
    public int CurrentTokenCount { get; set; }
    public string CurrentModelName { get; set; } = "fake-model";
    public string CurrentPresetId { get; set; } = "default";
    public string CurrentCustomPrompt { get; set; } = "";
    public IReadOnlyList<ChatModelOption> ModelOptions { get; set; } = [];
    public IReadOnlyList<PromptPresetConfig> Presets { get; set; } = [];
    public List<ChatChunk> Chunks { get; set; } = [];
    public Exception? Failure { get; set; }
    public IReadOnlyList<ChatHistoryEntry> LoadedHistory { get; private set; } = [];
    public ChatSessionOptions? LastOptions { get; private set; }
    public string? LastProviderId { get; set; }

    public ChatModelRequest? LastRequest { get; private set; }

    public Task ConfigureAsync(ChatSessionOptions? options)
    {
        LastOptions = options;

        if (!string.IsNullOrWhiteSpace(options?.PresetId))
        {
            CurrentPresetId = options.PresetId;
            CurrentCustomPrompt = options.CustomPrompt ?? "";
        }

        if (!string.IsNullOrWhiteSpace(options?.ModelName))
        {
            CurrentModelName = options.ModelName;
        }

        return Task.CompletedTask;
    }

    public Task LoadHistoryAsync(IReadOnlyCollection<ChatHistoryEntry> messages)
    {
        LoadedHistory = messages.ToList();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request)
    {
        await Task.Yield();
        LastRequest = request;

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
    public string? CreatedProviderId { get; private set; }
    public string? CreatedModelName { get; private set; }
    public string? CreatedPresetId { get; private set; }
    public string? CreatedCustomPrompt { get; private set; }
    public List<FakeStoredMessage> Messages { get; } = [];
    public IReadOnlyList<ChatHistoryEntry> History { get; init; } = [];
    public List<(Guid ConversationId, Guid UserId, int TokenCount)> TokenUpdates { get; } = [];

    public Task<Conversation?> CreateConversationAsync(
        Guid userId,
        string providerId,
        string modelName,
        string presetId,
        string? customPrompt = null)
    {
        CreatedProviderId = providerId;
        CreatedModelName = modelName;
        CreatedPresetId = presetId;
        CreatedCustomPrompt = customPrompt;

        return Task.FromResult<Conversation?>(new Conversation
        {
            Id = CreatedConversationId,
            UserId = userId,
            ProviderId = providerId,
            ModelName = modelName,
            PresetId = presetId,
            CustomPrompt = customPrompt
        });
    }

    public Task<IReadOnlyList<ChatHistoryEntry>> GetHistoryAsync(Guid conversationId, Guid userId) =>
        Task.FromResult(History);

    public Task<Message?> AddMessageAsync(
        Guid conversationId,
        Guid userId,
        string role,
        string content,
        string? thinkingContent = null,
        IReadOnlyCollection<ChatImageAttachment>? attachments = null)
    {
        Messages.Add(new FakeStoredMessage(
            role,
            content,
            thinkingContent,
            attachments?.ToList() ?? []));
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

internal sealed record FakeStoredMessage(
    string Role,
    string Content,
    string? ThinkingContent,
    IReadOnlyList<ChatImageAttachment> Attachments);

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

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
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
