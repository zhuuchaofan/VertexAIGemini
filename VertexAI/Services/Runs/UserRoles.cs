using Google.Cloud.Firestore;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>A named system prompt a person keeps in their own account.</summary>
public sealed record UserRole(string Id, string Name, string Prompt, DateTime? UpdatedAt = null);

public interface IUserRoleStore
{
    Task<IReadOnlyList<UserRole>> ListAsync(AuthenticatedUser user, CancellationToken cancellationToken);

    /// <summary>Returns null when the person already keeps the maximum number of roles.</summary>
    Task<UserRole?> CreateAsync(AuthenticatedUser user, string name, string prompt, CancellationToken cancellationToken);

    /// <summary>Returns null when the role does not exist.</summary>
    Task<UserRole?> UpdateAsync(AuthenticatedUser user, string id, string name, string prompt, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(AuthenticatedUser user, string id, CancellationToken cancellationToken);

    /// <summary>The role a new conversation starts with, or null. A default that no longer names a role reads as null.</summary>
    Task<string?> GetDefaultAsync(AuthenticatedUser user, CancellationToken cancellationToken);

    /// <summary>Null clears the default. Returns false when the id names no role of this person.</summary>
    Task<bool> SetDefaultAsync(AuthenticatedUser user, string? id, CancellationToken cancellationToken);
}

public static class UserRoleValidator
{
    public const int MaxRoles = 30;
    public const int MaxNameChars = 40;

    /// <summary>Role ids are minted by the server as 32 hex characters; anything else cannot name a role.</summary>
    public static bool IsRoleId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    public static (string Name, string Prompt, string? Error) Validate(string? name, string? prompt)
    {
        var trimmedName = name?.Trim() ?? "";
        var trimmedPrompt = prompt?.Trim() ?? "";
        if (trimmedName.Length == 0) return ("", "", "请给角色起个名字。");
        if (trimmedName.Length > MaxNameChars) return ("", "", $"角色名最多 {MaxNameChars} 个字。");
        if (trimmedPrompt.Length == 0) return ("", "", "角色提示词不能为空。");
        if (trimmedPrompt.Length > ChatSettingsDocument.MaxCustomPromptChars)
            return ("", "", $"角色提示词最多 {ChatSettingsDocument.MaxCustomPromptChars} 个字符。");
        return (trimmedName, trimmedPrompt, null);
    }
}

/// <summary>Roles live under <c>userSettings/{uid}/roles/{roleId}</c>; only the owner's server code reads them.</summary>
public sealed class FirestoreUserRoleStore(FirestoreDb db) : IUserRoleStore
{
    private const string RolesCollection = "roles";

    private const string DefaultRoleField = "defaultRoleId";

    private DocumentReference UserDocument(AuthenticatedUser user) =>
        db.Collection(FirestoreSettingsStore.UserSettingsCollection).Document(FirestoreConversationSchema.UserDocumentId(user));

    private CollectionReference Roles(AuthenticatedUser user) => UserDocument(user).Collection(RolesCollection);

    private static UserRole Read(DocumentSnapshot snapshot) => new(
        snapshot.Id,
        snapshot.TryGetValue<string>("name", out var name) ? name : "",
        snapshot.TryGetValue<string>("prompt", out var prompt) ? prompt : "",
        snapshot.TryGetValue<Timestamp>("updatedAt", out var updated) ? updated.ToDateTime() : null);

    public async Task<IReadOnlyList<UserRole>> ListAsync(AuthenticatedUser user, CancellationToken cancellationToken)
    {
        var snapshot = await Roles(user).GetSnapshotAsync(cancellationToken);
        return snapshot.Documents.Select(Read).OrderBy(r => r.Name, StringComparer.CurrentCulture).ToList();
    }

    public async Task<UserRole?> CreateAsync(AuthenticatedUser user, string name, string prompt, CancellationToken cancellationToken)
    {
        var roles = Roles(user);
        var reference = roles.Document(Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        var created = await db.RunTransactionAsync(async transaction =>
        {
            var existing = await transaction.GetSnapshotAsync(roles, cancellationToken);
            if (existing.Count >= UserRoleValidator.MaxRoles) return false;
            transaction.Create(reference, new Dictionary<string, object>
            {
                ["name"] = name,
                ["prompt"] = prompt,
                ["createdAt"] = Timestamp.FromDateTime(now),
                ["updatedAt"] = Timestamp.FromDateTime(now)
            });
            return true;
        }, cancellationToken: cancellationToken);
        return created ? new UserRole(reference.Id, name, prompt, now) : null;
    }

    public async Task<UserRole?> UpdateAsync(
        AuthenticatedUser user, string id, string name, string prompt, CancellationToken cancellationToken)
    {
        if (!UserRoleValidator.IsRoleId(id)) return null;
        var reference = Roles(user).Document(id);
        var now = DateTime.UtcNow;
        var updated = await db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken);
            if (!snapshot.Exists) return false;
            transaction.Update(reference, new Dictionary<string, object>
            {
                ["name"] = name,
                ["prompt"] = prompt,
                ["updatedAt"] = Timestamp.FromDateTime(now)
            });
            return true;
        }, cancellationToken: cancellationToken);
        return updated ? new UserRole(id, name, prompt, now) : null;
    }

    public async Task<bool> DeleteAsync(AuthenticatedUser user, string id, CancellationToken cancellationToken)
    {
        if (!UserRoleValidator.IsRoleId(id)) return false;
        var reference = Roles(user).Document(id);
        var userDocument = UserDocument(user);
        return await db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken);
            var owner = await transaction.GetSnapshotAsync(userDocument, cancellationToken);
            if (!snapshot.Exists) return false;
            transaction.Delete(reference);
            if (owner.Exists && owner.TryGetValue<string>(DefaultRoleField, out var current) && current == id)
                transaction.Update(userDocument, new Dictionary<string, object> { [DefaultRoleField] = FieldValue.Delete });
            return true;
        }, cancellationToken: cancellationToken);
    }

    public async Task<string?> GetDefaultAsync(AuthenticatedUser user, CancellationToken cancellationToken)
    {
        var owner = await UserDocument(user).GetSnapshotAsync(cancellationToken);
        if (!owner.Exists || !owner.TryGetValue<string>(DefaultRoleField, out var id) || !UserRoleValidator.IsRoleId(id)) return null;
        var role = await Roles(user).Document(id).GetSnapshotAsync(cancellationToken);
        return role.Exists ? id : null;
    }

    public async Task<bool> SetDefaultAsync(AuthenticatedUser user, string? id, CancellationToken cancellationToken)
    {
        var userDocument = UserDocument(user);
        if (id == null)
        {
            await userDocument.SetAsync(new Dictionary<string, object> { [DefaultRoleField] = FieldValue.Delete }, SetOptions.MergeAll, cancellationToken);
            return true;
        }

        if (!UserRoleValidator.IsRoleId(id)) return false;
        var reference = Roles(user).Document(id);
        return await db.RunTransactionAsync(async transaction =>
        {
            var role = await transaction.GetSnapshotAsync(reference, cancellationToken);
            if (!role.Exists) return false;
            transaction.Set(userDocument, new Dictionary<string, object> { [DefaultRoleField] = id }, SetOptions.MergeAll);
            return true;
        }, cancellationToken: cancellationToken);
    }
}
