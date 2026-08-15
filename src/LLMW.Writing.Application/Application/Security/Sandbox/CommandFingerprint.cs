using System.Security.Cryptography;
using System.Text;

namespace LLMW.Writing.Application.Security.Sandbox;

public static class ExactCommand
{
    public static CommandFingerprint Fingerprint(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        arguments ??= [];
        var canonical = Path.GetFullPath(executable);
        var payload = canonical + "\n" + string.Join("\n", arguments.Select(static argument => argument ?? ""));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new CommandFingerprint(canonical, arguments.ToArray(), digest);
    }
}
