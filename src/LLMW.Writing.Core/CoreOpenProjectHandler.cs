using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Sandbox;

namespace LLMW.Writing.Core;

internal sealed class CoreOpenProjectHandler : IIpcApplicationCommandHandler
{
    private readonly object gate = new();
    private readonly MutableIpcCommandHandler commands;
    private readonly ITrustedIpcBindingRegistry bindings;
    private readonly IpcEventRing eventRing;
    private readonly string workspaceInstanceId;
    private readonly string runtimeWorkerInstanceId;
    private readonly string runtimeChannelInstanceId;
    private readonly TrustedNativePrincipalSource nativeUi;
    private bool opened;

    public CoreOpenProjectHandler(
        MutableIpcCommandHandler commands,
        ITrustedIpcBindingRegistry bindings,
        IpcEventRing eventRing,
        string workspaceInstanceId,
        string runtimeWorkerInstanceId,
        string runtimeChannelInstanceId,
        TrustedNativePrincipalSource nativeUi)
    {
        this.commands = commands;
        this.bindings = bindings;
        this.eventRing = eventRing;
        this.workspaceInstanceId = workspaceInstanceId;
        this.runtimeWorkerInstanceId = runtimeWorkerInstanceId;
        this.runtimeChannelInstanceId = runtimeChannelInstanceId;
        this.nativeUi = nativeUi;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        if (context.SemanticType != IpcSemanticTypes.OpenProject)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(null);
        }

        if (context.ClientKind != IpcClientKind.Ui)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(Error(context, IpcErrorCodes.RuntimeManagementDenied, "OpenProject requires the UI channel."));
        }

        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.OpenProjectRequest);
        lock (gate)
        {
            if (opened)
            {
                return Task.FromResult<IpcApplicationCommandResult?>(Error(context, IpcErrorCodes.ProtocolViolation, "A project is already open in this Core process."));
            }

            if (!TryBindTrustedRoot(request.RequestedPath, out var trustedRoot, out var deny))
            {
                return Task.FromResult<IpcApplicationCommandResult?>(Error(context, IpcErrorCodes.BindingMismatch, deny));
            }

            var databasePath = Path.Combine(trustedRoot, "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp12", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var projectId = ProjectIdFromTrustedRoot(trustedRoot);
            var scope = new ProjectScope(projectId, workspaceInstanceId);
            bindings.Register(new TrustedIpcLaunchRecord(
                AuthenticatedClientKind.AgentRuntime,
                runtimeWorkerInstanceId,
                runtimeChannelInstanceId,
                scope));

            var sessionStore = new SqliteRunSessionStore(databasePath);
            var store = new SqliteRuntimeStore(databasePath);
            var sessions = new RunSessionService(sessionStore);
            var sandboxHost = CreateSandboxHost(trustedRoot, scope);
            var broker = new TrustedSandboxBroker(
                new CoreAuthorizationService(),
                sandboxHost,
                UnavailableSandboxPathGuard.Instance,
                new SandboxProjectContext(trustedRoot, scope),
                sessionRevalidator: new RunSessionRevalidator(sessionStore));
            var workerExe = Path.Combine(AppContext.BaseDirectory, "worker", "LLMW.Writing.Worker.exe");
            var supervisor = new CoreRunWorkerSupervisor(
                broker,
                sandboxHost,
                bindings,
                eventRing,
                workspaceInstanceId,
                workerExe,
                nativeUi.ResolveUserInteractive(),
                scope);
            var scheduler = new RuntimeSchedulerService(
                store,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                SystemSecurityClock.Instance,
                supervisor,
                new CoreAuthorizationService(),
                bindings: bindings,
                sessions: sessions);
            commands.Inner = new CompositeIpcCommandHandler(this, new RuntimeIpcCommandHandler(scheduler, workspaceInstanceId));
            opened = true;
            var response = IpcJson.Serialize(
                IpcEnvelopeFactory.Create(
                    IpcMessageType.Response,
                    IpcSemanticTypes.OpenProject,
                    workspaceInstanceId,
                    new OpenProjectResponse(projectId.ToString("D")),
                    context.EnvelopeProjectId,
                    context.EnvelopeRunId,
                    context.CorrelationId,
                    context.RequestId),
                IpcJsonContext.Default.OpenProjectResponseEnvelope);
            return Task.FromResult<IpcApplicationCommandResult?>(new IpcApplicationCommandResult(response));
        }
    }

    private static bool TryBindTrustedRoot(string requestedPath, out string trustedRoot, out string deny)
    {
        trustedRoot = "";
        deny = "Caller ProjectRoot is not security truth; Core could not bind a trusted existing project directory.";
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
            if (!Directory.Exists(full))
            {
                deny = "The requested project directory does not exist.";
                return false;
            }

            trustedRoot = full;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            deny = "The requested project path could not be canonicalized.";
            return false;
        }
    }

    private static Guid ProjectIdFromTrustedRoot(string trustedRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trustedRoot));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var id = new Guid(bytes);
        return id == Guid.Empty ? Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab") : id;
    }

    private static ISandboxHost CreateSandboxHost(string trustedRoot, ProjectScope scope)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UnsupportedSandboxHost.Instance;
        }

        var probe = Path.Combine(AppContext.BaseDirectory, "sandbox-probe", "LLMW.Writing.SandboxProbe.exe");
        if (!File.Exists(probe))
        {
            return new UnavailableSandboxHost(SandboxError.SandboxUnavailable);
        }

        return SandboxHostFactory.Create(trustedRoot, scope, probe);
    }

    private IpcApplicationCommandResult Error(IpcApplicationCommandContext context, string code, string message) =>
        new(IpcJson.Serialize(
            IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                context.SemanticType,
                workspaceInstanceId,
                new IpcError(code, message, null, false),
                context.EnvelopeProjectId,
                context.EnvelopeRunId,
                context.CorrelationId,
                context.RequestId),
            IpcJsonContext.Default.ErrorEnvelope));
}
