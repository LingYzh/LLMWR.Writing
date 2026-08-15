using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class PrivilegeScope : IDisposable
{
    private readonly SafeAccessTokenHandle token;
    private readonly bool ownsToken;
    private readonly IntPtr previousState;
    private readonly int previousLength;
    private bool restored;

    private PrivilegeScope(SafeAccessTokenHandle token, bool ownsToken, IntPtr previousState, int previousLength)
    {
        this.token = token;
        this.ownsToken = ownsToken;
        this.previousState = previousState;
        this.previousLength = previousLength;
    }

    public static PrivilegeScope EnableOnCurrentProcess(string privilegeName, ISandboxFaultInjector faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);
        faultInjector ??= NoSandboxFaultInjector.Instance;
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeConstants.TOKEN_QUERY | NativeConstants.TOKEN_ADJUST_PRIVILEGES,
                out var processToken))
        {
            throw new SandboxLayerException(
                SandboxError.RestrictedTokenFailed,
                $"OpenProcessToken for scoped privilege failed: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, privilegeName, out var luid))
            {
                throw new SandboxLayerException(
                    SandboxError.RestrictedTokenFailed,
                    $"LookupPrivilegeValueW({privilegeName}) failed: {Marshal.GetLastWin32Error()}.");
            }

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeConstants.SE_PRIVILEGE_ENABLED
            };
            var previous = Marshal.AllocHGlobal(64);
            NativeMethods.SetLastError(0);
            if (!NativeMethods.AdjustTokenPrivileges(processToken, false, ref privileges, 64, previous, out var returned) ||
                Marshal.GetLastWin32Error() == NativeConstants.ERROR_NOT_ALL_ASSIGNED)
            {
                var error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(previous);
                throw new SandboxLayerException(
                    SandboxError.RestrictedTokenFailed,
                    $"AdjustTokenPrivileges({privilegeName}) failed: {error}.");
            }

            var scope = new PrivilegeScope(processToken, ownsToken: true, previous, returned);
            processToken = null!;
            if (faultInjector.Fault == SandboxFaultPoint.PrivilegeScopedEnable)
            {
                scope.Dispose();
                throw new SandboxLayerException(
                    SandboxError.RestrictedTokenFailed,
                    "Injected scoped privilege failure.");
            }

            return scope;
        }
        finally
        {
            processToken?.Dispose();
        }
    }

    public void Dispose()
    {
        if (restored)
        {
            return;
        }

        restored = true;
        try
        {
            if (previousState != IntPtr.Zero && previousLength > 0)
            {
                var previous = Marshal.PtrToStructure<TOKEN_PRIVILEGES>(previousState);
                NativeMethods.AdjustTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            if (previousState != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(previousState);
            }

            if (ownsToken)
            {
                token.Dispose();
            }
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class CoreProcessPrivilegeSnapshot
{
    public static IReadOnlyDictionary<string, uint> Capture()
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeConstants.TOKEN_QUERY,
                out var token))
        {
            throw new InvalidOperationException($"OpenProcessToken failed: {Marshal.GetLastWin32Error()}.");
        }

        using (token)
        {
            NativeMethods.GetTokenInformation(token, NativeConstants.TokenPrivileges, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            }

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeConstants.TokenPrivileges, buffer, length, out _))
                {
                    throw new InvalidOperationException($"GetTokenInformation(TokenPrivileges) failed: {Marshal.GetLastWin32Error()}.");
                }

                var count = Marshal.ReadInt32(buffer);
                var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                var cursor = buffer + 4;
                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<LUID_AND_ATTRIBUTES>(cursor);
                    var name = new char[256];
                    var nameLength = 256;
                    var luid = entry.Luid;
                    if (NativeMethods.LookupPrivilegeNameW(null, ref luid, name, ref nameLength))
                    {
                        result[new string(name, 0, Math.Max(0, nameLength))] = entry.Attributes;
                    }

                    cursor += Marshal.SizeOf<LUID_AND_ATTRIBUTES>();
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public static uint Attribute(IReadOnlyDictionary<string, uint> snapshot, string name) =>
        snapshot.TryGetValue(name, out var value) ? value : 0;
}
