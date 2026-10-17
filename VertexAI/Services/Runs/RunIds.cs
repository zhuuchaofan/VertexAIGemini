using System.Security.Cryptography;
using System.Text;

namespace VertexAI.Services.Runs;

/// <summary>Deterministic identifiers derived from the run id, so a replay computes the same ids without reading anything.</summary>
public static class RunIds
{
    private static readonly Guid Namespace = new("6b0f3f0e-8f6d-4a0e-9a54-2f0d8a7a1c11");

    public static bool TryParseRunId(string? value, out string runId)
    {
        runId = "";
        if (!Guid.TryParseExact(value, "D", out var parsed)) return false;
        runId = parsed.ToString("D");
        return true;
    }

    public static Guid UserMessageId(string runId) => CreateV5(Namespace, $"{runId}:user");
    public static Guid AssistantMessageId(string runId) => CreateV5(Namespace, $"{runId}:assistant");

    // RFC 4122 version 5 (SHA-1) UUID.
    internal static Guid CreateV5(Guid ns, string name)
    {
        var namespaceBytes = ns.ToByteArray();
        SwapByteOrder(namespaceBytes);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(data, 0);
        nameBytes.CopyTo(data, namespaceBytes.Length);

        var hash = SHA1.HashData(data);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapByteOrder(bytes);
        return new Guid(bytes);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
