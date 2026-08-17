namespace LLMW.Writing.Contracts.Ipc;

/// <summary>
/// Stable v1 semantic discriminators. These are not CLR type names and are not inferred from payload shape.
/// </summary>
public static class IpcSemanticTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "helloAck";
    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeatAck";
    public const string Cancel = "cancel";
    public const string Gap = "gap";
    public const string CoreNotice = "coreNotice";
    public const string GetStateSnapshot = "getStateSnapshot";
    public const string SubscribeEvents = "subscribeEvents";
    public const string CreateRunSession = "createRunSession";
    public const string RevokeRunSession = "revokeRunSession";
    public const string OpenProject = "openProject";
    public const string GetProjectState = "getProjectState";
    public const string SubmitCandidate = "submitCandidate";
    public const string CancelSubmission = "cancelSubmission";
    public const string AcceptAuthority = "acceptAuthority";
    public const string ApplyNarrativeChangeSet = "applyNarrativeChangeSet";
    public const string RegisterProjectFile = "registerProjectFile";
    public const string ReconcileRegistryEntry = "reconcileRegistryEntry";
    public const string SearchNarrative = "searchNarrative";
    public const string RestoreHistoryEntry = "restoreHistoryEntry";
    public const string ActivateExtension = "activateExtension";
    public const string LoadSchedulerSnapshot = "loadSchedulerSnapshot";
    public const string CreateWorkflowRun = "createWorkflowRun";
    public const string CreateRun = "createRun";
    public const string CreateTask = "createTask";
    public const string DispatchReadyTask = "dispatchReadyTask";
    public const string CancelRuntimeScope = "cancelRuntimeScope";
    public const string RetryTask = "retryTask";
    public const string PersistCheckpoint = "persistCheckpoint";
    public const string ClassifyResume = "classifyResume";
    public const string LaunchRunWorker = "launchRunWorker";
    public const string ReleaseRunWorker = "releaseRunWorker";
    public const string ReconcileRunWorkers = "reconcileRunWorkers";
    public const string SpawnChildRun = "spawnChildRun";
    public const string RequestTaskCompletion = "requestTaskCompletion";
    public const string SubmitResultArtifact = "submitResultArtifact";
    public const string GetResultArtifact = "getResultArtifact";
    public const string GetTaskHandoff = "getTaskHandoff";
    public const string CreateResultDependency = "createResultDependency";
    public const string UpdateResultDependency = "updateResultDependency";
    public const string ProposeResultDependencyChange = "proposeResultDependencyChange";
    public const string RefreshResultDependencyStatus = "refreshResultDependencyStatus";
    public const string GetEffectiveOversight = "getEffectiveOversight";
    public const string SetOversightOverride = "setOversightOverride";
    public const string ListPendingApprovals = "listPendingApprovals";
    public const string ResolveRuntimeGrill = "resolveRuntimeGrill";
    public const string ListSpecialists = "listSpecialists";
    public const string GetSpecialist = "getSpecialist";
    public const string CreateSpecialist = "createSpecialist";
    public const string UpdateSpecialist = "updateSpecialist";
    public const string DuplicateSpecialist = "duplicateSpecialist";
    public const string ValidateSpecialist = "validateSpecialist";
    public const string CreateSpecialistTestRun = "createSpecialistTestRun";
    public const string ListBackgroundTasks = "listBackgroundTasks";
    public const string GetBackgroundTask = "getBackgroundTask";
    public const string StopBackgroundTask = "stopBackgroundTask";
    public const string GetTaskExecutionSnapshot = "getTaskExecutionSnapshot";
    public const string PersistProviderInvocation = "persistProviderInvocation";
    public const string AuthorizeToolProposal = "authorizeToolProposal";
    public const string OpenDraftEditorSession = "openDraftEditorSession";
    public const string GetDraftEditorSessionState = "getDraftEditorSessionState";
    public const string ReleaseDraftEditorSession = "releaseDraftEditorSession";
    public const string BeginEditorContentUpload = "beginEditorContentUpload";
    public const string EditorContentUploadChunk = "editorContentUploadChunk";
    public const string CommitEditorContentUpload = "commitEditorContentUpload";
    public const string SaveDraftEditorSession = "saveDraftEditorSession";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Hello,
        HelloAck,
        Heartbeat,
        HeartbeatAck,
        Cancel,
        Gap,
        CoreNotice,
        GetStateSnapshot,
        SubscribeEvents,
        CreateRunSession,
        RevokeRunSession,
        OpenProject,
        GetProjectState,
        SubmitCandidate,
        CancelSubmission,
        AcceptAuthority,
        ApplyNarrativeChangeSet,
        RegisterProjectFile,
        ReconcileRegistryEntry,
        SearchNarrative,
        RestoreHistoryEntry,
        ActivateExtension,
        LoadSchedulerSnapshot,
        CreateWorkflowRun,
        CreateRun,
        CreateTask,
        DispatchReadyTask,
        CancelRuntimeScope,
        RetryTask,
        PersistCheckpoint,
        ClassifyResume,
        LaunchRunWorker,
        ReleaseRunWorker,
        ReconcileRunWorkers,
        SpawnChildRun,
        RequestTaskCompletion,
        SubmitResultArtifact,
        GetResultArtifact,
        GetTaskHandoff,
        CreateResultDependency,
        UpdateResultDependency,
        ProposeResultDependencyChange,
        RefreshResultDependencyStatus,
        GetEffectiveOversight,
        SetOversightOverride,
        ListPendingApprovals,
        ResolveRuntimeGrill,
        ListSpecialists,
        GetSpecialist,
        CreateSpecialist,
        UpdateSpecialist,
        DuplicateSpecialist,
        ValidateSpecialist,
        CreateSpecialistTestRun,
        ListBackgroundTasks,
        GetBackgroundTask,
        StopBackgroundTask,
        GetTaskExecutionSnapshot,
        PersistProviderInvocation,
        AuthorizeToolProposal,
        OpenDraftEditorSession,
        GetDraftEditorSessionState,
        ReleaseDraftEditorSession,
        BeginEditorContentUpload,
        EditorContentUploadChunk,
        CommitEditorContentUpload,
        SaveDraftEditorSession
    };

    public static IReadOnlyCollection<string> All { get; } = Known.ToArray();

    public static bool IsKnown(string? semanticType) =>
        !string.IsNullOrWhiteSpace(semanticType) && Known.Contains(semanticType);

    public static bool IsWellFormed(string semanticType, IpcMessageType messageType)
    {
        if (!IsKnown(semanticType))
        {
            return false;
        }

        return semanticType switch
        {
            Hello or HelloAck or Heartbeat or HeartbeatAck or Cancel => messageType == IpcMessageType.Control,
            Gap or CoreNotice => messageType == IpcMessageType.Event,
            _ => messageType is IpcMessageType.Request or IpcMessageType.Response
        };
    }

    public static bool IsHandshakeOnly(string semanticType) =>
        semanticType is Hello or HelloAck;

    public static bool IsSafeToReplayAfterReconnect(string semanticType) =>
        semanticType is Hello or Heartbeat or GetStateSnapshot or SubscribeEvents or GetTaskExecutionSnapshot;

    public static bool IsCriticalControl(string semanticType) =>
        semanticType is Hello or HelloAck or Heartbeat or HeartbeatAck or Cancel or GetStateSnapshot
            or SubscribeEvents;
}
