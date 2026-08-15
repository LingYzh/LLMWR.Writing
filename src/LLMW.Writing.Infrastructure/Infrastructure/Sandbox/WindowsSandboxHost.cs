using System.Runtime.Versioning;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Infrastructure.Sandbox;

public static class SandboxHostFactory
{
    public static ISandboxHost Create(
        string projectRoot,
        ProjectScope projectScope,
        string selfTestExecutable,
        ISandboxFaultInjector? faultInjector = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UnsupportedSandboxHost.Instance;
        }

        return CreateWindows(projectRoot, projectScope, selfTestExecutable, faultInjector);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsSandboxHost CreateWindows(
        string projectRoot,
        ProjectScope projectScope,
        string selfTestExecutable,
        ISandboxFaultInjector? faultInjector) =>
        new WindowsSandboxHost(projectRoot, projectScope, selfTestExecutable, faultInjector);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxHost : ISandboxHost
{
    private readonly string projectRoot;
    private readonly ProjectScope projectScope;
    private readonly string selfTestExecutable;
    private readonly ISandboxFaultInjector faultInjector;
    private readonly object gate = new();
    private SandboxAvailability availability;
    private SandboxIdentity? identity;
    private SandboxError? initError;
    private string? initializationDetail;
    private bool initialized;

    public WindowsSandboxHost(
        string projectRoot,
        ProjectScope projectScope,
        string selfTestExecutable,
        ISandboxFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(selfTestExecutable);
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.projectScope = projectScope;
        this.selfTestExecutable = Path.GetFullPath(selfTestExecutable);
        this.faultInjector = faultInjector ?? NoSandboxFaultInjector.Instance;
        availability = SandboxAvailability.Unavailable;
    }

    public SandboxError? InitializationError
    {
        get
        {
            EnsureInitialized();
            return initError;
        }
    }

    public SandboxAvailability Availability
    {
        get
        {
            EnsureInitialized();
            return availability;
        }
    }

    public SandboxIdentity? Identity
    {
        get
        {
            EnsureInitialized();
            return identity;
        }
    }

    public SandboxExecutionResult Execute(SandboxExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitialized();
        if (availability is not SandboxAvailability.Available || identity is null)
        {
            return SandboxExecutionResult.Fail(request, initError ?? SandboxError.SandboxUnavailable, initializationDetail);
        }

        if (!SandboxPathPolicy.PathsEqual(request.ProjectRoot, projectRoot) ||
            !string.Equals(
                request.Binding.ProjectScope.ToCanonicalValue(),
                projectScope.ToCanonicalValue(),
                StringComparison.Ordinal))
        {
            return SandboxExecutionResult.Fail(request, SandboxError.PathOutOfScope, "Caller project claims do not match Core sandbox context.");
        }

        try
        {
            var work = SafeSandboxHierarchy.EnsureRunWorkDirectory(projectRoot, request.Binding.RunId);
            return WindowsSandboxProcessLauncher.Launch(request, identity, work, projectRoot, faultInjector, grantInternetClient: false);
        }
        catch (SandboxLayerException exception)
        {
            return SandboxExecutionResult.Fail(request, exception.Error, exception.Message, identity.AppContainerSid);
        }
    }

    internal SandboxExecutionResult ExecuteWithOptionalNetworkCapability(SandboxExecutionRequest request, bool grantInternetClient)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitialized();
        if (availability is not SandboxAvailability.Available || identity is null)
        {
            return SandboxExecutionResult.Fail(request, initError ?? SandboxError.SandboxUnavailable, initializationDetail);
        }

        try
        {
            var work = SafeSandboxHierarchy.EnsureRunWorkDirectory(projectRoot, request.Binding.RunId);
            return WindowsSandboxProcessLauncher.Launch(request, identity, work, projectRoot, faultInjector, grantInternetClient);
        }
        catch (SandboxLayerException exception)
        {
            return SandboxExecutionResult.Fail(request, exception.Error, exception.Message, identity.AppContainerSid);
        }
    }

    internal LiveSandboxLaunch StartLive(SandboxExecutionRequest request)
    {
        EnsureInitialized();
        if (availability is not SandboxAvailability.Available || identity is null)
        {
            throw new SandboxLayerException(initError ?? SandboxError.SandboxUnavailable, initializationDetail ?? "Sandbox is not available.");
        }

        var work = SafeSandboxHierarchy.EnsureRunWorkDirectory(projectRoot, request.Binding.RunId);
        return WindowsSandboxProcessLauncher.LaunchLive(request, identity, work, projectRoot, faultInjector, grantInternetClient: false);
    }

    private void EnsureInitialized()
    {
        lock (gate)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            try
            {
                if (faultInjector.Fault == SandboxFaultPoint.SelfTest)
                {
                    availability = SandboxAvailability.Unavailable;
                    initError = SandboxError.SandboxSelfTestFailed;
                    initializationDetail = "Injected sandbox self-test failure.";
                    return;
                }

                identity = AppContainerProfileManager.CreateOrDerive(projectScope.ProjectId, faultInjector);
                AppContainerNetworkIsolation.EnsureLoopbackNotExempt(identity.AppContainerSid, faultInjector);
                SafeSandboxHierarchy.EnsureSandboxRoot(projectRoot);
                var selfTestWork = SafeSandboxHierarchy.EnsureRunWorkDirectory(projectRoot, "self-test");
                var selfTest = WindowsSandboxProcessLauncher.Launch(
                    new SandboxExecutionRequest(
                        SandboxLaunchBinding.Create("self-test", "self-test-worker", projectScope, "self-test"),
                        new TrustedNativePrincipalSource("sandbox-self-test").ResolveUserInteractive(),
                        Capability.ShellExecute,
                        selfTestExecutable,
                        ["whoami-token"],
                        projectRoot,
                        TimeSpan.FromSeconds(15)),
                    identity,
                    selfTestWork,
                    projectRoot,
                    faultInjector,
                    grantInternetClient: false);
                if (!selfTest.Succeeded || selfTest.ExitCode != 0 ||
                    !HasJsonFlag(selfTest.Stdout, "hasRestrictions", true) ||
                    !HasJsonFlag(selfTest.Stdout, "isAppContainer", true) ||
                    HasJsonFlag(selfTest.Stdout, "elevated", true) ||
                    !HasJsonFlag(selfTest.Stdout, "inJob", true) ||
                    !selfTest.Stdout.Contains(identity.AppContainerSid, StringComparison.OrdinalIgnoreCase))
                {
                    availability = SandboxAvailability.InitializationFailed;
                    initError = selfTest.Error ?? SandboxError.SandboxSelfTestFailed;
                    initializationDetail =
                        $"self-test error={selfTest.Error} deny={selfTest.DenyReason} exit={selfTest.ExitCode} stdout={selfTest.Stdout} stderr={selfTest.Stderr}";
                    return;
                }

                availability = SandboxAvailability.Available;
            }
            catch (SandboxLayerException exception)
            {
                availability = exception.Error == SandboxError.PlatformUnsupported
                    ? SandboxAvailability.UnsupportedPlatform
                    : SandboxAvailability.InitializationFailed;
                initError = exception.Error;
                initializationDetail = exception.Message;
            }
        }
    }

    private static bool HasJsonFlag(string json, string name, bool expected)
    {
        var needleTrue = "\"" + name + "\":true";
        var needleTrueSpaced = "\"" + name + "\": true";
        var needleFalse = "\"" + name + "\":false";
        var needleFalseSpaced = "\"" + name + "\": false";
        var hasTrue = json.Contains(needleTrue, StringComparison.OrdinalIgnoreCase) ||
                      json.Contains(needleTrueSpaced, StringComparison.OrdinalIgnoreCase);
        var hasFalse = json.Contains(needleFalse, StringComparison.OrdinalIgnoreCase) ||
                       json.Contains(needleFalseSpaced, StringComparison.OrdinalIgnoreCase);
        return expected ? hasTrue && !hasFalse : hasFalse;
    }
}
