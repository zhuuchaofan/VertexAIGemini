using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VertexAI.Api;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

internal static class PinTests
{
    private static AuthenticatedUser NewUser(string name) => new(Guid.NewGuid(), name, $"{name}@example.com");

    private static Guid Add(FakeConversationStore store, AuthenticatedUser owner, string title, DateTime? pinnedAt = null)
    {
        var id = Guid.NewGuid();
        store.Conversations[id] = new Conversation { Id = id, UserId = owner.LocalUserId, Title = title, PinnedAt = pinnedAt };
        return id;
    }

    private static int Pin(FakeConversationStore store, AuthenticatedUser? user, Guid id, bool pinned) =>
        ResultTestHelpers.Execute(ConversationEndpoints.SetPinnedAsync(
            id, new ConversationEndpoints.ConversationPinRequest(pinned), new DefaultHttpContext(), new FakeUserContext(user), store)).StatusCode;

    public static void OnlyTheOwnerCanPinAndUnpinningWorks()
    {
        var store = new FakeConversationStore();
        var owner = NewUser("owner");
        var id = Add(store, owner, "mine");

        Assert.Equal(StatusCodes.Status401Unauthorized, Pin(store, null, id, true));
        Assert.Equal(StatusCodes.Status404NotFound, Pin(store, NewUser("other"), id, true));
        Assert.Null(store.Conversations[id].PinnedAt);

        Assert.Equal(StatusCodes.Status204NoContent, Pin(store, owner, id, true));
        Assert.NotNull(store.Conversations[id].PinnedAt);

        Assert.Equal(StatusCodes.Status204NoContent, Pin(store, owner, id, false));
        Assert.Null(store.Conversations[id].PinnedAt);
    }

    public static void TheNumberOfPinnedConversationsIsCapped()
    {
        var store = new FakeConversationStore();
        var owner = NewUser("owner");
        var pinned = Enumerable.Range(0, ConversationEndpoints.MaxPinned)
            .Select(i => Add(store, owner, $"p{i}", DateTime.UtcNow.AddMinutes(-i)))
            .ToList();
        var extra = Add(store, owner, "one more");

        Assert.Equal(StatusCodes.Status409Conflict, Pin(store, owner, extra, true));
        Assert.Null(store.Conversations[extra].PinnedAt);

        // Pinning one that is already pinned, and unpinning, still work at the cap.
        Assert.Equal(StatusCodes.Status204NoContent, Pin(store, owner, pinned[0], true));
        Assert.Equal(StatusCodes.Status204NoContent, Pin(store, owner, pinned[1], false));
        Assert.Equal(StatusCodes.Status204NoContent, Pin(store, owner, extra, true));
    }

    public static void TheFirstPageCarriesThePinnedConversations()
    {
        var store = new FakeConversationStore();
        var owner = NewUser("owner");
        Add(store, owner, "older pin", DateTime.UtcNow.AddHours(-2));
        Add(store, owner, "newer pin", DateTime.UtcNow.AddHours(-1));
        Add(store, NewUser("other"), "not mine", DateTime.UtcNow);

        var first = ResultTestHelpers.Execute(ConversationEndpoints.ListAsync(null, null, null, null, new DefaultHttpContext(), new FakeUserContext(owner), store));
        using var body = JsonDocument.Parse(first.Body);
        var titles = body.RootElement.GetProperty("pinned").EnumerateArray().Select(item => item.GetProperty("title").GetString()).ToList();
        Assert.Equal(2, titles.Count);
        Assert.Equal("newer pin", titles[0]);
        Assert.True(body.RootElement.GetProperty("pinned")[0].GetProperty("pinned").GetBoolean());

        var later = ResultTestHelpers.Execute(ConversationEndpoints.ListAsync(null, null, "2026-06-21T00:00:00.0000000Z", null, new DefaultHttpContext(), new FakeUserContext(owner), store));
        using var laterBody = JsonDocument.Parse(later.Body);
        Assert.True(laterBody.RootElement.GetProperty("pinned").ValueKind == JsonValueKind.Null);
    }
}
