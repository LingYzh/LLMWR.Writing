using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class AppContainerAclManager
{
    private static readonly ConcurrentDictionary<string, byte> Granted = new(StringComparer.OrdinalIgnoreCase);

    private AppContainerAclManager()
    {
    }

    public static void GrantMinimumRecursive(
        string path,
        string appContainerSid,
        uint accessMask,
        ISandboxFaultInjector faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        GrantMinimum(full, appContainerSid, accessMask, inherit: true, faultInjector);
        if (!Directory.Exists(full))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(full))
        {
            if (IsReparse(file))
            {
                continue;
            }

            GrantMinimum(file, appContainerSid, accessMask, inherit: false, faultInjector);
        }

        foreach (var directory in Directory.EnumerateDirectories(full))
        {
            if (IsReparse(directory))
            {
                continue;
            }

            GrantMinimumRecursive(directory, appContainerSid, accessMask, faultInjector);
        }
    }

    public static void GrantInteractiveDesktop(string appContainerSid, ISandboxFaultInjector faultInjector)
    {
        if (faultInjector.Fault == SandboxFaultPoint.AppContainerAcl)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Injected AppContainer ACL failure.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appContainerSid);
        var cacheKey = "winsta-desktop|" + appContainerSid;
        if (Granted.ContainsKey(cacheKey))
        {
            return;
        }

        var windowStation = NativeMethods.GetProcessWindowStation();
        var desktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        if (windowStation == IntPtr.Zero || desktop == IntPtr.Zero)
        {
            throw new SandboxLayerException(
                SandboxError.AppContainerAclFailed,
                $"Window station/desktop handles are unavailable: {Marshal.GetLastWin32Error()}.");
        }

        GrantUserObject(windowStation, appContainerSid, NativeConstants.SandboxWindowStationAccess);
        GrantUserObject(desktop, appContainerSid, NativeConstants.SandboxDesktopAccess);
        Granted.TryAdd(cacheKey, 0);
    }

    private static void GrantUserObject(IntPtr handle, string appContainerSid, uint accessMask)
    {
        if (!NativeMethods.ConvertStringSidToSidW(appContainerSid, out var sid) || sid == IntPtr.Zero)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "AppContainer SID is not convertible.");
        }

        using var sidHandle = new SafeSidHandle(sid, ownsHandle: true, SidReleaseKind.LocalFree);
        uint securityInfo = NativeConstants.DACL_SECURITY_INFORMATION;
        NativeMethods.GetUserObjectSecurity(handle, ref securityInfo, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            throw new SandboxLayerException(
                SandboxError.AppContainerAclFailed,
                $"GetUserObjectSecurity size failed: {Marshal.GetLastWin32Error()}.");
        }

        var descriptor = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.GetUserObjectSecurity(handle, ref securityInfo, descriptor, needed, out _))
            {
                throw new SandboxLayerException(
                    SandboxError.AppContainerAclFailed,
                    $"GetUserObjectSecurity failed: {Marshal.GetLastWin32Error()}.");
            }

            if (!NativeMethods.GetSecurityDescriptorDacl(descriptor, out _, out var dacl, out _))
            {
                throw new SandboxLayerException(
                    SandboxError.AppContainerAclFailed,
                    $"GetSecurityDescriptorDacl failed: {Marshal.GetLastWin32Error()}.");
            }

            var entry = new EXPLICIT_ACCESS
            {
                grfAccessPermissions = accessMask,
                grfAccessMode = NativeConstants.GRANT_ACCESS,
                grfInheritance = NativeConstants.NO_INHERITANCE,
                Trustee = new TRUSTEE
                {
                    TrusteeForm = NativeConstants.TRUSTEE_IS_SID,
                    TrusteeType = NativeConstants.TRUSTEE_IS_UNKNOWN,
                    ptstrName = sidHandle.DangerousGetHandle()
                }
            };
            var setStatus = NativeMethods.SetEntriesInAclW(1, [entry], dacl, out var newAcl);
            if (setStatus != 0 || newAcl == IntPtr.Zero)
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, $"Window station SetEntriesInAclW failed: {setStatus}.");
            }

            var absolute = Marshal.AllocHGlobal(256);
            try
            {
                if (!NativeMethods.InitializeSecurityDescriptor(absolute, NativeConstants.SECURITY_DESCRIPTOR_REVISION) ||
                    !NativeMethods.SetSecurityDescriptorDacl(absolute, true, newAcl, false))
                {
                    throw new SandboxLayerException(
                        SandboxError.AppContainerAclFailed,
                        $"SetSecurityDescriptorDacl failed: {Marshal.GetLastWin32Error()}.");
                }

                if (!NativeMethods.SetUserObjectSecurity(handle, ref securityInfo, absolute))
                {
                    throw new SandboxLayerException(
                        SandboxError.AppContainerAclFailed,
                        $"SetUserObjectSecurity failed: {Marshal.GetLastWin32Error()}.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(absolute);
                NativeMethods.LocalFree(newAcl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
        }
    }

    private static bool IsReparse(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void GrantMinimum(
        string path,
        string appContainerSid,
        uint accessMask,
        bool inherit,
        ISandboxFaultInjector faultInjector)
    {
        if (faultInjector.Fault == SandboxFaultPoint.AppContainerAcl)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Injected AppContainer ACL failure.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(appContainerSid);
        var full = Path.GetFullPath(path);
        var cacheKey = string.Join(
            '|',
            appContainerSid,
            full,
            accessMask.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inherit ? "1" : "0");
        if (Granted.ContainsKey(cacheKey))
        {
            return;
        }

        if (!NativeMethods.ConvertStringSidToSidW(appContainerSid, out var sid) || sid == IntPtr.Zero)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "AppContainer SID is not convertible.");
        }

        using var sidHandle = new SafeSidHandle(sid, ownsHandle: true, SidReleaseKind.LocalFree);
        var status = NativeMethods.GetNamedSecurityInfoW(
            full,
            NativeConstants.SE_FILE_OBJECT,
            NativeConstants.DACL_SECURITY_INFORMATION,
            out _,
            out _,
            out var dacl,
            out _,
            out var descriptor);
        if (status != 0)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, $"GetNamedSecurityInfoW failed: {status}.");
        }

        try
        {
            if (dacl == IntPtr.Zero)
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Existing DACL could not be read; failing closed.");
            }

            var entry = new EXPLICIT_ACCESS
            {
                grfAccessPermissions = accessMask,
                grfAccessMode = NativeConstants.GRANT_ACCESS,
                grfInheritance = inherit ? NativeConstants.SUB_CONTAINERS_AND_OBJECTS_INHERIT : NativeConstants.NO_INHERITANCE,
                Trustee = new TRUSTEE
                {
                    pMultipleTrustee = IntPtr.Zero,
                    MultipleTrusteeOperation = 0,
                    TrusteeForm = NativeConstants.TRUSTEE_IS_SID,
                    TrusteeType = NativeConstants.TRUSTEE_IS_UNKNOWN,
                    ptstrName = sidHandle.DangerousGetHandle()
                }
            };

            var setStatus = NativeMethods.SetEntriesInAclW(1, [entry], dacl, out var newAcl);
            if (setStatus != 0 || newAcl == IntPtr.Zero)
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, $"SetEntriesInAclW failed: {setStatus}.");
            }

            try
            {
                var writeStatus = NativeMethods.SetNamedSecurityInfoW(
                    full,
                    NativeConstants.SE_FILE_OBJECT,
                    NativeConstants.DACL_SECURITY_INFORMATION,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    newAcl,
                    IntPtr.Zero);
                if (writeStatus != 0)
                {
                    throw new SandboxLayerException(SandboxError.AppContainerAclFailed, $"SetNamedSecurityInfoW failed: {writeStatus}.");
                }

                Granted.TryAdd(cacheKey, 0);
            }
            finally
            {
                NativeMethods.LocalFree(newAcl);
            }
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                NativeMethods.LocalFree(descriptor);
            }
        }
    }

    public static AclSnapshot Read(string path)
    {
        var status = NativeMethods.GetNamedSecurityInfoW(
            path,
            NativeConstants.SE_FILE_OBJECT,
            NativeConstants.DACL_SECURITY_INFORMATION,
            out _,
            out _,
            out var dacl,
            out _,
            out var descriptor);
        if (status != 0 || dacl == IntPtr.Zero)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, $"ACL read failed: {status}.");
        }

        try
        {
            if (!NativeMethods.GetAclInformation(dacl, out var info, Marshal.SizeOf<ACL_SIZE_INFORMATION>(), 2))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, $"GetAclInformation failed: {Marshal.GetLastWin32Error()}.");
            }

            List<AclAce> aces = [];
            for (uint i = 0; i < info.AceCount; i++)
            {
                if (!NativeMethods.GetAce(dacl, i, out var ace) || ace == IntPtr.Zero)
                {
                    continue;
                }

                var header = Marshal.PtrToStructure<ACE_HEADER>(ace);
                if (header.AceType != NativeConstants.ACCESS_ALLOWED_ACE_TYPE)
                {
                    continue;
                }

                var mask = (uint)Marshal.ReadInt32(ace, Marshal.SizeOf<ACE_HEADER>());
                var sid = ace + Marshal.SizeOf<ACE_HEADER>() + 4;
                aces.Add(new AclAce(NativeSid.ToStringSid(sid), mask, header.AceFlags));
            }

            return new AclSnapshot(aces);
        }
        finally
        {
            NativeMethods.LocalFree(descriptor);
        }
    }
}

internal sealed record AclAce(string Sid, uint Mask, byte Flags);

internal sealed record AclSnapshot(IReadOnlyList<AclAce> AllowedAces)
{
    public bool Grants(string sid, uint requiredMask) =>
        AllowedAces.Any(ace =>
            StringComparer.OrdinalIgnoreCase.Equals(ace.Sid, sid) &&
            (ace.Mask & requiredMask) == requiredMask);

    public bool ContainsSid(string sid) =>
        AllowedAces.Any(ace => StringComparer.OrdinalIgnoreCase.Equals(ace.Sid, sid));

    public bool GrantsGenericAll(string sid) =>
        AllowedAces.Any(ace =>
            StringComparer.OrdinalIgnoreCase.Equals(ace.Sid, sid) &&
            (ace.Mask & NativeConstants.GENERIC_ALL) == NativeConstants.GENERIC_ALL);

    public bool ContainsWellKnownBroadGrant()
    {
        foreach (var ace in AllowedAces)
        {
            if (ace.Sid.Equals("S-1-1-0", StringComparison.OrdinalIgnoreCase) ||
                ace.Sid.Equals("S-1-5-32-545", StringComparison.OrdinalIgnoreCase) ||
                ace.Sid.Equals("S-1-5-11", StringComparison.OrdinalIgnoreCase))
            {
                if ((ace.Mask & NativeConstants.GENERIC_ALL) == NativeConstants.GENERIC_ALL ||
                    (ace.Mask & NativeConstants.SandboxWorkAccess) == NativeConstants.SandboxWorkAccess)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
