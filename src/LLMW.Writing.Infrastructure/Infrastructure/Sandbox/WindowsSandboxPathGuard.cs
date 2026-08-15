using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSandboxPathGuard : ISandboxPathGuard
{
    public SandboxError? TryOpenRead(string projectRoot, string runId, string logicalRelativePath, out byte[] bytes)
    {
        bytes = [];
        var opened = TryOpen(projectRoot, runId, logicalRelativePath, write: false, contents: default, out var error, out var read);
        if (error is not null)
        {
            return error;
        }

        bytes = read;
        return opened;
    }

    public SandboxError? TryOpenWrite(string projectRoot, string runId, string logicalRelativePath, ReadOnlySpan<byte> contents)
    {
        return TryOpen(projectRoot, runId, logicalRelativePath, write: true, contents, out var error, out _) ?? error;
    }

    private static SandboxError? TryOpen(
        string projectRoot,
        string runId,
        string logicalRelativePath,
        bool write,
        ReadOnlySpan<byte> contents,
        out SandboxError? error,
        out byte[] bytes)
    {
        error = null;
        bytes = [];
        string relative;
        try
        {
            relative = SandboxPathPolicy.NormalizeRelative(logicalRelativePath);
        }
        catch (ArgumentException)
        {
            error = SandboxError.PathOutOfScope;
            return error;
        }

        if (SandboxPathPolicy.IsAuthorityTree(relative) || SandboxPathPolicy.IsWindowsSystemLocation(projectRoot))
        {
            error = SandboxError.PathOutOfScope;
            return error;
        }

        if (write && !SandboxPathPolicy.IsDesignatedWorkRelative(relative, runId))
        {
            error = SandboxError.PathOutOfScope;
            return error;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var segments = relative.Split('/');
        SafeFileHandle? current = null;
        try
        {
            current = OpenNt(null, ToNtPath(root), directory: true, createFile: false, overwrite: false, out error);
            if (error is not null || current is null)
            {
                error ??= SandboxError.PathOutOfScope;
                return error;
            }

            if (IsReparse(current))
            {
                error = SandboxError.ReparsePointRejected;
                return error;
            }

            if (!FinalPathIsInside(current, root))
            {
                error = SandboxError.PathOutOfScope;
                return error;
            }

            for (var i = 0; i < segments.Length; i++)
            {
                var last = i == segments.Length - 1;
                var next = OpenNt(
                    current,
                    segments[i],
                    directory: !last,
                    createFile: last && write,
                    overwrite: last && write,
                    out error);
                current.Dispose();
                current = next;
                if (error is not null || current is null)
                {
                    error ??= SandboxError.PathOutOfScope;
                    return error;
                }

                if (IsReparse(current))
                {
                    error = SandboxError.ReparsePointRejected;
                    return error;
                }

                var expectedParent = i == segments.Length - 1
                    ? Path.GetFullPath(Path.Combine(root, Path.Combine(segments)))
                    : Path.GetFullPath(Path.Combine(root, Path.Combine(segments[..(i + 1)])));
                if (!FinalPathIsInside(current, write ? SandboxPathPolicy.RunWorkDirectory(projectRoot, runId) : root) &&
                    !FinalPathEquals(current, expectedParent))
                {
                    error = SandboxError.ReparsePointRejected;
                    return error;
                }
            }

            if (SandboxPathPolicy.IsWindowsSystemLocation(QueryFinalPath(current)))
            {
                error = SandboxError.PathOutOfScope;
                return error;
            }

            if (write)
            {
                var buffer = contents.ToArray();
                if (buffer.Length > 0)
                {
                    if (!NativeMethods.WriteFile(current, buffer, buffer.Length, out var written, IntPtr.Zero) ||
                        written != buffer.Length)
                    {
                        error = SandboxError.PathOutOfScope;
                        return error;
                    }
                }
            }
            else
            {
                bytes = ReadAll(current);
            }

            return null;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static SafeFileHandle? OpenNt(
        SafeFileHandle? parent,
        string name,
        bool directory,
        bool createFile,
        bool overwrite,
        out SandboxError? error)
    {
        error = null;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeMemory = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
        var attributesMemory = Marshal.AllocHGlobal(Marshal.SizeOf<OBJECT_ATTRIBUTES>());
        try
        {
            var unicode = new UNICODE_STRING
            {
                Length = (ushort)(checked(name.Length * 2)),
                MaximumLength = (ushort)(checked((name.Length + 1) * 2)),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicode, unicodeMemory, false);
            var attributes = new OBJECT_ATTRIBUTES
            {
                Length = (uint)Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
                RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = unicodeMemory,
                Attributes = NativeConstants.OBJ_CASE_INSENSITIVE
            };
            Marshal.StructureToPtr(attributes, attributesMemory, false);
            var oa = Marshal.PtrToStructure<OBJECT_ATTRIBUTES>(attributesMemory);
            var io = new IO_STATUS_BLOCK();
            uint disposition = NativeConstants.FILE_OPEN;
            if (createFile)
            {
                disposition = overwrite ? NativeConstants.FILE_OVERWRITE_IF : NativeConstants.FILE_OPEN_IF;
            }

            uint options = NativeConstants.FILE_SYNCHRONOUS_IO_NONALERT |
                           NativeConstants.FILE_OPEN_REPARSE_POINT |
                           NativeConstants.FILE_OPEN_FOR_BACKUP_INTENT;
            options |= directory ? NativeConstants.FILE_DIRECTORY_FILE : NativeConstants.FILE_NON_DIRECTORY_FILE;
            uint access = NativeConstants.SYNCHRONIZE | NativeConstants.FILE_READ_ATTRIBUTES |
                          NativeConstants.FILE_READ_DATA;
            if (createFile)
            {
                access |= NativeConstants.FILE_WRITE_DATA | NativeConstants.FILE_WRITE_ATTRIBUTES | NativeConstants.DELETE;
            }

            var status = NativeMethods.NtCreateFile(
                out var handle,
                access,
                ref oa,
                ref io,
                IntPtr.Zero,
                NativeConstants.FILE_ATTRIBUTE_NORMAL,
                NativeConstants.FILE_SHARE_READ | NativeConstants.FILE_SHARE_WRITE | NativeConstants.FILE_SHARE_DELETE,
                disposition,
                options,
                IntPtr.Zero,
                0);
            if (status < 0 || handle == IntPtr.Zero)
            {
                error = SandboxError.PathOutOfScope;
                return null;
            }

            return new SafeFileHandle(handle, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(attributesMemory);
            Marshal.FreeHGlobal(unicodeMemory);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static bool IsReparse(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFORMATION>());
        try
        {
            var io = new IO_STATUS_BLOCK();
            var status = NativeMethods.NtQueryInformationFile(
                handle,
                ref io,
                buffer,
                (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFORMATION>(),
                NativeConstants.FileAttributeTagInformation);
            if (status < 0)
            {
                var basic = Marshal.AllocHGlobal(Marshal.SizeOf<FILE_BASIC_INFORMATION>());
                try
                {
                    io = new IO_STATUS_BLOCK();
                    status = NativeMethods.NtQueryInformationFile(
                        handle,
                        ref io,
                        basic,
                        (uint)Marshal.SizeOf<FILE_BASIC_INFORMATION>(),
                        NativeConstants.FileBasicInformation);
                    if (status < 0)
                    {
                        return true;
                    }

                    var info = Marshal.PtrToStructure<FILE_BASIC_INFORMATION>(basic);
                    return (info.FileAttributes & NativeConstants.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(basic);
                }
            }

            var tag = Marshal.PtrToStructure<FILE_ATTRIBUTE_TAG_INFORMATION>(buffer);
            return (tag.FileAttributes & NativeConstants.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool FinalPathIsInside(SafeFileHandle handle, string parent) =>
        SandboxPathPolicy.IsInside(parent, QueryFinalPath(handle));

    private static bool FinalPathEquals(SafeFileHandle handle, string expected) =>
        QueryFinalPath(handle).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    private static string QueryFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[520];
        var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeConstants.VOLUME_NAME_DOS);
        if (length == 0)
        {
            return "";
        }

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return Path.GetFullPath(path);
    }

    private static string ToNtPath(string win32Path)
    {
        var full = Path.GetFullPath(win32Path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            full = full[4..];
        }

        return @"\??\" + full;
    }

    private static byte[] ReadAll(SafeFileHandle handle)
    {
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

internal static class HeadTailCapture
{
    public static (string Text, bool Truncated) Decode(byte[] data)
    {
        if (data.Length <= SandboxPathPolicy.MaxCapturedOutputBytes)
        {
            return (Encoding.UTF8.GetString(data), false);
        }

        var head = data.AsSpan(0, SandboxPathPolicy.OutputHeadBytes);
        var tail = data.AsSpan(data.Length - SandboxPathPolicy.OutputTailBytes, SandboxPathPolicy.OutputTailBytes);
        var text = Encoding.UTF8.GetString(head) + Encoding.UTF8.GetString(tail);
        return (text, true);
    }
}
