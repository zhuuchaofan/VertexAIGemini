using VertexAI.Services;
using VertexAI.Api;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Configuration;
using HarmBlockThreshold = Google.GenAI.Types.HarmBlockThreshold;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VertexAI.Services.Quota;

var tests = new (string Name, Action Test)[]
{
    ("GeminiPartFactory creates text and image parts", GeminiPartFactoryTests.CreateTextAndImageParts),
    ("GeminiCatalog exposes thinking levels", GeminiCatalogTests.ExposesThinkingLevels),
    ("GeminiSafetyPolicy splits admin and default thresholds", GeminiSafetyPolicyTests.SplitsAdminAndDefaultThresholds),
    ("ChatAttachmentValidator validates image payloads", ChatAttachmentValidatorTests.ValidatePayloads),
    ("ChatAttachmentValidator blocks active content", ChatAttachmentValidatorTests.BlocksActiveContent),
    ("ChatAttachmentSerializer round-trips and tolerates invalid json", ChatAttachmentSerializerTests.RoundTripsAndToleratesInvalidJson),
    ("ChatErrorMapper maps common exceptions", ChatErrorMapperTests.MapCommonExceptions),
    ("ChatOrchestrator streams and persists successful responses", ChatOrchestratorTests.StreamsAndPersistsSuccess),
    ("ChatOrchestrator passes multimodal model request", ChatOrchestratorTests.PassesMultimodalModelRequest),
    ("ChatOrchestrator passes image-only model request", ChatOrchestratorTests.PassesImageOnlyModelRequest),
    ("ChatOrchestrator loads existing conversation history", ChatOrchestratorTests.LoadsExistingConversationHistory),
    ("ChatOrchestrator reuses existing conversation", ChatOrchestratorTests.ReusesExistingConversation),
    ("ChatOrchestrator applies model session options", ChatOrchestratorTests.AppliesModelSessionOptions),
    ("ChatOrchestrator applies request augmenters", ChatOrchestratorTests.AppliesRequestAugmenters),
    ("ChatOrchestrator passes authenticated user to aware model clients", ChatOrchestratorTests.PassesAuthenticatedUserToAwareModelClients),
    ("ChatOrchestrator enforces quota", ChatOrchestratorTests.EnforcesQuota),
    ("ChatOrchestrator maps model failures", ChatOrchestratorTests.MapsModelFailures),
    ("Auth status endpoint reports authentication state", AuthEndpointTests.StatusReportsAuthenticationState),
    ("Export endpoints enforce ownership and include attachments", ExportEndpointTests.EnforcesOwnershipAndIncludesAttachments),
    ("Conversation delete endpoint passes authenticated owner to store", ConversationEndpointTests.DeletePassesAuthenticatedOwnerToStore),
    ("Admin quota usage endpoint requires admin", AdminEndpointTests.QuotaUsageRequiresAdmin),
    ("Admin quota usage endpoint returns daily usage", AdminEndpointTests.QuotaUsageReturnsDailyUsage),
    ("ChatProviderCatalog selects registered provider", ChatProviderCatalogTests.SelectsRegisteredProvider),
    ("ChatProviderCatalog falls back from invalid default provider", ChatProviderCatalogTests.FallsBackFromInvalidDefaultProvider),
    ("MockChatModelClient streams local multimodal response", MockChatModelClientTests.StreamsMultimodalResponse),
    ("MockChatModelClient applies custom session prompt", MockChatModelClientTests.AppliesCustomSessionPrompt),
    ("OpenAICompatibleCatalog enables providers from environment keys", OpenAICompatibleCatalogTests.EnablesProvidersFromEnvironmentKeys),
    ("OpenAICompatibleCatalog preserves legacy single provider", OpenAICompatibleCatalogTests.PreservesLegacySingleProvider),
    ("OpenAICompatibleChatModelClient streams SSE response", OpenAICompatibleChatModelClientTests.StreamsSseResponse),
    ("OpenAICompatibleChatModelClient applies thinking options", OpenAICompatibleChatModelClientTests.AppliesThinkingOptions),
    ("OpenAICompatibleChatModelClient applies provider thinking options", OpenAICompatibleChatModelClientTests.AppliesProviderThinkingOptions),
    ("OpenAICompatibleChatModelClient streams reasoning chunks", OpenAICompatibleChatModelClientTests.StreamsReasoningChunks)
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

internal static class GeminiPartFactoryTests
{
    public static void CreateTextAndImageParts()
    {
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var image = new ChatAttachment(
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

internal static class GeminiCatalogTests
{
    public static void ExposesThinkingLevels()
    {
        var models = GeminiCatalog.CreateModelOptions(new GeminiSettings());
        var flash = models.First(model => model.ModelName == "gemini-3.5-flash");
        var pro = models.First(model => model.ModelName == "gemini-3.1-pro-preview");

        Assert.True(flash.SupportsThinking);
        Assert.Equal("medium", flash.Thinking?.Default);
        Assert.True(flash.Thinking?.Options.Any(option => option.Value == "minimal") ?? false);

        Assert.True(pro.SupportsThinking);
        Assert.False(pro.Thinking?.CanDisable ?? true);
        Assert.False(pro.Thinking?.Options.Any(option => option.Value == "off") ?? true);
    }
}

internal static class GeminiSafetyPolicyTests
{
    public static void SplitsAdminAndDefaultThresholds()
    {
        var policy = new GeminiSafetyPolicy(Options.Create(new GeminiSettings
        {
            Safety = new GeminiSafetySettings
            {
                DefaultThreshold = "BLOCK_MEDIUM_AND_ABOVE",
                AdminThreshold = "OFF",
                AdminCanDisable = true
            }
        }));

        var normalUser = new AuthenticatedUser(Guid.NewGuid(), "normal", "normal@example.com");
        var adminUser = normalUser with { IsAdmin = true };

        var normalSettings = policy.CreateSafetySettings(normalUser);
        var adminSettings = policy.CreateSafetySettings(adminUser);

        Assert.True(normalSettings.Count >= 5);
        Assert.True(normalSettings.All(setting => setting.Threshold == HarmBlockThreshold.BLOCK_MEDIUM_AND_ABOVE));
        Assert.True(adminSettings.All(setting => setting.Threshold == HarmBlockThreshold.OFF));
    }
}

internal static class ChatAttachmentValidatorTests
{
    public static void ValidatePayloads()
    {
        var validImage = new ChatAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "ok.png");

        Assert.Null(ChatAttachmentValidator.Validate([validImage]));
        Assert.NotNull(ChatAttachmentValidator.Validate([
            validImage,
            validImage,
            validImage,
            validImage,
            validImage,
            validImage,
            validImage,
            validImage,
            validImage
        ]));
        Assert.NotNull(ChatAttachmentValidator.Validate([validImage with { MimeType = "application/octet-stream" }]));
        Assert.NotNull(ChatAttachmentValidator.Validate([validImage with { Base64Data = "not-base64" }]));
        Assert.NotNull(ChatAttachmentValidator.Validate([
            validImage with { Base64Data = Convert.ToBase64String(new byte[4 * 1024 * 1024 + 1]) }
        ]));
    }

    public static void BlocksActiveContent()
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("content"));

        Assert.Null(ChatAttachmentValidator.Validate([
            new ChatAttachment(payload, "text/markdown", "notes.md")
        ]));
        Assert.NotNull(ChatAttachmentValidator.Validate([
            new ChatAttachment(payload, "text/html", "page.html")
        ]));
        Assert.NotNull(ChatAttachmentValidator.Validate([
            new ChatAttachment(payload, "application/javascript", "script.js")
        ]));
    }
}

internal static class ChatAttachmentSerializerTests
{
    public static void RoundTripsAndToleratesInvalidJson()
    {
        var image = new ChatAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "ok.png");

        var json = ChatAttachmentSerializer.Serialize([image]);
        var restored = ChatAttachmentSerializer.Deserialize(json);

        Assert.NotNull(json);
        Assert.Equal(image, restored.Single());
        Assert.Null(ChatAttachmentSerializer.Serialize([]));
        Assert.Equal(0, ChatAttachmentSerializer.Deserialize("{").Count);
        Assert.Equal(0, ChatAttachmentSerializer.Deserialize(null).Count);
    }
}

internal static class ChatErrorMapperTests
{
    public static void MapCommonExceptions()
    {
        Assert.Equal("请求超时，请重试", ChatErrorMapper.ToUserMessage(new TaskCanceledException()));
        Assert.Equal("网络连接异常，请检查网络后重试", ChatErrorMapper.ToUserMessage(new HttpRequestException()));
        Assert.Contains("Cloud Run", ChatErrorMapper.ToUserMessage(new InvalidOperationException("Vertex AI project is not configured.")));
        Assert.Contains("配额", ChatErrorMapper.ToUserMessage(new Exception("quota exceeded")));
        Assert.Contains("权限", ChatErrorMapper.ToUserMessage(new Exception("permission denied")));
    }
}

internal static class ChatOrchestratorTests
{
    public static void StreamsAndPersistsSuccess()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
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
            new ChatSendRequest(null, user, "  hi  ", []),
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
        Assert.Equal(0, lastRequest.Attachments.Count);
        Assert.Equal(SearchModes.Auto, lastRequest.SearchMode);
    }

    public static void MapsModelFailures()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
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
            new ChatSendRequest(null, user, "hi", []),
            _ => Task.CompletedTask));

        Assert.False(result.Succeeded);
        Assert.Contains("Cloud Run", result.ErrorMessage ?? "");
        Assert.Equal(1, store.Messages.Count);
        Assert.Equal("user", store.Messages[0].Role);
        Assert.Equal((conversationId, userId, 7), store.TokenUpdates.Single());
    }

    public static void EnforcesQuota()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "Hello" }]
        };
        var store = new FakeConversationStore();
        var quota = new FakeQuotaService
        {
            Failure = new InvalidOperationException("Daily request quota exceeded.")
        };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance,
            quota: quota);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, user, "hi", []),
            _ => Task.CompletedTask));

        Assert.False(result.Succeeded);
        Assert.Contains("请求次数", result.ErrorMessage ?? "");
        Assert.Equal(0, store.Messages.Count);
        Assert.Equal(1, quota.CheckCount);
        Assert.Equal(0, quota.RecordCount);
    }

    public static void PassesMultimodalModelRequest()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
        var conversationId = Guid.NewGuid();
        var image = new ChatAttachment(
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
            new ChatSendRequest(null, user, "  explain this  ", [image], SearchModes.Force),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        var lastRequest = model.LastRequest;
        Assert.NotNull(lastRequest);
        Assert.Contains("用户问题：explain this", lastRequest!.Message);
        Assert.Equal(1, lastRequest.Attachments.Count);
        Assert.Equal(image, lastRequest.Attachments.Single());
        Assert.Equal(SearchModes.Force, lastRequest.SearchMode);
        Assert.Equal(1, store.Messages[0].Attachments.Count);
        Assert.Equal(image, store.Messages[0].Attachments.Single());
    }

    public static void LoadsExistingConversationHistory()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
        var conversationId = Guid.NewGuid();
        var image = new ChatAttachment(Convert.ToBase64String([4, 5, 6]), "image/png", "history.png");
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
            new ChatSendRequest(conversationId, user, "continue", []),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal(40, store.LastHistoryMaxMessages);
        Assert.Equal(2, model.LoadedHistory.Count);
        Assert.Equal(history[0], model.LoadedHistory[0]);
        Assert.Equal(history[1], model.LoadedHistory[1]);
        Assert.Equal(1, model.LoadedHistory[0].Attachments?.Count ?? 0);
        Assert.Equal(image, model.LoadedHistory[0].Attachments!.Single());
        Assert.Null(store.CreatedPresetId);
        Assert.Equal(("user", "continue", null), (store.Messages[0].Role, store.Messages[0].Content, store.Messages[0].ThinkingContent));
    }

    public static void PassesImageOnlyModelRequest()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
        var conversationId = Guid.NewGuid();
        var image = new ChatAttachment(Convert.ToBase64String([7, 8, 9]), "image/png", "scan.png");
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "image received" }]
        };
        var store = new FakeConversationStore { CreatedConversationId = conversationId };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, user, "   ", [image]),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal("image received", result.Content);
        Assert.NotNull(model.LastRequest);
        Assert.Equal("", model.LastRequest!.Message);
        Assert.Equal(image, model.LastRequest.Attachments.Single());
        Assert.Equal(2, store.Messages.Count);
        Assert.Equal(("user", "", null), (store.Messages[0].Role, store.Messages[0].Content, store.Messages[0].ThinkingContent));
        Assert.Equal(image, store.Messages[0].Attachments.Single());
    }

    public static void ReusesExistingConversation()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
        var conversationId = Guid.NewGuid();
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "ok" }]
        };
        var store = new FakeConversationStore
        {
            CreatedConversationId = Guid.NewGuid(),
            History = [new ChatHistoryEntry("user", "prior")]
        };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(conversationId, user, "next", []),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal(conversationId, result.ConversationId);
        Assert.Equal(0, store.CreatedConversationCount);
        Assert.Equal(1, model.LoadedHistory.Count);
        Assert.Equal(("user", "next", null), (store.Messages[0].Role, store.Messages[0].Content, store.Messages[0].ThinkingContent));
        Assert.Equal((conversationId, userId, 0), store.TokenUpdates.Single());
    }

    public static void AppliesModelSessionOptions()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
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
                user,
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

    public static void AppliesRequestAugmenters()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId);
        var conversationId = Guid.NewGuid();
        var citation = new SearchCitation
        {
            Title = "Knowledge chunk",
            Uri = "kb://chunk/1"
        };
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "augmented" }]
        };
        var store = new FakeConversationStore { CreatedConversationId = conversationId };
        var augmenter = new FakeRequestAugmenter("[context] ", citation);
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance,
            augmenters: [augmenter]);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, user, "hi", []),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal("[context] hi", model.LastRequest?.Message);
        Assert.Equal("hi", store.Messages[0].Content);
        Assert.Equal(citation.Uri, result.Citations?.Single().Uri);
        Assert.Equal(0, augmenter.LastContext.History.Count);
        Assert.Equal(SearchModes.Auto, augmenter.LastContext.SearchMode);
    }

    public static void PassesAuthenticatedUserToAwareModelClients()
    {
        var userId = Guid.NewGuid();
        var user = TestUser(userId) with { IsAdmin = true };
        var model = new FakeChatModelClient
        {
            Chunks = [new ChatChunk { Text = "ok" }]
        };
        var store = new FakeConversationStore { CreatedConversationId = Guid.NewGuid() };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            store,
            NullLogger<ChatOrchestrator>.Instance);

        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, user, "hi", []),
            _ => Task.CompletedTask));

        Assert.True(result.Succeeded);
        Assert.Equal(user, model.LastAuthenticatedUser);
    }

    private static T Run<T>(Task<T> task) =>
        task.GetAwaiter().GetResult();

    private static AuthenticatedUser TestUser(Guid userId) =>
        new(userId, $"firebase-{userId:N}", "test@example.com");
}

internal static class AdminEndpointTests
{
    public static void QuotaUsageRequiresAdmin()
    {
        var reader = new FakeQuotaUsageReader();

        var anonymousStatus = ExecuteStatusCode(AdminEndpoints.GetDailyQuotaUsageAsync(
            null,
            null,
            null,
            new DefaultHttpContext(),
            new FakeUserContext(null),
            reader));

        var normalUser = new AuthenticatedUser(Guid.NewGuid(), "normal", "normal@example.com");
        var forbiddenStatus = ExecuteStatusCode(AdminEndpoints.GetDailyQuotaUsageAsync(
            null,
            null,
            null,
            new DefaultHttpContext(),
            new FakeUserContext(normalUser),
            reader));

        Assert.Equal(StatusCodes.Status401Unauthorized, anonymousStatus);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenStatus);
        Assert.Equal(0, reader.CallCount);
    }

    public static void QuotaUsageReturnsDailyUsage()
    {
        var admin = new AuthenticatedUser(Guid.NewGuid(), "admin", "admin@example.com", IsAdmin: true);
        var reader = new FakeQuotaUsageReader
        {
            Report = new QuotaUsageReport(
                "20260621",
                [
                    new QuotaUsageEntry(
                        "user-1",
                        "20260621",
                        Requests: 2,
                        EstimatedTokens: 100,
                        ActualTokens: 80,
                        Searches: 1,
                        AttachmentBytes: 2048,
                        UpdatedAt: new DateTime(2026, 6, 21, 1, 2, 3, DateTimeKind.Utc))
                ],
                new QuotaUsageTotals(2, 100, 80, 1, 2048))
        };

        var status = ExecuteStatusCode(AdminEndpoints.GetDailyQuotaUsageAsync(
            "2026-06-21",
            50,
            " user-1 ",
            new DefaultHttpContext(),
            new FakeUserContext(admin),
            reader));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal("20260621", reader.LastDate);
        Assert.Equal(50, reader.LastLimit);
        Assert.Equal("user-1", reader.LastUserId);
    }

    private static int ExecuteStatusCode(Task<IResult> task)
    {
        return ResultTestHelpers.Execute(task).StatusCode;
    }
}

internal static class AuthEndpointTests
{
    public static void StatusReportsAuthenticationState()
    {
        var user = new AuthenticatedUser(Guid.NewGuid(), "firebase-user", "user@example.com");

        var anonymous = ResultTestHelpers.Execute(AuthEndpoints.GetStatusAsync(
            new DefaultHttpContext(),
            new FakeUserContext(null)));
        var authenticated = ResultTestHelpers.Execute(AuthEndpoints.GetStatusAsync(
            new DefaultHttpContext(),
            new FakeUserContext(user)));

        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, authenticated.StatusCode);
        Assert.Contains("\"success\":true", authenticated.Body);
        Assert.Contains("\"email\":\"user@example.com\"", authenticated.Body);
        Assert.Contains($"\"id\":\"{user.LocalUserId:D}\"", authenticated.Body);
    }
}

internal static class ExportEndpointTests
{
    public static void EnforcesOwnershipAndIncludesAttachments()
    {
        var owner = new AuthenticatedUser(Guid.NewGuid(), "owner", "owner@example.com");
        var other = new AuthenticatedUser(Guid.NewGuid(), "other", "other@example.com");
        var conversationId = Guid.NewGuid();
        var attachment = new ChatAttachment(
            Convert.ToBase64String([1, 2, 3]),
            "image/png",
            "pixel.png");
        var store = new FakeConversationStore();
        store.Conversations[conversationId] = new Conversation
        {
            Id = conversationId,
            UserId = owner.LocalUserId,
            ProviderId = "gemini",
            ModelName = "gemini-3.5-flash",
            PresetId = "default",
            Title = "Export Check",
            TokenCount = 42,
            CreatedAt = new DateTime(2026, 6, 21, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 6, 21, 1, 1, 0, DateTimeKind.Utc),
            Messages =
            [
                new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    Role = "user",
                    Content = "hello",
                    AttachmentsJson = ChatAttachmentSerializer.Serialize([attachment]),
                    CreatedAt = new DateTime(2026, 6, 21, 1, 2, 0, DateTimeKind.Utc)
                }
            ]
        };

        var anonymous = ResultTestHelpers.Execute(ExportEndpoints.ExportJsonAsync(
            conversationId,
            new DefaultHttpContext(),
            store,
            new FakeUserContext(null)));
        var notOwner = ResultTestHelpers.Execute(ExportEndpoints.ExportJsonAsync(
            conversationId,
            new DefaultHttpContext(),
            store,
            new FakeUserContext(other)));
        var exported = ResultTestHelpers.Execute(ExportEndpoints.ExportMarkdownAsync(
            conversationId,
            new DefaultHttpContext(),
            store,
            new FakeUserContext(owner)));

        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, notOwner.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, exported.StatusCode);
        Assert.Contains("pixel.png", exported.Body);
        Assert.Contains("image/png", exported.Body);
        Assert.Contains("Export Check", exported.Body);
    }
}

internal static class ConversationEndpointTests
{
    public static void DeletePassesAuthenticatedOwnerToStore()
    {
        var user = new AuthenticatedUser(Guid.NewGuid(), "owner", "owner@example.com");
        var conversationId = Guid.NewGuid();
        var store = new FakeConversationStore();

        var anonymous = ResultTestHelpers.Execute(ConversationEndpoints.DeleteAsync(
            conversationId,
            new DefaultHttpContext(),
            new FakeUserContext(null),
            store));
        var deleted = ResultTestHelpers.Execute(ConversationEndpoints.DeleteAsync(
            conversationId,
            new DefaultHttpContext(),
            new FakeUserContext(user),
            store));

        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, deleted.StatusCode);
        Assert.Equal((conversationId, user.LocalUserId), store.DeletedConversations.Single());
    }
}

internal static class ResultTestHelpers
{
    public static ExecutedResult Execute(Task<IResult> task)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        var result = task.GetAwaiter().GetResult();
        result.ExecuteAsync(context).GetAwaiter().GetResult();

        body.Position = 0;
        using var reader = new StreamReader(body, leaveOpen: true);
        return new ExecutedResult(context.Response.StatusCode, reader.ReadToEnd());
    }
}

internal sealed record ExecutedResult(int StatusCode, string Body);

internal sealed class FakeUserContext : IUserContext
{
    private readonly AuthenticatedUser? _user;

    public FakeUserContext(AuthenticatedUser? user)
    {
        _user = user;
    }

    public Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context) =>
        Task.FromResult(_user);
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

internal sealed class FakeRequestAugmenter : IChatRequestAugmenter
{
    private readonly string _prefix;
    private readonly SearchCitation _citation;

    public FakeRequestAugmenter(string prefix, SearchCitation citation)
    {
        _prefix = prefix;
        _citation = citation;
    }

    public ChatRequestContext LastContext { get; private set; } = null!;

    public Task<ChatRequestAugmentation> AugmentAsync(
        ChatRequestContext context,
        ChatRequestAugmentation current,
        CancellationToken cancellationToken = default)
    {
        LastContext = context;
        return Task.FromResult(new ChatRequestAugmentation(
            $"{_prefix}{current.Message}",
            [_citation]));
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
        var image = new ChatAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "pixel.png");

        Run(client.ConfigureAsync(new ChatSessionOptions("mock", "mock-detailed", "custom", "Be brief.")));
        Run(client.LoadHistoryAsync([new ChatHistoryEntry("user", "previous")]));
        var chunks = Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest("", [image], SearchModes.Force))));

        Assert.True(chunks.Count > 0);
        Assert.Contains("Mock provider mock-detailed", string.Concat(chunks.Select(c => c.Text)));
        Assert.True(client.CurrentTokenCount > 0);
    }

    public static void AppliesCustomSessionPrompt()
    {
        var client = new MockChatModelClient();

        Run(client.ConfigureAsync(new ChatSessionOptions(
            "mock",
            "mock-fast",
            "custom",
            "Use this chat prompt.")));
        var chunks = Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest("hi", []))));
        var text = string.Concat(chunks.Select(c => c.Text));

        Assert.Contains("Custom prompt length: 21", text);
        Assert.Equal("custom", client.CurrentPresetId);
        Assert.Equal("Use this chat prompt.", client.CurrentCustomPrompt);
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
            new OpenAICompatibleProviderSettings
            {
                Enabled = true,
                ProviderId = "test",
                Name = "Test Provider",
                ApiKey = "test-key",
                Endpoint = "https://provider.example/v1/chat/completions",
                ModelName = "test-model"
            });

        Run(client.LoadHistoryAsync([
            new ChatHistoryEntry(
                "user",
                "Previous image",
                Attachments: [new ChatAttachment(Convert.ToBase64String([9, 8, 7]), "image/png", "history.png")]),
            new ChatHistoryEntry("model", "Previous answer")
        ]));
        var chunks = Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest(
            "Describe",
            [new ChatAttachment(Convert.ToBase64String([1, 2, 3]), "image/png", "image.png")]))));

        Assert.Equal("Hello", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
        Assert.Contains("\"model\":\"test-model\"", handler.RequestBody ?? "");
        Assert.Contains("\"role\":\"assistant\"", handler.RequestBody ?? "");
        Assert.Contains("CQgH", handler.RequestBody ?? "");
        Assert.Contains("\"image_url\"", handler.RequestBody ?? "");
        Assert.True(client.CurrentTokenCount > 0);
    }

    public static void AppliesThinkingOptions()
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var client = new OpenAICompatibleChatModelClient(
            new HttpClient(handler),
            new OpenAICompatibleProviderSettings
            {
                Enabled = true,
                ProviderId = "deepseek",
                Name = "DeepSeek",
                ApiKey = "test-key",
                Endpoint = "https://provider.example/v1/chat/completions",
                ModelName = "deepseek-v4-pro",
                Models =
                [
                    new()
                    {
                        Name = "DeepSeek V4 Pro",
                        ModelName = "deepseek-v4-pro",
                        SupportsThinking = true
                    }
                ]
            });

        Run(client.ConfigureAsync(new ChatSessionOptions(
            ProviderId: "deepseek",
            ModelName: "deepseek-v4-pro",
            ThinkingEnabled: true,
            ThinkingLevel: "max")));
        Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest("hi", []))));

        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", handler.RequestBody ?? "");
        Assert.Contains("\"reasoning_effort\":\"max\"", handler.RequestBody ?? "");
    }

    public static void AppliesProviderThinkingOptions()
    {
        var deepSeek = CaptureRequestBody(
            "deepseek",
            "deepseek-v4-pro",
            new ChatSessionOptions("deepseek", "deepseek-v4-pro", ThinkingEnabled: false, ThinkingLevel: "off"));
        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", deepSeek);

        var kimi = CaptureRequestBody(
            "kimi",
            "kimi-k2.7-code",
            new ChatSessionOptions("kimi", "kimi-k2.7-code", ThinkingEnabled: true));
        Assert.Contains("\"thinking\":{\"type\":\"enabled\",\"keep\":\"all\"}", kimi);

        var qwen = CaptureRequestBody(
            "qwen",
            "qwen-plus",
            new ChatSessionOptions("qwen", "qwen-plus", ThinkingEnabled: true, ThinkingBudget: 2000));
        Assert.Contains("\"enable_thinking\":true", qwen);
        Assert.Contains("\"thinking_budget\":2000", qwen);

        var zhipu = CaptureRequestBody(
            "zhipu",
            "glm-5.1",
            new ChatSessionOptions("zhipu", "glm-5.1", ThinkingEnabled: false, ThinkingLevel: "off"));
        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", zhipu);
    }

    public static void StreamsReasoningChunks()
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var client = new OpenAICompatibleChatModelClient(
            new HttpClient(handler),
            new OpenAICompatibleProviderSettings
            {
                Enabled = true,
                ProviderId = "test",
                Name = "Test Provider",
                ApiKey = "test-key",
                Endpoint = "https://provider.example/v1/chat/completions",
                ModelName = "test-model"
            });

        var chunks = Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest("hi", []))));

        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[0].IsThinking);
        Assert.Equal("plan", chunks[0].Text);
        Assert.False(chunks[1].IsThinking);
        Assert.Equal("answer", chunks[1].Text);
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

    private static string CaptureRequestBody(
        string providerId,
        string modelName,
        ChatSessionOptions options)
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var client = new OpenAICompatibleChatModelClient(
            new HttpClient(handler),
            new OpenAICompatibleProviderSettings
            {
                Enabled = true,
                ProviderId = providerId,
                Name = providerId,
                ApiKey = "test-key",
                Endpoint = "https://provider.example/v1/chat/completions",
                ModelName = modelName,
                Models =
                [
                    new()
                    {
                        Name = modelName,
                        ModelName = modelName,
                        SupportsThinking = true
                    }
                ]
            });

        Run(client.ConfigureAsync(options));
        Run(ReadAllAsync(client.StreamChatAsync(new ChatModelRequest("hi", []))));

        return handler.RequestBody ?? "";
    }
}

internal static class OpenAICompatibleCatalogTests
{
    public static void EnablesProvidersFromEnvironmentKeys()
    {
        const string envName = "VERTEX_TEST_PROVIDER_API_KEY";
        Environment.SetEnvironmentVariable(envName, "env-test-key");
        try
        {
            var providers = OpenAICompatibleCatalog.CreateEnabledProviderSettings(new OpenAICompatibleSettings
            {
                Providers =
                [
                    new()
                    {
                        ProviderId = "configured",
                        Name = "Configured",
                        Endpoint = "https://provider.example/v1/chat/completions",
                        ApiKeyEnv = envName,
                        ModelName = "configured-model"
                    },
                    new()
                    {
                        ProviderId = "missing-key",
                        Name = "Missing Key",
                        Endpoint = "https://missing.example/v1/chat/completions",
                        ApiKeyEnv = "VERTEX_MISSING_PROVIDER_API_KEY",
                        ModelName = "missing-model"
                    },
                    new()
                    {
                        ProviderId = "missing-endpoint",
                        Name = "Missing Endpoint",
                        ApiKey = "test-key",
                        ModelName = "missing-endpoint-model"
                    }
                ]
            });

            Assert.Equal(1, providers.Count);
            Assert.Equal("configured", providers[0].ProviderId);
            Assert.Equal("env-test-key", providers[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    public static void PreservesLegacySingleProvider()
    {
        var providers = OpenAICompatibleCatalog.CreateEnabledProviderSettings(new OpenAICompatibleSettings
        {
            Enabled = true,
            ProviderId = "openai-compatible",
            Name = "OpenAI Compatible",
            Endpoint = "https://provider.example/v1/chat/completions",
            ApiKey = "legacy-key",
            ModelName = "legacy-model",
            Providers =
            [
                new()
                {
                    ProviderId = "template",
                    Name = "Template",
                    Endpoint = "https://template.example/v1/chat/completions",
                    ApiKeyEnv = "VERTEX_MISSING_PROVIDER_API_KEY",
                    ModelName = "template-model"
                }
            ]
        });

        Assert.Equal(1, providers.Count);
        Assert.Equal("openai-compatible", providers[0].ProviderId);
        Assert.Equal("legacy-key", providers[0].ApiKey);
    }
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

internal sealed class FakeChatModelClient : IChatModelClient, IAuthenticatedUserAwareChatModelClient
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
    public AuthenticatedUser? LastAuthenticatedUser { get; private set; }

    public ChatModelRequest? LastRequest { get; private set; }

    public void SetAuthenticatedUser(AuthenticatedUser user)
    {
        LastAuthenticatedUser = user;
    }

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
    public int CreatedConversationCount { get; private set; }
    public List<FakeStoredMessage> Messages { get; } = [];
    public IReadOnlyList<ChatHistoryEntry> History { get; init; } = [];
    public int? LastHistoryMaxMessages { get; private set; }
    public List<(Guid ConversationId, Guid UserId, int TokenCount)> TokenUpdates { get; } = [];
    public Dictionary<Guid, Conversation> Conversations { get; } = [];
    public List<(Guid ConversationId, Guid UserId)> DeletedConversations { get; } = [];

    public Task<List<Conversation>> GetUserConversationsAsync(AuthenticatedUser user, int offset, int limit) =>
        Task.FromResult(new List<Conversation>());

    public Task<List<Conversation>> GetUserConversationsPageAsync(AuthenticatedUser user, string? cursor, int limit) =>
        Task.FromResult(new List<Conversation>());

    public Task<Conversation?> GetConversationAsync(Guid conversationId, AuthenticatedUser user)
    {
        if (!Conversations.TryGetValue(conversationId, out var conversation)
            || conversation.UserId != user.LocalUserId)
        {
            return Task.FromResult<Conversation?>(null);
        }

        return Task.FromResult<Conversation?>(conversation);
    }

    public Task<Conversation?> CreateConversationAsync(
        AuthenticatedUser user,
        string providerId,
        string modelName,
        string presetId,
        string? customPrompt = null)
    {
        CreatedConversationCount++;
        CreatedProviderId = providerId;
        CreatedModelName = modelName;
        CreatedPresetId = presetId;
        CreatedCustomPrompt = customPrompt;

        return Task.FromResult<Conversation?>(new Conversation
        {
            Id = CreatedConversationId,
            UserId = user.LocalUserId,
            ProviderId = providerId,
            ModelName = modelName,
            PresetId = presetId,
            CustomPrompt = customPrompt
        });
    }

    public Task<IReadOnlyList<ChatHistoryEntry>> GetHistoryAsync(Guid conversationId, AuthenticatedUser user, int maxMessages)
    {
        LastHistoryMaxMessages = maxMessages;
        return Task.FromResult(History.TakeLast(maxMessages).ToList() as IReadOnlyList<ChatHistoryEntry>);
    }

    public Task<Message?> AddMessageAsync(
        Guid conversationId,
        AuthenticatedUser user,
        string role,
        string content,
        string? thinkingContent = null,
        IReadOnlyCollection<ChatAttachment>? attachments = null)
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

    public Task UpdateTitleAsync(Guid conversationId, AuthenticatedUser user, string title) =>
        Task.CompletedTask;

    public Task DeleteConversationAsync(Guid conversationId, AuthenticatedUser user)
    {
        DeletedConversations.Add((conversationId, user.LocalUserId));
        return Task.CompletedTask;
    }

    public Task UpdateTokenCountAsync(Guid conversationId, AuthenticatedUser user, int tokenCount)
    {
        TokenUpdates.Add((conversationId, user.LocalUserId, tokenCount));
        return Task.CompletedTask;
    }
}

internal sealed class FakeQuotaService : IChatQuotaService
{
    public Exception? Failure { get; set; }
    public int CheckCount { get; private set; }
    public int RecordCount { get; private set; }

    public Task CheckAndReserveAsync(ChatQuotaRequest request, CancellationToken cancellationToken = default)
    {
        CheckCount++;
        if (Failure != null)
        {
            throw Failure;
        }

        return Task.CompletedTask;
    }

    public Task RecordTokenUsageAsync(ChatQuotaRequest request, int actualTokens, CancellationToken cancellationToken = default)
    {
        RecordCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeQuotaUsageReader : IQuotaUsageReader
{
    public QuotaUsageReport Report { get; set; } = new(
        "20260621",
        [],
        new QuotaUsageTotals(0, 0, 0, 0, 0));

    public int CallCount { get; private set; }
    public string? LastDate { get; private set; }
    public int LastLimit { get; private set; }
    public string? LastUserId { get; private set; }

    public Task<QuotaUsageReport> GetDailyUsageAsync(
        string date,
        int limit,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastDate = date;
        LastLimit = limit;
        LastUserId = userId;
        return Task.FromResult(Report);
    }
}

internal sealed record FakeStoredMessage(
    string Role,
    string Content,
    string? ThinkingContent,
    IReadOnlyList<ChatAttachment> Attachments);

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
