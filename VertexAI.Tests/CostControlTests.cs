using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VertexAI.Api;
using VertexAI.Configuration;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Quota;

internal static class CostControlTests
{
    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static AuthenticatedUser User(
        string uid = "uid-1",
        string? email = "user@example.com",
        bool verified = true,
        bool admin = false) =>
        new(Guid.NewGuid(), uid, email, admin, verified);

    // ---- accounting ----------------------------------------------------------------------

    public static void SettlementDoesNotDoubleCountTokens()
    {
        var request = new ChatQuotaRequest(User(), 1, EstimatedTokens: 1000, SearchCount: 0, AttachmentBytes: 0);
        var reserved = new UsageTotals(0, 0, 0, 0, 0).Reserve(request);
        Assert.Equal(1000L, reserved.TokensInUse);

        var (estimatedDelta, actualDelta) = QuotaAccounting.SettleDeltas(request.EstimatedTokens, 800);
        var settled = reserved with
        {
            EstimatedTokens = reserved.EstimatedTokens + (int)estimatedDelta,
            ActualTokens = reserved.ActualTokens + (int)actualDelta
        };
        Assert.Equal(800L, settled.TokensInUse);

        // A request that never reached the model costs no tokens once settled.
        var (failedEstimate, failedActual) = QuotaAccounting.SettleDeltas(1000, 0);
        Assert.Equal(-1000L, failedEstimate);
        Assert.Equal(0L, failedActual);
    }

    public static void EnforcesUserAndGlobalLimits()
    {
        var settings = new QuotaSettings
        {
            DailyRequestLimit = 2,
            DailyTokenLimit = 100,
            DailySearchLimit = 1,
            DailyAttachmentBytesLimit = 10,
            GlobalDailyRequestLimit = 3,
            GlobalDailyTokenLimit = 1000,
            GlobalDailySearchLimit = 0
        };

        Assert.Null(QuotaAccounting.FindUserViolation(new UsageTotals(2, 50, 50, 1, 10), settings));
        Assert.Contains("request", QuotaAccounting.FindUserViolation(new UsageTotals(3, 0, 0, 0, 0), settings) ?? "");
        Assert.Contains("token", QuotaAccounting.FindUserViolation(new UsageTotals(1, 60, 50, 0, 0), settings) ?? "");
        Assert.Contains("search", QuotaAccounting.FindUserViolation(new UsageTotals(1, 0, 0, 2, 0), settings) ?? "");
        Assert.Contains("attachment", QuotaAccounting.FindUserViolation(new UsageTotals(1, 0, 0, 0, 11), settings) ?? "");

        Assert.Null(QuotaAccounting.FindGlobalViolation(new UsageTotals(3, 0, 1000, 99, 0), settings));
        Assert.Contains("Global budget", QuotaAccounting.FindGlobalViolation(new UsageTotals(4, 0, 0, 0, 0), settings) ?? "");
        Assert.Contains("Global budget", QuotaAccounting.FindGlobalViolation(new UsageTotals(1, 0, 1001, 0, 0), settings) ?? "");
        Assert.False(new QuotaSettings { GlobalDailyRequestLimit = 0, GlobalDailyTokenLimit = 0, GlobalDailySearchLimit = 0 }.GlobalLimitsEnabled);
    }

    public static void EstimatesIgnoreClientSizeAndAvoidBase64Inflation()
    {
        var pdfBytes = new byte[512 * 1024];
        var pdf = new ChatAttachment(Convert.ToBase64String(pdfBytes), "application/pdf", "a.pdf", SizeBytes: 1);

        Assert.Equal((long)pdfBytes.Length, QuotaEstimator.EstimateAttachmentBytes(pdf));
        var tokens = QuotaEstimator.EstimateTokens("hello", [pdf]);
        Assert.True(tokens > 1000 && tokens < 20000);

        var image = new ChatAttachment(Convert.ToBase64String(new byte[700 * 1024]), "image/webp", "i.webp");
        Assert.True(QuotaEstimator.EstimateTokens("", [image]) < 5000);
        Assert.Equal(0L, QuotaEstimator.EstimateAttachmentBytes(new ChatAttachment(null, "image/png", null, SizeBytes: 999)));
    }

    // ---- concurrency ---------------------------------------------------------------------

    public static void ConcurrencyLimiterBoundsUsersAndReleasesOnce()
    {
        var limiter = new ChatConcurrencyLimiter(Options.Create(new QuotaSettings
        {
            MaxConcurrentChatsPerUser = 2,
            MaxConcurrentChatsPerInstance = 3
        }));

        var a1 = limiter.TryEnter("a");
        var a2 = limiter.TryEnter("a");
        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.Null(limiter.TryEnter("a"));

        var b1 = limiter.TryEnter("b");
        Assert.NotNull(b1);
        Assert.Null(limiter.TryEnter("c")); // instance cap of 3 reached

        a1!.Dispose();
        a1.Dispose(); // double dispose must not free a second slot
        Assert.NotNull(limiter.TryEnter("c"));
        Assert.Null(limiter.TryEnter("d"));
    }

    public static void OrchestratorRejectsWhenTooManyConcurrentChats()
    {
        var limiter = new ChatConcurrencyLimiter(Options.Create(new QuotaSettings { MaxConcurrentChatsPerUser = 1 }));
        var user = User();
        var held = limiter.TryEnter(user.FirebaseUid!);
        Assert.NotNull(held);

        var quota = new FakeQuotaService();
        var model = new FakeChatModelClient { Chunks = [new ChatChunk { Text = "x" }] };
        var orchestrator = new ChatOrchestrator(
            new FakeProviderCatalog(model),
            new FakeConversationStore(),
            NullLogger<ChatOrchestrator>.Instance,
            quota: quota,
            concurrency: limiter);

        var result = Run(orchestrator.SendAsync(new ChatSendRequest(null, user, "hi", []), _ => Task.CompletedTask));

        Assert.False(result.Succeeded);
        Assert.Contains("多个对话", result.ErrorMessage ?? "");
        Assert.Equal(0, quota.CheckCount); // rejected before any quota or model work
        held!.Dispose();
    }

    public static void OrchestratorSettlesReservationOnEveryPath()
    {
        var user = User();

        // success
        var quota = new FakeQuotaService();
        var model = new FakeChatModelClient { CurrentTokenCount = 10, Chunks = [new ChatChunk { Text = "ok" }] };
        var orchestrator = new ChatOrchestrator(new FakeProviderCatalog(model), new FakeConversationStore(), NullLogger<ChatOrchestrator>.Instance, quota: quota);
        Run(orchestrator.SendAsync(new ChatSendRequest(null, user, "hi", []), _ => Task.CompletedTask));
        Assert.Equal(1, quota.CheckCount);
        Assert.Equal(1, quota.RecordCount);

        // failure before the model is created (unknown model): still settled exactly once
        quota = new FakeQuotaService();
        model = new FakeChatModelClient { ModelOptions = [new ChatModelOption { Name = "a", ModelName = "a" }] };
        orchestrator = new ChatOrchestrator(new FakeProviderCatalog(model), new FakeConversationStore(), NullLogger<ChatOrchestrator>.Instance, quota: quota);
        var result = Run(orchestrator.SendAsync(
            new ChatSendRequest(null, user, "hi", [], Options: new ChatSessionOptions("fake", "missing")),
            _ => Task.CompletedTask));
        Assert.False(result.Succeeded);
        Assert.Equal(1, quota.RecordCount);

        // quota rejection: nothing was reserved, so nothing is settled
        quota = new FakeQuotaService { Failure = new InvalidOperationException("Global budget exceeded (requests).") };
        orchestrator = new ChatOrchestrator(new FakeProviderCatalog(model), new FakeConversationStore(), NullLogger<ChatOrchestrator>.Instance, quota: quota);
        result = Run(orchestrator.SendAsync(new ChatSendRequest(null, user, "hi", []), _ => Task.CompletedTask));
        Assert.Contains("站点", result.ErrorMessage ?? "");
        Assert.Equal(0, quota.RecordCount);
    }

    // ---- access policy -------------------------------------------------------------------

    private static AccessPolicy Policy(Action<AccessSettings> configure)
    {
        var settings = new AccessSettings();
        configure(settings);
        return new AccessPolicy(Options.Create(settings));
    }

    public static void AccessPolicyDefaultsToOpen()
    {
        Assert.True(Policy(_ => { }).IsAllowed(User(verified: false), out var reason));
        Assert.Null(reason);
    }

    public static void AccessPolicyRequiresVerifiedEmailWhenConfigured()
    {
        var policy = Policy(s => s.RequireVerifiedEmail = true);
        Assert.False(policy.IsAllowed(User(verified: false), out var reason));
        Assert.Contains("验证邮箱", reason ?? "");
        Assert.True(policy.IsAllowed(User(verified: true), out _));
        Assert.True(policy.IsAllowed(User(verified: false, admin: true), out _));
    }

    public static void AccessPolicyAllowListNeedsVerifiedEmail()
    {
        var policy = Policy(s =>
        {
            s.AllowedEmails.Add("Owner@Example.com");
            s.AllowedEmailDomains.Add("@team.dev");
            s.AllowedUids.Add("trusted-uid");
        });

        Assert.True(policy.IsAllowed(User(email: "owner@example.com", verified: true), out _));
        Assert.True(policy.IsAllowed(User(email: "x@team.dev", verified: true), out _));
        Assert.True(policy.IsAllowed(User(uid: "trusted-uid", email: null, verified: false), out _));

        // An unverified sign-up using an allow-listed address must NOT get in.
        Assert.False(policy.IsAllowed(User(email: "owner@example.com", verified: false), out var reason));
        Assert.Contains("未被授权", reason ?? "");
        Assert.False(policy.IsAllowed(User(email: "stranger@evil.com", verified: true), out _));
        Assert.False(policy.IsAllowed(User(email: null, verified: true), out _));
    }

    public static void AccessMiddlewareBlocksDeniedUsersOnlyOnApiPaths()
    {
        var policy = Policy(s => s.AllowedEmails.Add("owner@example.com"));
        var denied = new FakeUserContext(User(email: "stranger@evil.com"));

        var blocked = Invoke("/api/chat/stream", denied, policy);
        Assert.Equal(StatusCodes.Status403Forbidden, blocked.StatusCode);
        Assert.False(blocked.NextCalled);

        var config = Invoke("/api/workspace/config", denied, policy);
        Assert.True(config.NextCalled);

        var anonymous = Invoke("/api/chat/stream", new FakeUserContext(null), policy);
        Assert.True(anonymous.NextCalled); // endpoints answer 401 themselves

        var allowed = Invoke("/api/conversations", new FakeUserContext(User(email: "owner@example.com")), policy);
        Assert.True(allowed.NextCalled);

        var health = Invoke("/health/live", denied, policy);
        Assert.True(health.NextCalled);
    }

    private static (int StatusCode, bool NextCalled) Invoke(string path, IUserContext users, AccessPolicy policy)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = new AccessPolicyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        middleware.InvokeAsync(context, users, policy).GetAwaiter().GetResult();
        return (context.Response.StatusCode, nextCalled);
    }
}

internal static class AccessSettingsTests
{
    public static void ExpandsCsvLists()
    {
        var settings = new AccessSettings
        {
            AllowedEmailsCsv = " a@x.com , b@y.com ,, ",
            AllowedEmailDomainsCsv = "team.dev",
            AllowedUids = ["u1"],
            AllowedUidsCsv = "u2"
        };

        Assert.Equal(2, settings.Emails().Count);
        Assert.Equal("a@x.com", settings.Emails()[0]);
        Assert.Equal(2, settings.Uids().Count);
        Assert.True(settings.HasAllowList);
        Assert.False(new AccessSettings { AllowedEmailsCsv = " , " }.HasAllowList);
    }
}

internal static class FirebaseUserContextTests
{
    // A throw-away key generated at run time: nothing here is a real credential, and malformed tokens
    // are rejected before the verifier would ever contact Google.
    private static FirebaseAdmin.Auth.FirebaseAuth NewAuth()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "unit-test",
            private_key_id = "k1",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            client_email = "unit@unit-test.iam.gserviceaccount.com",
            client_id = "1",
            token_uri = "https://oauth2.googleapis.com/token"
        });
        var app = FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
        {
            Credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromJson(json),
            ProjectId = "unit-test"
        }, $"unit-{Guid.NewGuid():N}");
        return FirebaseAdmin.Auth.FirebaseAuth.GetAuth(app);
    }

    public static void MalformedBearerTokensAreAnonymousNotServerErrors()
    {
        var context = new FirebaseUserContext(NewAuth(), NullLogger<FirebaseUserContext>.Instance);

        foreach (var token in new[] { "not.a.jwt", "garbage", "a.b.c", "....", "%%%.%%%.%%%" })
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = $"Bearer {token}";
            var user = context.GetCurrentUserAsync(http).GetAwaiter().GetResult();
            Assert.Null(user);
        }
    }

    public static void MissingOrNonBearerHeaderIsAnonymous()
    {
        var context = new FirebaseUserContext(NewAuth(), NullLogger<FirebaseUserContext>.Instance);

        Assert.Null(context.GetCurrentUserAsync(new DefaultHttpContext()).GetAwaiter().GetResult());

        var basic = new DefaultHttpContext();
        basic.Request.Headers.Authorization = "Basic abc";
        Assert.Null(context.GetCurrentUserAsync(basic).GetAwaiter().GetResult());
    }
}
