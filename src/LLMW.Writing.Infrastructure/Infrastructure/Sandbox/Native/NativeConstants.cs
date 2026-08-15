namespace LLMW.Writing.Infrastructure.Sandbox.Native;

internal static class NativeConstants
{
    public const uint DISABLE_MAX_PRIVILEGE = 0x1;
    public const uint SANDBOX_INERT = 0x2;
    public const uint LUA_TOKEN = 0x4;
    public const uint WRITE_RESTRICTED = 0x8;

    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_IMPERSONATE = 0x0004;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    public const uint TOKEN_ALL_ACCESS_WIN8 = 0x000F01FF;

    public const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x00000010;
    public const uint SE_GROUP_ENABLED = 0x00000004;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    public const int TokenUser = 1;
    public const int TokenPrivileges = 3;
    public const int TokenRestrictedSids = 11;
    public const int TokenElevation = 20;
    public const int TokenHasRestrictions = 21;
    public const int TokenIntegrityLevel = 25;
    public const int TokenIsAppContainer = 29;
    public const int TokenAppContainerSid = 31;
    public const int TokenIsRestricted = 40;

    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint LOGON_WITH_PROFILE = 1;
    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint HANDLE_FLAG_INHERIT = 0x00000001;

    public const uint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    public const uint PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x00020009;
    public const uint PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY = 0x0002000F;
    public const uint PROCESS_CREATION_ALL_APPLICATION_PACKAGES_OPT_OUT = 1;

    public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    public const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    public const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const int JobObjectExtendedLimitInformation = 9;
    public const int JobObjectCpuRateControlInformation = 15;
    public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_EXECUTE = 0x20000000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint DELETE = 0x00010000;
    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_WRITE_DATA = 0x0002;
    public const uint FILE_APPEND_DATA = 0x0004;
    public const uint FILE_READ_EA = 0x0008;
    public const uint FILE_WRITE_EA = 0x0010;
    public const uint FILE_EXECUTE = 0x0020;
    public const uint FILE_DELETE_CHILD = 0x0040;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint STANDARD_RIGHTS_READ = 0x00020000;
    public const uint STANDARD_RIGHTS_WRITE = 0x00020000;
    public const uint STANDARD_RIGHTS_EXECUTE = 0x00020000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint OPEN_EXISTING = 3;
    public const uint CREATE_ALWAYS = 2;

    public const uint FILE_GENERIC_READ =
        STANDARD_RIGHTS_READ | FILE_READ_DATA | FILE_READ_ATTRIBUTES | FILE_READ_EA | SYNCHRONIZE;
    public const uint FILE_GENERIC_WRITE =
        STANDARD_RIGHTS_WRITE | FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES | FILE_WRITE_EA | FILE_APPEND_DATA | SYNCHRONIZE;
    public const uint FILE_GENERIC_EXECUTE =
        STANDARD_RIGHTS_EXECUTE | FILE_READ_ATTRIBUTES | FILE_EXECUTE | SYNCHRONIZE;

    public const string RestrictedCodeSid = "S-1-5-12";
    public const string BuiltinUsersSid = "S-1-5-32-545";
    public const uint SandboxWorkAccess =
        FILE_GENERIC_READ | FILE_GENERIC_WRITE | FILE_GENERIC_EXECUTE | DELETE | FILE_DELETE_CHILD;
    public const uint SandboxExecuteAccess = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE | FILE_LIST_DIRECTORY;

    public const uint GRANT_ACCESS = 1;
    public const uint SUB_CONTAINERS_AND_OBJECTS_INHERIT = 0x3;
    public const uint NO_INHERITANCE = 0;
    public const uint SE_FILE_OBJECT = 1;
    public const uint DACL_SECURITY_INFORMATION = 0x00000004;
    public const uint TRUSTEE_IS_SID = 0;
    public const uint TRUSTEE_IS_UNKNOWN = 0;
    public const uint ACCESS_ALLOWED_ACE_TYPE = 0;
    public const uint GENERIC_ALL = 0x10000000;
    public const uint SECURITY_DESCRIPTOR_REVISION = 1;

    public const uint WINSTA_ENUMDESKTOPS = 0x0001;
    public const uint WINSTA_READATTRIBUTES = 0x0002;
    public const uint WINSTA_ACCESSCLIPBOARD = 0x0004;
    public const uint WINSTA_CREATEDESKTOP = 0x0008;
    public const uint WINSTA_WRITEATTRIBUTES = 0x0010;
    public const uint WINSTA_ACCESSGLOBALATOMS = 0x0020;
    public const uint WINSTA_EXITWINDOWS = 0x0040;
    public const uint WINSTA_ENUMERATE = 0x0100;
    public const uint WINSTA_READSCREEN = 0x0200;
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_CREATEWINDOW = 0x0002;
    public const uint DESKTOP_CREATEMENU = 0x0004;
    public const uint DESKTOP_HOOKCONTROL = 0x0008;
    public const uint DESKTOP_JOURNALRECORD = 0x0010;
    public const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
    public const uint DESKTOP_ENUMERATE = 0x0040;
    public const uint DESKTOP_WRITEOBJECTS = 0x0080;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    public const uint SandboxWindowStationAccess =
        WINSTA_ENUMDESKTOPS | WINSTA_READATTRIBUTES | WINSTA_ACCESSCLIPBOARD | WINSTA_CREATEDESKTOP |
        WINSTA_WRITEATTRIBUTES | WINSTA_ACCESSGLOBALATOMS | WINSTA_EXITWINDOWS | WINSTA_ENUMERATE |
        WINSTA_READSCREEN | STANDARD_RIGHTS_READ | STANDARD_RIGHTS_WRITE;
    public const uint SandboxDesktopAccess =
        DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU | DESKTOP_HOOKCONTROL |
        DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | STANDARD_RIGHTS_READ | STANDARD_RIGHTS_WRITE;

    public const int WinBuiltinAdministratorsSid = 26;
    public const int WinBuiltinUsersSid = 27;
    public const int WinBuiltinPowerUsersSid = 29;
    public const int WinRestrictedCodeSid = 18;
    public const int WinWorldSid = 1;
    public const int WinAccountAdministratorSid = 38;

    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int WAIT_TIMEOUT = 258;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint INFINITE = 0xFFFFFFFF;

    public const uint OBJ_CASE_INSENSITIVE = 0x00000040;
    public const uint FILE_DIRECTORY_FILE = 0x00000001;
    public const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    public const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
    public const uint FILE_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000;
    public const uint FILE_OPEN = 1;
    public const uint FILE_CREATE = 2;
    public const uint FILE_OPEN_IF = 3;
    public const uint FILE_OVERWRITE_IF = 5;

    public const int FileBasicInformation = 4;
    public const int FileAttributeTagInformation = 35;

    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const int TokenImpersonation = 2;

    public const int HRESULT_ERROR_ALREADY_EXISTS = unchecked((int)0x800700B7);
    public const uint VOLUME_NAME_DOS = 0x0;
}
