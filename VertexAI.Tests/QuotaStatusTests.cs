using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VertexAI.Api;
using VertexAI.Configuration;
using VertexAI.Services.Auth;
using VertexAI.Services.Quota;

internal static class QuotaStatusTests
{
    private static readonly DateTime Noon = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    public static void CountsEstimatedAndActualTokensAndResetsAtMidnight()
    {
        var settings = new QuotaSettings { DailyRequestLimit = 10, DailyTokenLimit = 1000, DailySearchLimit = 5, DailyAttachmentBytesLimit = 4096 };
        var usage = new QuotaUsageEntry("u", "20260621", Requests: 3, EstimatedTokens: 200, ActualTokens: 150, Searches: 1, AttachmentBytes: 2048, UpdatedAt: null);

        var status = QuotaStatus.From(usage, settings, Noon);

        Assert.True(status.Limited);
        Assert.Equal("20260621", status.Date);
        Assert.Equal(new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc), status.ResetsAtUtc);
        Assert.Equal(new QuotaMeter(3, 10), status.Requests);
        Assert.Equal(new QuotaMeter(350, 1000), status.Tokens);
        Assert.Equal(new QuotaMeter(1, 5), status.Searches);
        Assert.Equal(new QuotaMeter(2048, 4096), status.AttachmentBytes);
    }

    public static void NothingUsedYetStartsAtZero()
    {
        var status = QuotaStatus.From(null, new QuotaSettings(), Noon);

        Assert.Equal(0L, status.Requests.Used);
        Assert.Equal((long)new QuotaSettings().DailyRequestLimit, status.Requests.Limit);
        Assert.Equal(0L, status.Tokens.Used);
    }

    public static void EndpointRequiresSignInAndReturnsOnlyTheCallersAllowance()
    {
        var reader = new FakeQuotaStatusReader(QuotaStatus.From(null, new QuotaSettings(), Noon));

        var anonymous = ResultTestHelpers.Execute(QuotaEndpoints.GetQuotaAsync(new DefaultHttpContext(), new FakeUserContext(null), reader));
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, reader.Calls);

        var user = new AuthenticatedUser(Guid.NewGuid(), "firebase-me", "me@example.com");
        var signedIn = ResultTestHelpers.Execute(QuotaEndpoints.GetQuotaAsync(new DefaultHttpContext(), new FakeUserContext(user), reader));
        Assert.Equal(StatusCodes.Status200OK, signedIn.StatusCode);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(user, reader.LastUser);

        using var body = JsonDocument.Parse(signedIn.Body);
        Assert.True(body.RootElement.GetProperty("limited").GetBoolean());
        Assert.Equal(new QuotaSettings().DailyRequestLimit, body.RootElement.GetProperty("requests").GetProperty("limit").GetInt32());
    }

    private sealed class FakeQuotaStatusReader(QuotaStatus status) : IQuotaStatusReader
    {
        public int Calls { get; private set; }
        public AuthenticatedUser? LastUser { get; private set; }

        public Task<QuotaStatus> GetAsync(AuthenticatedUser user, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUser = user;
            return Task.FromResult(status);
        }
    }
}
