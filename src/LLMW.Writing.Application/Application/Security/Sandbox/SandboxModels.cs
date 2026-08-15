using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security.Sandbox;

public sealed record SandboxResourceLimits(
    long ProcessMemoryBytes,
    int ActiveProcessLimit,
    int? CpuRateHundredthsPercent)
{
    public const long DefaultProcessMemoryBytes = 256L * 1024 * 1024;
    public const int DefaultActiveProcessLimit = 16;

    public static SandboxResourceLimits Default { get; } = new(
        DefaultProcessMemoryBytes,
        DefaultActiveProcessLimit,
        CpuRateHundredthsPercent: null);

    public SandboxResourceLimits()
        : this(DefaultProcessMemoryBytes, DefaultActiveProcessLimit, null)
    {
    }
}

public sealed record SandboxIdentity(string AppContainerName, string AppContainerSid);

public sealed record SandboxLaunchBinding(
    string RunId,
    string WorkerInstanceId,
    ProjectScope ProjectScope,
    string PolicySnapshotReference)
{
    public static SandboxLaunchBinding Create(
        string runId,
        string workerInstanceId,
        ProjectScope projectScope,
        string policySnapshotReference = "wp09")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerInstanceId);
        ArgumentNullException.ThrowIfNull(projectScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(policySnapshotReference);
        return new SandboxLaunchBinding(runId, workerInstanceId, projectScope, policySnapshotReference);
    }
}

public sealed record SandboxExecutionRequest(
    SandboxLaunchBinding Binding,
    CallerPrincipal Principal,
    Capability Capability,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string ProjectRoot,
    TimeSpan Timeout,
    bool NetworkRequired = false,
    SandboxResourceLimits? Limits = null,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public SandboxResourceLimits EffectiveLimits => Limits ?? SandboxResourceLimits.Default;

    public TimeSpan EffectiveTimeout => Timeout <= TimeSpan.Zero ? DefaultTimeout : Timeout;
}

public sealed record SandboxExecutionResult(
    bool Succeeded,
    SandboxError? Error,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut,
    string RunId,
    string WorkerInstanceId,
    string? SandboxIdentity,
    string ExecutableIdentity,
    Capability Capability,
    string? DenyReason,
    int? ProcessId = null)
{
    public static SandboxExecutionResult Fail(
        SandboxExecutionRequest request,
        SandboxError error,
        string? denyReason = null,
        string? sandboxIdentity = null) =>
        new(
            false,
            error,
            null,
            "",
            "",
            false,
            false,
            error == SandboxError.Timeout,
            request.Binding.RunId,
            request.Binding.WorkerInstanceId,
            sandboxIdentity,
            request.ExecutablePath,
            request.Capability,
            denyReason ?? error.ToString());
}

public sealed record SandboxFileReadRequest(
    CallerPrincipal Principal,
    ProjectScope ProjectScope,
    string ProjectRoot,
    string LogicalRelativePath,
    string RunId);

public sealed record SandboxFileReadResult(bool Succeeded, SandboxError? Error, byte[]? Bytes, string? DenyReason)
{
    public static SandboxFileReadResult Fail(SandboxError error, string? denyReason = null) =>
        new(false, error, null, denyReason ?? error.ToString());

    public static SandboxFileReadResult Ok(byte[] bytes) => new(true, null, bytes, null);
}

public sealed record SandboxFileWriteRequest(
    CallerPrincipal Principal,
    ProjectScope ProjectScope,
    string ProjectRoot,
    string LogicalRelativePath,
    string RunId,
    ReadOnlyMemory<byte> Contents);

public sealed record SandboxFileWriteResult(bool Succeeded, SandboxError? Error, string? DenyReason)
{
    public static SandboxFileWriteResult Fail(SandboxError error, string? denyReason = null) =>
        new(false, error, denyReason ?? error.ToString());

    public static SandboxFileWriteResult Ok() => new(true, null, null);
}

public sealed record CommandFingerprint(string CanonicalExecutable, IReadOnlyList<string> Arguments, string Digest)
{
    public override string ToString() => Digest;
}
