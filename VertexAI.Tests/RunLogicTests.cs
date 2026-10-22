using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>Pure run logic; needs no emulator.</summary>
internal static class RunLogicTests
{
    private static int _tick;

    private static Message M(string role, string content, string? runId = null, string? status = null) => new()
    {
        Role = role,
        Content = content,
        RunId = runId,
        Status = status,
        CreatedAt = DateTime.UtcNow.AddSeconds(_tick++)
    };

    private static string[] Texts(IReadOnlyList<ChatHistoryEntry> history) => history.Select(h => h.Content).ToArray();

    public static void TheSelectorPairsQuestionsWithRepliesAndDropsOrphans()
    {
        var history = RunContextSelector.Select(
        [
            M("user", "q1"), M("model", "a1"),
            M("user", "orphan"),                 // its run failed: never answered
            M("user", "q3"), M("model", "a3")
        ], maxTurns: 10);
        Assert.SequenceEqual(new[] { "q1", "a1", "q3", "a3" }, Texts(history));
    }

    public static void TheQuestionBeingAnsweredNowIsNeverContext()
    {
        var history = RunContextSelector.Select([M("user", "q1"), M("model", "a1"), M("user", "current")], maxTurns: 10);
        Assert.SequenceEqual(new[] { "q1", "a1" }, Texts(history));
    }

    public static void APartialReplyCountsButAnEmptyOneDoesNot()
    {
        var history = RunContextSelector.Select(
        [
            M("user", "q1", "r1"), M("model", "half an ans", "r1", "partial"),
            M("user", "q2", "r2"), M("model", "   ", "r2", "partial"),
            M("user", "q3", "r3"), M("model", "a3", "r3", "final")
        ], maxTurns: 10);
        Assert.SequenceEqual(new[] { "q1", "half an ans", "q3", "a3" }, Texts(history));
    }

    public static void OnlyTheNewestTurnsAreKept()
    {
        var messages = Enumerable.Range(1, 5).SelectMany(i => new[] { M("user", $"q{i}"), M("model", $"a{i}") }).ToList();
        Assert.SequenceEqual(new[] { "q4", "a4", "q5", "a5" }, Texts(RunContextSelector.Select(messages, maxTurns: 2)));
    }

    public static void MessagesFromBeforeRunsExistAreTreatedTheSame()
    {
        // No RunId and no Status at all, as in every conversation created before this feature.
        var history = RunContextSelector.Select([M("user", "old q"), M("model", "old a"), M("model", "stray reply")], maxTurns: 10);
        Assert.SequenceEqual(new[] { "old q", "old a" }, Texts(history));
    }

    public static void ThinkingTextTravelsWithTheReply()
    {
        var reply = M("model", "answer");
        reply.ThinkingContent = "reasoning";
        var history = RunContextSelector.Select([M("user", "q"), reply], maxTurns: 10);
        Assert.Equal("reasoning", history[1].ThinkingContent!);
    }

    public static void RunIdsAreDeterministicAndFollowRfc4122Version5()
    {
        // The published RFC 4122 example: v5 of "python.org" in the DNS namespace.
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"), RunIds.CreateV5(dns, "python.org"));

        var runId = Guid.NewGuid().ToString("D");
        Assert.Equal(RunIds.UserMessageId(runId), RunIds.UserMessageId(runId));
        Assert.True(RunIds.UserMessageId(runId) != RunIds.AssistantMessageId(runId));
        Assert.True(RunIds.UserMessageId(runId) != RunIds.UserMessageId(Guid.NewGuid().ToString("D")));
    }

    public static void OnlyCanonicalUuidsAreAcceptedAsRunIds()
    {
        var valid = Guid.NewGuid();
        Assert.True(RunIds.TryParseRunId(valid.ToString("D").ToUpperInvariant(), out var normalized));
        Assert.Equal(valid.ToString("D"), normalized);
        foreach (var bad in new[] { null, "", "nope", valid.ToString("N"), "{" + valid + "}", valid + "x", "../" + valid })
            Assert.False(RunIds.TryParseRunId(bad, out _));
    }

    public static void TheFingerprintChangesWithAnythingThatDefinesTheRequest()
    {
        var conversation = Guid.NewGuid();
        var requested = new RunRequested("p", "m", "default", null, "auto", null, null, null, null);
        var baseline = RunFingerprint.Compute(conversation, "hello", requested);

        Assert.Equal(baseline, RunFingerprint.Compute(conversation, "hello", requested));
        Assert.True(baseline != RunFingerprint.Compute(conversation, "hello!", requested));
        Assert.True(baseline != RunFingerprint.Compute(Guid.NewGuid(), "hello", requested));
        Assert.True(baseline != RunFingerprint.Compute(conversation, "hello", requested with { ModelName = "other" }));
        Assert.True(baseline != RunFingerprint.Compute(conversation, "hello", requested with { SearchMode = "off" }));
        Assert.True(baseline != RunFingerprint.Compute(conversation, "hello", requested with { Advanced = new ChatAdvancedOptions(Temperature: 0.5) }));
    }

    public static void ProviderErrorsAreClassifiedWithoutLeakingInternals()
    {
        Assert.Equal("safety", RunErrors.Classify(new InvalidOperationException("Gemini response was blocked by safety settings: X")).Code);
        Assert.Equal("empty_response", RunErrors.Classify(new InvalidOperationException("Gemini response was empty.")).Code);
        Assert.Equal("provider_error", RunErrors.Classify(new HttpRequestException("boom")).Code);
        Assert.True(RunErrors.Classify(new HttpRequestException("boom")).Retryable);
        var unknown = RunErrors.Classify(new InvalidOperationException("secret stack detail"));
        Assert.Equal("internal", unknown.Code);
        Assert.False(unknown.Message.Contains("secret"));
    }

    public static void FailuresSayWhatHappenedWhatToDoAndKeepDetailsForAdmins()
    {
        // A filter verdict is not "empty": the message names the filter and the finish reason, and offers a way out.
        var filtered = RunErrors.ForEmptyResponse(new RunErrorContext("grok-vertex", "xai/grok-4.6", "content_filter"));
        Assert.Equal("safety", filtered.Code);
        Assert.True(filtered.Retryable);
        Assert.Contains("内容过滤", filtered.Message);
        Assert.Contains("content_filter", filtered.Message);
        Assert.Contains("重试", filtered.Hint!);
        Assert.Contains("grok-vertex", filtered.Detail!);
        Assert.False(string.IsNullOrWhiteSpace(filtered.AdminHint));

        // A genuinely empty answer, and one that ran out of length, are told apart.
        Assert.Equal("empty_response", RunErrors.ForEmptyResponse(new RunErrorContext(null, null, "STOP")).Code);
        Assert.Contains("长度", RunErrors.ForEmptyResponse(new RunErrorContext(null, null, "length")).Message);

        // Each daily limit is named, with the setting that raises it and the day boundary.
        foreach (var (raw, what, setting) in new[]
        {
            ("Daily request quota exceeded.", "请求次数", "DailyRequestLimit"),
            ("Daily token quota exceeded.", "Token", "DailyTokenLimit"),
            ("Daily search quota exceeded.", "联网搜索", "DailySearchLimit"),
            ("Daily attachment quota exceeded.", "附件", "DailyAttachmentBytesLimit")
        })
        {
            var quota = RunErrors.ForQuota(raw);
            Assert.Equal("quota", quota.Code);
            Assert.Contains(what, quota.Message);
            Assert.Contains("8 点", quota.Hint!);
            Assert.Contains($"Quota__{setting}", quota.AdminHint!);
            Assert.False(quota.Message.Contains("exceeded"));
        }
        Assert.Equal("quota", RunErrors.Classify(new InvalidOperationException("Daily token quota exceeded.")).Code);

        // Provider and infrastructure text stays out of what everyone sees.
        var permission = RunErrors.Classify(new InvalidOperationException("IAM_PERMISSION_DENIED on aiplatform.googleapis.com project 123"));
        Assert.False(permission.Message.Contains("Cloud Run") || permission.Message.Contains("IAM"));
        Assert.Contains("aiplatform", permission.AdminHint!);
        var stripped = permission.ForClient(isAdmin: false);
        Assert.Equal(null, stripped.Detail);
        Assert.Equal(null, stripped.AdminHint);
        Assert.Equal(permission.Message, stripped.Message);
        Assert.Equal(permission.Hint, stripped.Hint);
        Assert.Equal(permission.Detail, permission.ForClient(isAdmin: true).Detail);

        // Details are cut to a sane length.
        var long_ = RunErrors.Classify(new InvalidOperationException(new string('x', 5000)));
        Assert.True(long_.Detail!.Length < 1000);
    }
}
