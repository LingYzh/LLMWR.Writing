using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal static class NtObjectPath
{
    public static SafeFileHandle OpenRoot(string win32Path, uint access)
    {
        var handle = Open(
            parent: null,
            ToNtPath(win32Path),
            directory: true,
            create: false,
            overwrite: false,
            access);
        try
        {
            RejectReparse(handle);
            if (!FinalPathEquals(handle, win32Path))
            {
                throw new SandboxLayerException(SandboxError.ReparsePointRejected, "Trusted root final path drifted.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenChild(
        SafeFileHandle parent,
        string name,
        bool directory,
        bool create,
        uint access)
    {
        ValidateSegment(name);
        var handle = Open(parent, name, directory, create, overwrite: false, access);
        try
        {
            RejectReparse(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenOrCreateDirectory(SafeFileHandle parent, string name, uint access)
    {
        ValidateSegment(name);
        var handle = Open(parent, name, directory: true, create: true, overwrite: false, access);
        try
        {
            RejectReparse(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle CreateOrReplaceFile(SafeFileHandle parent, string name, uint access)
    {
        ValidateSegment(name);
        var handle = Open(parent, name, directory: false, create: true, overwrite: true, access);
        try
        {
            RejectReparse(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenExisting(string win32Path, uint access)
    {
        var handle = Open(
            parent: null,
            ToNtPath(win32Path),
            directory: false,
            create: false,
            overwrite: false,
            access,
            allowEither: true);
        try
        {
            RejectReparse(handle);
            if (!FinalPathEquals(handle, win32Path) && !FinalPathIsInside(handle, Path.GetDirectoryName(win32Path) ?? win32Path))
            {
                throw new SandboxLayerException(SandboxError.ReparsePointRejected, "Opened path redirected outside the expected location.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static bool IsReparse(SafeFileHandle handle)
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

    public static void RejectReparse(SafeFileHandle handle)
    {
        if (IsReparse(handle))
        {
            throw new SandboxLayerException(SandboxError.ReparsePointRejected, "A reparse point was found on a Core sandbox path.");
        }
    }

    public static bool FinalPathIsInside(SafeFileHandle handle, string parent) =>
        SandboxPathPolicy.IsInside(parent, QueryFinalPath(handle));

    public static bool FinalPathEquals(SafeFileHandle handle, string expected)
    {
        var actual = QueryFinalPath(handle);
        var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual));
        var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected));
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    public static string QueryFinalPath(SafeFileHandle handle)
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

    public static string ToNtPath(string win32Path)
    {
        var full = Path.GetFullPath(win32Path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            full = full[4..];
        }

        return @"\??\" + full;
    }

    public static byte[] ReadAll(SafeFileHandle handle)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (NativeMethods.ReadFile(handle, buffer, buffer.Length, out var read, IntPtr.Zero) && read > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    public static void WriteAll(SafeFileHandle handle, ReadOnlySpan<byte> contents)
    {
        if (contents.Length == 0)
        {
            return;
        }

        var buffer = contents.ToArray();
        if (!NativeMethods.WriteFile(handle, buffer, buffer.Length, out var written, IntPtr.Zero) || written != buffer.Length)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Could not write a sandbox-internal file without following reparse points.");
        }
    }

    public static void ValidateSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new SandboxLayerException(SandboxError.PathOutOfScope, "A sandbox path segment is not a single safe name.");
        }
    }

    public static SafeFileHandle Open(
        SafeFileHandle? parent,
        string name,
        bool directory,
        bool create,
        bool overwrite,
        uint access,
        bool allowEither = false)
    {
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
            if (create)
            {
                disposition = overwrite ? NativeConstants.FILE_OVERWRITE_IF : NativeConstants.FILE_OPEN_IF;
            }

            uint options = NativeConstants.FILE_SYNCHRONOUS_IO_NONALERT |
                           NativeConstants.FILE_OPEN_REPARSE_POINT |
                           NativeConstants.FILE_OPEN_FOR_BACKUP_INTENT;
            if (!allowEither)
            {
                options |= directory ? NativeConstants.FILE_DIRECTORY_FILE : NativeConstants.FILE_NON_DIRECTORY_FILE;
            }

            var status = NativeMethods.NtCreateFile(
                out var raw,
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
            if (status < 0 || raw == IntPtr.Zero)
            {
                throw new SandboxLayerException(
                    MapOpenError(status, create),
                    $"NtCreateFile failed for '{name}': 0x{status:X8}.");
            }

            return new SafeFileHandle(raw, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(attributesMemory);
            Marshal.FreeHGlobal(unicodeMemory);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static SandboxError MapOpenError(int status, bool create)
    {
        if (status is NativeConstants.STATUS_OBJECT_NAME_NOT_FOUND or NativeConstants.STATUS_OBJECT_PATH_NOT_FOUND)
        {
            return SandboxError.PathOutOfScope;
        }

        return create ? SandboxError.ReparsePointRejected : SandboxError.PathOutOfScope;
    }
}
