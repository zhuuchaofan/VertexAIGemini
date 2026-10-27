using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Services.Auth;

/// <summary>Who may use the site once an allow-list is configured; with no lists the site stays open to every signed-in account.</summary>
internal static class AccessPolicyTests
{
    private static AccessPolicy Policy(AccessSettings settings) => new(Options.Create(settings));

    private static AuthenticatedUser User(string email, bool verified = true, bool admin = false) =>
        new(Guid.NewGuid(), "uid-" + email, email, admin, verified);

    public static void AnOpenSiteAllowsEveryone()
    {
        Assert.True(Policy(new AccessSettings()).IsAllowed(User("a@x.com", verified: false), out _));
    }

    public static void AnAllowListAdmitsOnlyVerifiedMatches()
    {
        var policy = Policy(new AccessSettings { AllowedEmailsCsv = "a@x.com", AllowedEmailDomains = ["corp.com"] });
        Assert.True(policy.IsAllowed(User("A@x.com"), out _));
        Assert.True(policy.IsAllowed(User("b@corp.com"), out _));
        Assert.False(policy.IsAllowed(User("c@other.com"), out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        // Anyone can register an address they do not own until it is verified.
        Assert.False(policy.IsAllowed(User("a@x.com", verified: false), out _));
    }

    public static void AdminsAreNeverLockedOut()
    {
        var policy = Policy(new AccessSettings { AllowedEmailsCsv = "a@x.com", RequireVerifiedEmail = true });
        Assert.True(policy.IsAllowed(User("boss@other.com", verified: false, admin: true), out _));
    }
}
