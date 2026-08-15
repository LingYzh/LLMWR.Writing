namespace LLMW.Writing.Application.Security.Sandbox;

public enum SandboxAvailability
{
    Available,
    Unavailable,
    InitializationFailed,
    UnsupportedPlatform
}

public enum SandboxError
{
    PlatformUnsupported,
    SandboxUnavailable,
    RestrictedTokenFailed,
    AppContainerProfileFailed,
    AppContainerAclFailed,
    SecurityCapabilitiesFailed,
    JobCreationFailed,
    JobConfigurationFailed,
    JobAssignmentFailed,
    ProcessLaunchFailed,
    SandboxSelfTestFailed,
    CapabilityDenied,
    ApprovalRequired,
    TrustRequired,
    PathOutOfScope,
    ReparsePointRejected,
    NetworkDenied,
    CredentialAccessDenied,
    Timeout,
    ProcessLimitExceeded,
    MemoryLimitExceeded,
    BrokerUnavailable,
    SessionBindingMismatch,
    SessionRevoked,
    SessionExpired,
    EnvironmentRejected
}

public enum SandboxFaultPoint
{
    None,
    RestrictedTokenInit,
    AppContainerProfile,
    AppContainerAcl,
    SecurityCapabilities,
    CreateProcess,
    JobCreation,
    JobConfiguration,
    JobAssignment,
    SelfTest,
    BrokerUnavailable,
    RunSessionRevalidation,
    NetworkIsolationQuery,
    NetworkIsolationSet,
    PrivilegeScopedEnable,
    CpuJobConfiguration
}

public enum SandboxPathClass
{
    DesignatedWorkSurface,
    LlmwInternals,
    ProjectSensitive,
    OutsideProject,
    SystemProtected,
    ReparseRejected
}
