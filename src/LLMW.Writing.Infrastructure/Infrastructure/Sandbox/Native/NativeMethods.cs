using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox.Native;

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(true)
    {
    }

    public SafeJobHandle(IntPtr handle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal sealed class SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeProcThreadAttributeList()
        : base(true)
    {
    }

    public SafeProcThreadAttributeList(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.DeleteProcThreadAttributeList(handle);
        Marshal.FreeHGlobal(handle);
        return true;
    }
}

internal sealed class SafeLocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeLocalAllocHandle()
        : base(true)
    {
    }

    public SafeLocalAllocHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return NativeMethods.LocalFree(handle) == IntPtr.Zero;
    }
}

internal enum SidReleaseKind
{
    None,
    LocalFree,
    FreeSid,
    MarshalFree
}

internal sealed class SafeSidHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly SidReleaseKind releaseKind;

    public SafeSidHandle(IntPtr handle, bool ownsHandle, SidReleaseKind releaseKind)
        : base(ownsHandle)
    {
        this.releaseKind = releaseKind;
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        switch (releaseKind)
        {
            case SidReleaseKind.LocalFree:
                return NativeMethods.LocalFree(handle) == IntPtr.Zero;
            case SidReleaseKind.FreeSid:
                NativeMethods.FreeSid(handle);
                return true;
            case SidReleaseKind.MarshalFree:
                Marshal.FreeHGlobal(handle);
                return true;
            default:
                return true;
        }
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        in JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
        int infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        in JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info,
        int infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
        int infoLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle? job, out bool result);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr CreateWindowStationW(
        [MarshalAs(UnmanagedType.LPWStr)] string? name,
        uint flags,
        uint desiredAccess,
        IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr OpenWindowStationW(string name, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseWindowStation(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessWindowStation(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr CreateDesktopW(
        string desktop,
        IntPtr device,
        IntPtr deviceMode,
        uint flags,
        uint desiredAccess,
        IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenDesktopW(string desktop, uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetUserObjectInformationW(
        IntPtr handle,
        int index,
        IntPtr information,
        uint length,
        out uint lengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetUserObjectSecurity(
        IntPtr handle,
        ref uint securityInfo,
        IntPtr securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetUserObjectSecurity(
        IntPtr handle,
        ref uint securityInfo,
        IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        out bool daclPresent,
        out IntPtr dacl,
        out bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool InitializeSecurityDescriptor(IntPtr securityDescriptor, uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool SetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        bool daclPresent,
        IntPtr dacl,
        bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existing,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        [In] SID_AND_ATTRIBUTES[]? sidsToDisable,
        uint deletePrivilegeCount,
        IntPtr privilegesToDelete,
        uint restrictedSidCount,
        [In] SID_AND_ATTRIBUTES[]? sidsToRestrict,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CreateWellKnownSid(
        int wellKnownSidType,
        IntPtr domainSid,
        IntPtr sid,
        ref int cbSid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    internal static extern int GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CopySid(int destinationLength, IntPtr destination, IntPtr source);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        bool disableAll,
        ref TOKEN_PRIVILEGES newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        bool disableAll,
        ref TOKEN_PRIVILEGES newState,
        int bufferLength,
        IntPtr previousState,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool LookupPrivilegeNameW(string? systemName, ref LUID luid, char[] name, ref int nameLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token,
        string? applicationName,
        [In][Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEXW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string? applicationName,
        [In][Out] char[] commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEXW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        IntPtr capabilities,
        uint capabilityCount,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetAppContainerFolderPath(string pszAppContainerSid, out IntPtr ppszPath);

    [DllImport("kernel32.dll")]
    internal static extern void SetLastError(uint errorCode);

    [DllImport("kernelbase.dll", EntryPoint = "CreateAppContainerToken", SetLastError = true)]
    internal static extern bool CreateAppContainerToken(
        SafeAccessTokenHandle tokenHandle,
        in SECURITY_CAPABILITIES securityCapabilities,
        out SafeAccessTokenHandle appContainerToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool ImpersonateLoggedOnUser(SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessW(
        string? applicationName,
        [In][Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEXW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool DeriveCapabilitySidsFromName(
        string capabilityName,
        out IntPtr capabilityGroupSids,
        out uint capabilityGroupSidCount,
        out IntPtr capabilitySids,
        out uint capabilitySidCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        UIntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SECURITY_ATTRIBUTES pipeAttributes,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetNamedSecurityInfoW(
        string objectName,
        uint objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint SetNamedSecurityInfoW(
        string objectName,
        uint objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint SetEntriesInAclW(
        uint count,
        [In] EXPLICIT_ACCESS[] entries,
        IntPtr oldAcl,
        out IntPtr newAcl);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetAclInformation(
        IntPtr acl,
        out ACL_SIZE_INFORMATION info,
        int length,
        int classInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);

    [DllImport("ntdll.dll")]
    internal static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref OBJECT_ATTRIBUTES objectAttributes,
        ref IO_STATUS_BLOCK ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationFile(
        SafeFileHandle fileHandle,
        ref IO_STATUS_BLOCK ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(
        SafeFileHandle handle,
        byte[] buffer,
        int bytesToRead,
        out int bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(
        SafeFileHandle handle,
        byte[] buffer,
        int bytesToWrite,
        out int bytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateSymbolicLinkW(string symlinkFileName, string targetFileName, uint flags);

    [DllImport("FirewallAPI.dll")]
    internal static extern uint NetworkIsolationGetAppContainerConfig(out uint pdwNumPublicAppCs, out IntPtr appContainerSids);

    [DllImport("FirewallAPI.dll")]
    internal static extern uint NetworkIsolationSetAppContainerConfig(uint dwNumPublicAppCs, [In] SID_AND_ATTRIBUTES[]? appContainerSids);

    [DllImport("FirewallAPI.dll")]
    internal static extern uint NetworkIsolationFreeAppContainers(IntPtr pPublicAppCs);
}
