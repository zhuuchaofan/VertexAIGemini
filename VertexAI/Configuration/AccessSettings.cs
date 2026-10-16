namespace VertexAI.Configuration;

/// <summary>
/// Backend-enforced access policy. With all lists empty and RequireVerifiedEmail=false the site is
/// open to anyone who can sign in with Firebase (the pre-existing behaviour).
/// </summary>
public sealed class AccessSettings
{
    /// <summary>Firebase UIDs that are always allowed (safe even for unverified emails).</summary>
    public List<string> AllowedUids { get; set; } = [];

    /// <summary>Allowed e-mail addresses. Matching requires a verified e-mail.</summary>
    public List<string> AllowedEmails { get; set; } = [];

    /// <summary>Allowed e-mail domains, e.g. "example.com". Matching requires a verified e-mail.</summary>
    public List<string> AllowedEmailDomains { get; set; } = [];

    /// <summary>Reject accounts whose e-mail is not verified (Google sign-in is always verified).</summary>
    public bool RequireVerifiedEmail { get; set; }

    /// <summary>
    /// Comma-separated variants of the lists above. They exist so Cloud Run can configure the allow-list
    /// with one plain environment variable (Access__AllowedEmailsCsv=a@x.com,b@y.com).
    /// </summary>
    public string? AllowedUidsCsv { get; set; }
    public string? AllowedEmailsCsv { get; set; }
    public string? AllowedEmailDomainsCsv { get; set; }

    public IReadOnlyList<string> Uids() => Merge(AllowedUids, AllowedUidsCsv);
    public IReadOnlyList<string> Emails() => Merge(AllowedEmails, AllowedEmailsCsv);
    public IReadOnlyList<string> Domains() => Merge(AllowedEmailDomains, AllowedEmailDomainsCsv);

    public bool HasAllowList => Uids().Count > 0 || Emails().Count > 0 || Domains().Count > 0;

    private static IReadOnlyList<string> Merge(IEnumerable<string> list, string? csv) =>
        list.Concat((csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToList();
}
