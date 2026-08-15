using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal static class NativeSid
{
    public static SafeSidHandle CreateWellKnown(int wellKnownSidType)
    {
        var size = 64;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.CreateWellKnownSid(wellKnownSidType, IntPtr.Zero, buffer, ref size))
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                if (!NativeMethods.CreateWellKnownSid(wellKnownSidType, IntPtr.Zero, buffer, ref size))
                {
                    var error = Marshal.GetLastWin32Error();
                    Marshal.FreeHGlobal(buffer);
                    throw new InvalidOperationException($"CreateWellKnownSid failed: {error}.");
                }
            }

            var owned = buffer;
            buffer = IntPtr.Zero;
            return new SafeSidHandle(owned, ownsHandle: true, SidReleaseKind.MarshalFree);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public static string ToStringSid(IntPtr sid)
    {
        if (!NativeMethods.ConvertSidToStringSidW(sid, out var stringSid) || stringSid == IntPtr.Zero)
        {
            throw new InvalidOperationException($"ConvertSidToStringSidW failed: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            return Marshal.PtrToStringUni(stringSid) ?? throw new InvalidOperationException("SID string was null.");
        }
        finally
        {
            NativeMethods.LocalFree(stringSid);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRestrictedTokenFactory
{
    private WindowsRestrictedTokenFactory()
    {
    }
    public static SafeAccessTokenHandle Create(ISandboxFaultInjector faultInjector)
    {
        if (faultInjector.Fault == SandboxFaultPoint.RestrictedTokenInit)
        {
            throw new SandboxLayerException(SandboxError.RestrictedTokenFailed, "Injected restricted token failure.");
        }

        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeConstants.TOKEN_DUPLICATE | NativeConstants.TOKEN_QUERY | NativeConstants.TOKEN_ASSIGN_PRIMARY |
                NativeConstants.TOKEN_ADJUST_DEFAULT | NativeConstants.TOKEN_ADJUST_SESSIONID |
                NativeConstants.TOKEN_ADJUST_PRIVILEGES,
                out var processToken))
        {
            throw new SandboxLayerException(SandboxError.RestrictedTokenFailed, $"OpenProcessToken failed: {Marshal.GetLastWin32Error()}.");
        }

        using (processToken)
        {
            using var adminSid = NativeSid.CreateWellKnown(NativeConstants.WinBuiltinAdministratorsSid);
            using var powerSid = NativeSid.CreateWellKnown(NativeConstants.WinBuiltinPowerUsersSid);

            SID_AND_ATTRIBUTES[] disable =
            [
                new() { Sid = adminSid.DangerousGetHandle(), Attributes = NativeConstants.SE_GROUP_USE_FOR_DENY_ONLY },
                new() { Sid = powerSid.DangerousGetHandle(), Attributes = NativeConstants.SE_GROUP_USE_FOR_DENY_ONLY }
            ];
            // Restricting SIDs (and WRITE_RESTRICTED) are omitted: on this OS they cause
            // STATUS_ACCESS_DENIED (0xC0000022) at child image load when combined with the
            // mandatory AppContainer launch. The token is still produced by CreateRestrictedToken
            // with LUA_TOKEN, DISABLE_MAX_PRIVILEGE, and Administrators/Power Users deny-only.
            if (!NativeMethods.CreateRestrictedToken(
                    processToken,
                    NativeConstants.DISABLE_MAX_PRIVILEGE | NativeConstants.LUA_TOKEN,
                    (uint)disable.Length,
                    disable,
                    0,
                    IntPtr.Zero,
                    0,
                    null,
                    out var restricted))
            {
                throw new SandboxLayerException(
                    SandboxError.RestrictedTokenFailed,
                    $"CreateRestrictedToken failed: {Marshal.GetLastWin32Error()}.");
            }

            if (!TokenInspector.HasRestrictions(restricted) || TokenInspector.IsElevated(restricted))
            {
                var hasRestrictions = TokenInspector.HasRestrictions(restricted);
                var isRestricted = TokenInspector.IsRestricted(restricted);
                var elevated = TokenInspector.IsElevated(restricted);
                restricted.Dispose();
                throw new SandboxLayerException(
                    SandboxError.RestrictedTokenFailed,
                    $"CreateRestrictedToken did not produce a filtered, non-elevated token. hasRestrictions={hasRestrictions} isRestricted={isRestricted} elevated={elevated}.");
            }

            if (!NativeMethods.DuplicateTokenEx(
                    restricted,
                    NativeConstants.TOKEN_ALL_ACCESS_WIN8,
                    IntPtr.Zero,
                    NativeConstants.SecurityImpersonation,
                    NativeConstants.TokenPrimary,
                    out var primary))
            {
                return restricted;
            }

            restricted.Dispose();
            TryEnablePrivilege(primary, "SeChangeNotifyPrivilege");
            return primary;
        }
    }

    private static void TryEnablePrivilege(SafeAccessTokenHandle token, string name)
    {
        if (!NativeMethods.LookupPrivilegeValueW(null, name, out var luid))
        {
            return;
        }

        var privileges = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Luid = luid,
            Attributes = NativeConstants.SE_PRIVILEGE_ENABLED
        };
        NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
    }
}

[SupportedOSPlatform("windows")]
internal static class TokenInspector
{
    public static bool IsRestricted(SafeAccessTokenHandle token) => QueryBool(token, NativeConstants.TokenIsRestricted);

    public static bool HasRestrictions(SafeAccessTokenHandle token) => QueryBool(token, NativeConstants.TokenHasRestrictions);

    public static bool IsAppContainer(SafeAccessTokenHandle token) => QueryBool(token, NativeConstants.TokenIsAppContainer);

    public static bool IsElevated(SafeAccessTokenHandle token)
    {
        var size = Marshal.SizeOf<TOKEN_ELEVATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeConstants.TokenElevation, buffer, size, out _))
            {
                return false;
            }

            return Marshal.PtrToStructure<TOKEN_ELEVATION>(buffer).TokenIsElevated != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? AppContainerSid(SafeAccessTokenHandle token)
    {
        NativeMethods.GetTokenInformation(token, NativeConstants.TokenAppContainerSid, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeConstants.TokenAppContainerSid, buffer, length, out _))
            {
                return null;
            }

            var sid = Marshal.ReadIntPtr(buffer);
            return sid == IntPtr.Zero ? null : NativeSid.ToStringSid(sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool QueryBool(SafeAccessTokenHandle token, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, infoClass, buffer, 4, out _))
            {
                return false;
            }

            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal sealed class SandboxLayerException : Exception
{
    public SandboxLayerException(SandboxError error, string message)
        : base(message)
    {
        Error = error;
    }

    public SandboxError Error { get; }
}
