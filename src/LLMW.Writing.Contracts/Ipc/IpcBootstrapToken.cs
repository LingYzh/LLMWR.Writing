using System.Security.Cryptography;

namespace LLMW.Writing.Contracts.Ipc;

public static class IpcBootstrapToken
{
    private const int TokenBytes = IpcProtocol.BootstrapTokenMinimumBits / 8;

    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool FixedTimeEquals(string expected, string supplied)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(supplied);

        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(supplied);
        var lengthMismatch = left.Length != right.Length;
        if (lengthMismatch)
        {
            right = left;
        }

        return CryptographicOperations.FixedTimeEquals(left, right) && !lengthMismatch;
    }
}
