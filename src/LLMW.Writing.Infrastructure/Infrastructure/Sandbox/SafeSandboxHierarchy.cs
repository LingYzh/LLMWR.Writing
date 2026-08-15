using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal static class SafeSandboxHierarchy
{
    private const uint DirectoryReadAccess =
        NativeConstants.SYNCHRONIZE |
        NativeConstants.FILE_READ_ATTRIBUTES |
        NativeConstants.FILE_LIST_DIRECTORY |
        NativeConstants.READ_CONTROL;

    private const uint DirectoryAccess =
        DirectoryReadAccess |
        NativeConstants.FILE_WRITE_DATA |
        NativeConstants.FILE_APPEND_DATA |
        NativeConstants.WRITE_DAC;

    private const uint FileReadAccess =
        NativeConstants.SYNCHRONIZE |
        NativeConstants.FILE_READ_ATTRIBUTES |
        NativeConstants.FILE_READ_DATA;

    private const uint FileWriteAccess =
        NativeConstants.SYNCHRONIZE |
        NativeConstants.FILE_READ_ATTRIBUTES |
        NativeConstants.FILE_WRITE_DATA |
        NativeConstants.FILE_WRITE_ATTRIBUTES |
        NativeConstants.DELETE |
        NativeConstants.READ_CONTROL |
        NativeConstants.WRITE_DAC;

    public static string EnsureDirectory(string trustedProjectRoot, params string[] relativeSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedProjectRoot);
        ArgumentNullException.ThrowIfNull(relativeSegments);
        if (relativeSegments.Length == 0)
        {
            throw new SandboxLayerException(SandboxError.PathOutOfScope, "A Core sandbox directory requires relative segments.");
        }

        using var chain = OpenChain(trustedProjectRoot, relativeSegments, create: true);
        var final = NtObjectPath.QueryFinalPath(chain.Leaf);
        var expected = Path.GetFullPath(Path.Combine(new[] { trustedProjectRoot }.Concat(relativeSegments).ToArray()));
        if (!final.Equals(Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase) &&
            !SandboxPathPolicy.PathsEqual(final, expected))
        {
            throw new SandboxLayerException(SandboxError.ReparsePointRejected, "Ensured directory resolved outside the trusted project root.");
        }

        if (!SandboxPathPolicy.IsInside(trustedProjectRoot, final))
        {
            throw new SandboxLayerException(SandboxError.PathOutOfScope, "Ensured directory escaped the trusted project root.");
        }

        return final;
    }

    public static string EnsureSandboxRoot(string trustedProjectRoot) =>
        EnsureDirectory(trustedProjectRoot, SandboxPathPolicy.SandboxRootDirectoryName);

    public static string EnsureRunWorkDirectory(string trustedProjectRoot, string runId) =>
        EnsureDirectory(trustedProjectRoot, SandboxPathPolicy.SandboxRootDirectoryName, "runs", runId, "work");

    public static string EnsureToolStagingDirectory(string trustedProjectRoot, string stagingIdentity) =>
        EnsureDirectory(trustedProjectRoot, SandboxPathPolicy.SandboxRootDirectoryName, "tools", stagingIdentity);

    public static void VerifyExistingChain(string trustedProjectRoot, params string[] relativeSegments)
    {
        using var chain = OpenChain(trustedProjectRoot, relativeSegments, create: false);
        _ = chain.Leaf;
    }

    public static void VerifyExistingFile(string trustedRoot, params string[] relativeSegments)
    {
        using var chain = OpenChain(trustedRoot, relativeSegments, create: false, lastIsDirectory: false, lastAccess: FileReadAccess);
        _ = chain.Leaf;
    }

    public static byte[] ReadFileRelative(string trustedRoot, IReadOnlyList<string> relativeSegments)
    {
        using var chain = OpenChain(trustedRoot, relativeSegments, create: false, lastIsDirectory: false, lastAccess: FileReadAccess);
        return NtObjectPath.ReadAll(chain.Leaf);
    }

    public static void WriteFileRelative(
        string trustedRoot,
        IReadOnlyList<string> directorySegments,
        string fileName,
        ReadOnlySpan<byte> contents)
    {
        NtObjectPath.ValidateSegment(fileName);
        using var chain = OpenChain(trustedRoot, directorySegments, create: true);
        using var file = NtObjectPath.CreateOrReplaceFile(
            chain.Leaf,
            fileName,
            FileWriteAccess | NativeConstants.FILE_READ_DATA);
        if (!NtObjectPath.FinalPathIsInside(file, NtObjectPath.QueryFinalPath(chain.Leaf)))
        {
            throw new SandboxLayerException(SandboxError.ReparsePointRejected, "Staged file redirected outside the sandbox directory.");
        }

        NtObjectPath.WriteAll(file, contents);
    }

    private static HandleChain OpenChain(
        string trustedProjectRoot,
        IReadOnlyList<string> relativeSegments,
        bool create,
        bool lastIsDirectory = true,
        uint lastAccess = DirectoryAccess)
    {
        var directoryAccess = create ? DirectoryAccess : DirectoryReadAccess;
        var root = NtObjectPath.OpenRoot(trustedProjectRoot, directoryAccess);
        HandleChain? chain = null;
        try
        {
            chain = new HandleChain(root);
            var expected = Path.GetFullPath(trustedProjectRoot);
            for (var i = 0; i < relativeSegments.Count; i++)
            {
                var name = relativeSegments[i];
                NtObjectPath.ValidateSegment(name);
                expected = Path.GetFullPath(Path.Combine(expected, name));
                var last = i == relativeSegments.Count - 1;
                var directory = !last || lastIsDirectory;
                var access = last && !lastIsDirectory ? lastAccess : directoryAccess;
                SafeFileHandle child;
                if (directory && create)
                {
                    child = NtObjectPath.OpenOrCreateDirectory(chain.Leaf, name, access);
                }
                else
                {
                    child = NtObjectPath.OpenChild(chain.Leaf, name, directory, create: false, access);
                }

                chain.Push(child);
                if (!NtObjectPath.FinalPathEquals(child, expected))
                {
                    throw new SandboxLayerException(
                        SandboxError.ReparsePointRejected,
                        "A Core sandbox path component resolved to an unexpected final path.");
                }

                if (!SandboxPathPolicy.IsInside(trustedProjectRoot, expected))
                {
                    throw new SandboxLayerException(SandboxError.PathOutOfScope, "A Core sandbox path escaped the trusted project root.");
                }
            }

            var owned = chain;
            chain = null;
            return owned;
        }
        finally
        {
            chain?.Dispose();
        }
    }

    private sealed class HandleChain : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];

        public HandleChain(SafeFileHandle root)
        {
            handles.Add(root);
        }

        public SafeFileHandle Leaf => handles[^1];

        public void Push(SafeFileHandle handle) => handles.Add(handle);

        public void Dispose()
        {
            for (var i = handles.Count - 1; i >= 0; i--)
            {
                handles[i].Dispose();
            }
        }
    }
}
