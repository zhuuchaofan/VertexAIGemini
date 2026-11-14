using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VertexAI.Api;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Memory;

internal static class MemoryTests
{
    private static readonly AuthenticatedUser Me = new(Guid.NewGuid(), "me", "me@example.com");

    public static void RulesValidateAndBuildTheSystemText()
    {
        Assert.Equal("要记住的内容不能为空。", MemoryRules.Validate("   ").Error);
        Assert.NotNull(MemoryRules.Validate(new string('a', MemoryRules.MaxChars + 1)).Error);
        Assert.Equal("我在上海", MemoryRules.Validate("  我在上海 ").Text);

        Assert.Null(MemoryRules.ToSystemAddendum(UserMemory.Empty));
        var items = new[]
        {
            new MemoryItem("b", "喜欢简短的回答", new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)),
            new MemoryItem("a", "住在上海\n养了一只猫", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))
        };
        Assert.Null(MemoryRules.ToSystemAddendum(new UserMemory(false, items)));

        var text = MemoryRules.ToSystemAddendum(new UserMemory(true, items))!;
        Assert.True(text.IndexOf("住在上海 养了一只猫", StringComparison.Ordinal) < text.IndexOf("喜欢简短的回答", StringComparison.Ordinal));
        Assert.False(text.Contains('\n' + "养了一只猫"));

        Assert.Equal("预设", MemoryRules.Combine("预设", null));
        Assert.Equal("记忆", MemoryRules.Combine("", "记忆"));
        Assert.Equal("预设\n\n记忆", MemoryRules.Combine("预设", "记忆"));
    }

    public static void EndpointsAreTheCallersOwnAndEnforceTheLimits()
    {
        var store = new InMemoryUserMemoryStore();
        var anonymous = ResultTestHelpers.Execute(MemoryEndpoints.GetAsync(new DefaultHttpContext(), new FakeUserContext(null), store));
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);

        Assert.Equal(StatusCodes.Status400BadRequest, Add(store, " ").StatusCode);
        var added = Add(store, "喜欢猫");
        Assert.Equal(StatusCodes.Status200OK, added.StatusCode);
        var id = JsonDocument.Parse(added.Body).RootElement.GetProperty("item").GetProperty("id").GetString()!;

        for (var i = 1; i < MemoryRules.MaxItems; i++) Add(store, $"第 {i} 条");
        Assert.Equal(StatusCodes.Status409Conflict, Add(store, "再多一条").StatusCode);

        var removed = ResultTestHelpers.Execute(MemoryEndpoints.RemoveAsync(id, new DefaultHttpContext(), new FakeUserContext(Me), store));
        Assert.Equal(StatusCodes.Status204NoContent, removed.StatusCode);
        var again = ResultTestHelpers.Execute(MemoryEndpoints.RemoveAsync(id, new DefaultHttpContext(), new FakeUserContext(Me), store));
        Assert.Equal(StatusCodes.Status404NotFound, again.StatusCode);

        var off = ResultTestHelpers.Execute(MemoryEndpoints.SetEnabledAsync(new MemoryEndpoints.MemoryEnabledBody(false), new DefaultHttpContext(), new FakeUserContext(Me), store));
        Assert.False(JsonDocument.Parse(off.Body).RootElement.GetProperty("enabled").GetBoolean());

        // Someone else sees none of it.
        var other = new AuthenticatedUser(Guid.NewGuid(), "other", "other@example.com");
        var theirs = ResultTestHelpers.Execute(MemoryEndpoints.GetAsync(new DefaultHttpContext(), new FakeUserContext(other), store));
        Assert.Equal(0, JsonDocument.Parse(theirs.Body).RootElement.GetProperty("items").GetArrayLength());
    }

    public static void TheSystemPromptSentToTheProviderCarriesTheMemory()
    {
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n");
        var client = new OpenAICompatibleChatModelClient(
            new HttpClient(handler),
            new OpenAICompatibleProviderSettings
            {
                Enabled = true, ProviderId = "test", Name = "Test", ApiKey = "k",
                Endpoint = "https://provider.example/v1/chat/completions", ModelName = "test-model"
            });
        client.ConfigureAsync(new ChatSessionOptions(PresetId: "custom", CustomPrompt: "你是助手", SystemAddendum: "- 住在上海")).GetAwaiter().GetResult();
        foreach (var _ in client.StreamChatAsync(new ChatModelRequest("hi", [])).ToBlockingEnumerable()) { }

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var system = body.RootElement.GetProperty("messages")[0];
        Assert.Equal("system", system.GetProperty("role").GetString());
        Assert.Equal("你是助手\n\n- 住在上海", system.GetProperty("content").GetString());
        // The saved prompt is still the preset's alone: the memory is not written back into what the run records.
        Assert.Equal("你是助手", client.CurrentCustomPrompt);
    }

    private static ExecutedResult Add(IUserMemoryStore store, string text) =>
        ResultTestHelpers.Execute(MemoryEndpoints.AddAsync(new MemoryEndpoints.MemoryAddBody(text), new DefaultHttpContext(), new FakeUserContext(Me), store));
}

internal sealed class InMemoryUserMemoryStore : IUserMemoryStore
{
    private readonly Dictionary<string, UserMemory> _byUser = [];

    public Task<UserMemory> GetAsync(AuthenticatedUser user, CancellationToken cancellationToken) =>
        Task.FromResult(_byUser.GetValueOrDefault(user.FirebaseUid, UserMemory.Empty));

    public Task<MemoryItem?> AddAsync(AuthenticatedUser user, string text, CancellationToken cancellationToken)
    {
        var current = _byUser.GetValueOrDefault(user.FirebaseUid, UserMemory.Empty);
        if (current.Items.Count >= MemoryRules.MaxItems) return Task.FromResult<MemoryItem?>(null);
        var item = new MemoryItem(Guid.NewGuid().ToString("N"), text, DateTime.UtcNow);
        _byUser[user.FirebaseUid] = current with { Items = current.Items.Append(item).ToList() };
        return Task.FromResult<MemoryItem?>(item);
    }

    public Task<bool> RemoveAsync(AuthenticatedUser user, string id, CancellationToken cancellationToken)
    {
        var current = _byUser.GetValueOrDefault(user.FirebaseUid, UserMemory.Empty);
        if (current.Items.All(item => item.Id != id)) return Task.FromResult(false);
        _byUser[user.FirebaseUid] = current with { Items = current.Items.Where(item => item.Id != id).ToList() };
        return Task.FromResult(true);
    }

    public Task SetEnabledAsync(AuthenticatedUser user, bool enabled, CancellationToken cancellationToken)
    {
        _byUser[user.FirebaseUid] = _byUser.GetValueOrDefault(user.FirebaseUid, UserMemory.Empty) with { Enabled = enabled };
        return Task.CompletedTask;
    }
}
