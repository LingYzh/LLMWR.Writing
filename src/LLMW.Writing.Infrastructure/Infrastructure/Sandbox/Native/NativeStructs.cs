using System.Runtime.InteropServices;

namespace LLMW.Writing.Infrastructure.Sandbox.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct SID_AND_ATTRIBUTES
{
    public IntPtr Sid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SECURITY_CAPABILITIES
{
    public IntPtr AppContainerSid;
    public IntPtr Capabilities;
    public uint CapabilityCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFOW
{
    public uint cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public uint dwX;
    public uint dwY;
    public uint dwXSize;
    public uint dwYSize;
    public uint dwXCountChars;
    public uint dwYCountChars;
    public uint dwFillAttribute;
    public uint dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFOEXW
{
    public STARTUPINFOW StartupInfo;
    public IntPtr lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SECURITY_ATTRIBUTES
{
    public uint nLength;
    public IntPtr lpSecurityDescriptor;
    public int bInheritHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
{
    public uint ControlFlags;
    public uint CpuRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID_AND_ATTRIBUTES
{
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_ELEVATION
{
    public uint TokenIsElevated;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EXPLICIT_ACCESS
{
    public uint grfAccessPermissions;
    public uint grfAccessMode;
    public uint grfInheritance;
    public TRUSTEE Trustee;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRUSTEE
{
    public IntPtr pMultipleTrustee;
    public uint MultipleTrusteeOperation;
    public uint TrusteeForm;
    public uint TrusteeType;
    public IntPtr ptstrName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ACL_SIZE_INFORMATION
{
    public uint AceCount;
    public uint AclBytesInUse;
    public uint AclBytesFree;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ACE_HEADER
{
    public byte AceType;
    public byte AceFlags;
    public ushort AceSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OBJECT_ATTRIBUTES
{
    public uint Length;
    public IntPtr RootDirectory;
    public IntPtr ObjectName;
    public uint Attributes;
    public IntPtr SecurityDescriptor;
    public IntPtr SecurityQualityOfService;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_STATUS_BLOCK
{
    public IntPtr Status;
    public IntPtr Information;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_BASIC_INFORMATION
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ATTRIBUTE_TAG_INFORMATION
{
    public uint FileAttributes;
    public uint ReparseTag;
}
