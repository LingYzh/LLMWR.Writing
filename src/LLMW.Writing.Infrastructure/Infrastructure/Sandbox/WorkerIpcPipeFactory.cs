using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

public static class WorkerIpcPipeFactory
{
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream Create(string pipeName, string appContainerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appContainerSid);
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Current user SID is unavailable.");
        var sddl = $"D:(A;;GA;;;{userSid})(A;;GRGW;;;{appContainerSid})";
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                NativeConstants.SDDL_REVISION_1,
                out var descriptor,
                out _))
        {
            throw new SandboxLayerException(
                SandboxError.AppContainerAclFailed,
                $"Worker pipe SDDL conversion failed: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = 0
            };
            var handle = NativeMethods.CreateNamedPipeW(
                @"\\.\pipe\" + pipeName,
                NativeConstants.PIPE_ACCESS_DUPLEX | NativeConstants.FILE_FLAG_OVERLAPPED,
                NativeConstants.PIPE_TYPE_BYTE | NativeConstants.PIPE_WAIT | NativeConstants.PIPE_REJECT_REMOTE_CLIENTS,
                1,
                64 * 1024,
                64 * 1024,
                0,
                ref attributes);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                throw new SandboxLayerException(
                    SandboxError.ProcessLaunchFailed,
                    $"CreateNamedPipeW failed: {Marshal.GetLastWin32Error()}.");
            }

            var safe = new SafePipeHandle(handle, ownsHandle: true);
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safePipeHandle: safe);
        }
        finally
        {
            NativeMethods.LocalFree(descriptor);
        }
    }
}
