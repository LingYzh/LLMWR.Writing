using System.Security.Cryptography;

namespace LLMW.Writing.Contracts.Editor;

/// <summary>
/// Editor transport resource bounds. These are not the 1 MiB IPC/WebMessage frame limit.
/// </summary>
public static class EditorTransportLimits
{
    public const int MaximumDocumentUtf8Bytes = 32 * 1024 * 1024;
    public const int MaximumChunkUtf8Bytes = 256 * 1024;
    public const int AutosaveDebounceMilliseconds = 500;
    public const int MaximumFilenameChars = 255;
}

public static class ContentDigest
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsSha256Hex(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(Uri.IsHexDigit);

    public static string Normalize(string digest)
    {
        if (!IsSha256Hex(digest))
        {
            throw new ArgumentException("A content digest must be exactly 64 hexadecimal characters.", nameof(digest));
        }

        return digest.ToLowerInvariant();
    }
}
