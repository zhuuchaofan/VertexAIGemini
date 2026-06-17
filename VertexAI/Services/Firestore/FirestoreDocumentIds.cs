namespace VertexAI.Services.Firestore;

internal static class FirestoreDocumentIds
{
    public static string UserDocumentId(Guid localUserId, string? firebaseUid) =>
        string.IsNullOrWhiteSpace(firebaseUid)
            ? localUserId.ToString("D")
            : firebaseUid;
}
