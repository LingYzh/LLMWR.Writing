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

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(supplied));
    }
}
