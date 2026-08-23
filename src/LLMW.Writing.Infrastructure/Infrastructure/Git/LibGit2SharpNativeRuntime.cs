using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LLMW.Writing.Infrastructure.Git;

/// <summary>
/// Verifies the controlled package asset before any LibGit2Sharp repository operation can use it.
/// This boundary is intentionally Infrastructure-only; it exposes no LibGit2Sharp types.
/// </summary>
public static class LibGit2SharpNativeRuntime
{
    public const string NativeLibraryFileName = "git2-5853918.dll";
    public const string ExpectedLibGit2Version = "1.9.6";
    public const string ExpectedNativeSha256 = "9de746ab87a1d0f733c6cf2b340ca7c356481754b208b6c43c97ce2ae6067ffe";

    private static readonly object Gate = new();
    private static GitNativeRuntimeInfo? verified;
    private static nint libraryHandle;

    public static GitNativeRuntimeInfo Verify()
    {
        lock (Gate)
        {
            if (verified is not null)
            {
                return verified;
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The controlled WP19 native Git runtime is win-x64 only.");
            }

            var nativePath = ResolveNativePath();
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(sha256, ExpectedNativeSha256))
            {
                throw new InvalidOperationException("The controlled libgit2 native hash does not match the dependency audit.");
            }

            libraryHandle = NativeLibrary.Load(nativePath);
            git_libgit2_version(out var major, out var minor, out var revision);
            var version = $"{major}.{minor}.{revision}";
            if (!StringComparer.Ordinal.Equals(version, ExpectedLibGit2Version))
            {
                throw new InvalidOperationException($"Expected libgit2 {ExpectedLibGit2Version}, received {version}.");
            }

            verified = new GitNativeRuntimeInfo(version, nativePath, sha256);
            return verified;
        }
    }

    private static string ResolveNativePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, NativeLibraryFileName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", NativeLibraryFileName)
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new DllNotFoundException($"Controlled native asset {NativeLibraryFileName} was not deployed by Infrastructure.");
    }

    [DllImport("git2-5853918", CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_libgit2_version(out int major, out int minor, out int revision);
}

public sealed record GitNativeRuntimeInfo(string Version, string NativePath, string Sha256);
