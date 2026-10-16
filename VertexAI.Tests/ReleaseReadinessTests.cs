using Google.GenAI.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VertexAI.Api;
using VertexAI.Configuration;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

internal static class ReleaseReadinessTests
{
    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static AuthenticatedUser User(string uid = "uid-r", string? email = "u@example.com", bool verified = true) =>
        new(Guid.NewGuid(), uid, email, false, verified);

    private static ChatOrchestrator Orchestrator(FakeChatModelClient model, FakeQuotaService quota, FakeConversationStore? store = null) =>
        new(new FakeProviderCatalog(model), store ?? new FakeConversationStore(), NullLogger<ChatOrchestrator>.Instance, quota: quota);

    public static void ReservationAndSettlementShareTheDayKey()
    {
        var quota = new FakeQuotaService();
        var model = new FakeChatModelClient { Chunks = [new ChatChunk { Text = "ok" }] };

        Run(Orchestrator(model, quota).SendAsync(new ChatSendRequest(null, User(), "hi", []), _ => Task.CompletedTask));

        var reserved = quota.Reserved.Single();
        Assert.True(!string.IsNullOrEmpty(reserved.DateKey) && reserved.DateKey!.Length == 8);
        // The very same request object (and therefore day) is used to settle, even after UTC midnight.
        Assert.Equal(reserved.DateKey, quota.Settled.Single().Request.DateKey);
    }

    public static void CancelledGenerationIsNotFree()
    {
        using var cts = new CancellationTokenSource();
        var quota = new FakeQuotaService();
        var model = new FakeChatModelClient { CurrentTokenCount = 0, Chunks = [new ChatChunk { Text = new string('x', 4000) }, new ChatChunk { Text = "more" }] };

        var result = Run(Orchestrator(model, quota).SendAsync(
            new ChatSendRequest(null, User(), "hello world", []),
            _ =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            },
            cts.Token));

        Assert.Equal(ChatStatuses.Cancelled, result.Status);
        var (request, actual) = quota.Settled.Single();
        // 4000 produced chars is ~1000 tokens: the settled amount must cover estimate + output, not 0.
        Assert.True(actual >= request.EstimatedTokens + 1000);
    }

    public static void EarlyFailureIsNotChargedTokens()
    {
        var quota = new FakeQuotaService();
        var model = new FakeChatModelClient { ModelOptions = [new ChatModelOption { Name = "a", ModelName = "a" }] };

        var result = Run(Orchestrator(model, quota).SendAsync(
            new ChatSendRequest(null, User(), "hi", [], Options: new ChatSessionOptions("fake", "not-a-model")),
            _ => Task.CompletedTask));

        Assert.False(result.Succeeded);
        Assert.Equal(0, quota.Settled.Single().ActualTokens);
    }

    public static void DisabledThinkingUsesLowestLevelNotBudget()
    {
        ChatThinkingConfig Config(params string[] levels) => new()
        {
            Kind = "gemini-level",
            Default = levels[0],
            Options = levels.Select(level => new ChatThinkingOption { Value = level, Label = level }).ToList()
        };

        var flash = GeminiService.BuildThinkingConfig(Config("minimal", "low", "medium", "high"), true, null, null)!;
        Assert.Equal(ThinkingLevel.MINIMAL, flash.ThinkingLevel!.Value);
        Assert.True(flash.ThinkingBudget is null);
        Assert.True(flash.IncludeThoughts == false);

        var pro = GeminiService.BuildThinkingConfig(Config("low", "medium", "high"), true, null, null)!;
        Assert.Equal(ThinkingLevel.LOW, pro.ThinkingLevel!.Value); // Pro cannot go below LOW and cannot be switched off

        var legacy = GeminiService.BuildThinkingConfig(new ChatThinkingConfig { Kind = "gemini-budget", DefaultBudget = 1024 }, true, null, null)!;
        Assert.Equal(0, legacy.ThinkingBudget!.Value);

        var enabled = GeminiService.BuildThinkingConfig(Config("minimal", "low", "medium", "high"), false, "high", null)!;
        Assert.Equal(ThinkingLevel.HIGH, enabled.ThinkingLevel!.Value);
    }

    public static void EnvironmentVariablesBindToSettings()
    {
        var names = new[]
        {
            "Access__AllowedEmailsCsv", "Access__AllowedUidsCsv", "Access__RequireVerifiedEmail",
            "Quota__GlobalDailyRequestLimit", "Chat__MaxGenerationSeconds"
        };
        try
        {
            // Exactly the names scripts/lib/cloud-run-release.sh sets on the Cloud Run service.
            System.Environment.SetEnvironmentVariable("Access__AllowedEmailsCsv", "owner@example.com,second@example.com");
            System.Environment.SetEnvironmentVariable("Access__AllowedUidsCsv", "uid-trusted");
            System.Environment.SetEnvironmentVariable("Access__RequireVerifiedEmail", "true");
            System.Environment.SetEnvironmentVariable("Quota__GlobalDailyRequestLimit", "7");
            System.Environment.SetEnvironmentVariable("Chat__MaxGenerationSeconds", "111");

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
                .AddEnvironmentVariables()
                .Build();

            var access = new AccessSettings();
            configuration.GetSection("Access").Bind(access);
            var quota = new QuotaSettings();
            configuration.GetSection("Quota").Bind(quota);
            var chat = new ChatStreamSettings();
            configuration.GetSection("Chat").Bind(chat);

            Assert.Equal(7, quota.GlobalDailyRequestLimit);
            Assert.Equal(2_000_000, quota.GlobalDailyTokenLimit); // untouched default from appsettings.json
            Assert.Equal(111, chat.MaxGenerationSeconds);

            var policy = new AccessPolicy(Options.Create(access));
            Assert.True(policy.IsAllowed(User(email: "OWNER@example.com"), out _));
            Assert.True(policy.IsAllowed(User(uid: "uid-trusted", email: null, verified: false), out _));
            Assert.False(policy.IsAllowed(User(email: "stranger@example.com"), out var reason));
            Assert.Contains("未被授权", reason ?? "");
            Assert.False(policy.IsAllowed(User(email: "owner@example.com", verified: false), out _));
        }
        finally
        {
            foreach (var name in names)
            {
                System.Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static int StatusFor(string path, AccessPolicy policy, AuthenticatedUser user)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var middleware = new AccessPolicyMiddleware(_ => Task.CompletedTask);
        middleware.InvokeAsync(context, new FakeUserContext(user), policy).GetAwaiter().GetResult();
        return context.Response.StatusCode;
    }

    private static AccessPolicy DenyingPolicy() =>
        new(Options.Create(new AccessSettings { AllowedEmailsCsv = "owner@example.com" }));

    public static void AccessPolicyPathMatchingIgnoresCase()
    {
        var stranger = User(email: "stranger@evil.com");
        foreach (var path in new[] { "/API/chat/stream", "/Api/Conversations", "/aPi/export/x/json" })
        {
            Assert.Equal(StatusCodes.Status403Forbidden, StatusFor(path, DenyingPolicy(), stranger));
        }
    }

    public static void AccessPolicyCoversAllApiRoutes()
    {
        // Every route group registered under /api must sit behind the policy except the public config.
        var stranger = User(email: "stranger@evil.com");
        foreach (var path in new[]
        {
            "/api/auth/status", "/api/chat/stream", "/api/conversations", "/api/conversations/3f2b/title",
            "/api/export/3f2b/markdown", "/api/admin/quotas/daily"
        })
        {
            Assert.Equal(StatusCodes.Status403Forbidden, StatusFor(path, DenyingPolicy(), stranger));
        }

        Assert.Equal(StatusCodes.Status200OK, StatusFor("/api/workspace/config", DenyingPolicy(), stranger));
        Assert.Equal(StatusCodes.Status200OK, StatusFor("/health/ready", DenyingPolicy(), stranger));
    }
}
