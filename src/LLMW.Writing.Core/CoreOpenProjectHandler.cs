using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.History;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Docx;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Sandbox;
using LLMW.Writing.Infrastructure.Specialists;

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
    private readonly ProjectRunSessionServiceHolder runSessions;
    private readonly EditorRuntimeHolder editors;
    private bool opened;

    public CoreOpenProjectHandler(
        MutableIpcCommandHandler commands,
        ITrustedIpcBindingRegistry bindings,
        IpcEventRing eventRing,
        string workspaceInstanceId,
        string runtimeWorkerInstanceId,
        string runtimeChannelInstanceId,
        TrustedNativePrincipalSource nativeUi,
        ProjectRunSessionServiceHolder runSessions,
        EditorRuntimeHolder editors)
    {
        this.commands = commands;
        this.bindings = bindings;
        this.eventRing = eventRing;
        this.workspaceInstanceId = workspaceInstanceId;
        this.runtimeWorkerInstanceId = runtimeWorkerInstanceId;
        this.runtimeChannelInstanceId = runtimeChannelInstanceId;
        this.nativeUi = nativeUi;
        this.runSessions = runSessions;
        this.editors = editors;
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

            var bind = ExistingProjectPreflight.TryBind(request.RequestedPath);
            if (!bind.Succeeded)
            {
                return Task.FromResult<IpcApplicationCommandResult?>(
                    Error(context, IpcErrorCodes.BindingMismatch, bind.DenyReason ?? "Existing project preflight failed."));
            }

            var previousInner = commands.Inner;
            RunSessionService? published = null;
            EditorRuntime? publishedEditor = null;
            var runtimeBindingInstalled = false;
            try
            {
                var scope = new ProjectScope(bind.ProjectId, workspaceInstanceId);
                var sessionStore = new SqliteRunSessionStore(bind.DatabasePath);
                var store = new SqliteRuntimeStore(bind.DatabasePath);
                var sessions = new RunSessionService(sessionStore);
                var oversightBinder = new LateBoundOversightSource();
                var authorization = new CoreAuthorizationService(oversightSource: oversightBinder);
                var sandboxHost = CreateSandboxHost(bind.CanonicalRoot, scope);
                var broker = new TrustedSandboxBroker(
                    authorization,
                    sandboxHost,
                    UnavailableSandboxPathGuard.Instance,
                    new SandboxProjectContext(bind.CanonicalRoot, scope),
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
                    scope,
                    sessions);
                var scheduler = new RuntimeSchedulerService(
                    store,
                    new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                    SystemSecurityClock.Instance,
                    supervisor,
                    authorization,
                    bindings: bindings,
                    sessions: sessions);
                var userLibraryRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LLMW.Writing",
                    "user-specialists");
                var wp13 = new Wp13RuntimeService(
                    store,
                    scheduler,
                    SystemSecurityClock.Instance,
                    new MemoryApplicationOversightDefaults(),
                    new FileUserSpecialistProfileStore(userLibraryRoot),
                    SyntheticBuiltInSpecialistCatalog.Instance,
                    UnavailableSemanticCompletionEvaluator.Instance);
                oversightBinder.Inner = wp13;
                scheduler.OversightActivationListener = wp13;

                bindings.Register(new TrustedIpcLaunchRecord(
                    AuthenticatedClientKind.AgentRuntime,
                    runtimeWorkerInstanceId,
                    runtimeChannelInstanceId,
                    scope));
                runtimeBindingInstalled = true;

                commands.Inner = new CompositeIpcCommandHandler(
                    this,
                    new RuntimeIpcCommandHandler(scheduler, workspaceInstanceId),
                    new Wp13IpcCommandHandler(wp13, workspaceInstanceId),
                    new Wp14IpcCommandHandler(
                        new ProviderInvocationStateHandler(store, scheduler, authorization),
                        workspaceInstanceId),
                    new Wp16IpcCommandHandler(editors, workspaceInstanceId));
                var pathResolver = new ProjectPathResolver(bind.CanonicalRoot);
                var blobStore = new ImmutableBlobStore(bind.CanonicalRoot);
                var draftStore = new DraftFileStore(pathResolver, new SelfWriteTracker());
                var localHistory = new LocalHistoryService(
                    blobStore,
                    new FileLocalHistoryMetadataStore(pathResolver));
                var editor = new EditorRuntime(
                    bind.ProjectId.ToString("D"),
                    draftStore,
                    blobStore,
                    EditorSaveFaultEnvironment.FromEnvironment(),
                    new OpenXmlDocxDocumentAdapter(),
                    localHistory);
                editors.PublishOnce(editor);
                publishedEditor = editor;
                wp13.RecoverBackgroundTasks();
                runSessions.PublishOnce(sessions);
                published = sessions;
                opened = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                opened = false;
                commands.Inner = previousInner;
                if (published is not null)
                {
                    runSessions.TryAbandon(published);
                }

                if (publishedEditor is not null)
                {
                    editors.TryAbandon(publishedEditor);
                }

                if (runtimeBindingInstalled)
                {
                    bindings.Unregister(AuthenticatedClientKind.AgentRuntime);
                }

                return Task.FromResult<IpcApplicationCommandResult?>(
                    Error(context, IpcErrorCodes.ProtocolViolation, "Project composition failed closed: " + exception.GetType().Name));
            }

            var response = IpcJson.Serialize(
                IpcEnvelopeFactory.Create(
                    IpcMessageType.Response,
                    IpcSemanticTypes.OpenProject,
                    workspaceInstanceId,
                    new OpenProjectResponse(bind.ProjectId.ToString("D")),
                    context.EnvelopeProjectId,
                    context.EnvelopeRunId,
                    context.CorrelationId,
                    context.RequestId),
                IpcJsonContext.Default.OpenProjectResponseEnvelope);
            return Task.FromResult<IpcApplicationCommandResult?>(new IpcApplicationCommandResult(response));
        }
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
