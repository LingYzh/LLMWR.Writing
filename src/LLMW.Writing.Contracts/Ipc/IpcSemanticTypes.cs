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
        ActivateExtension
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
        semanticType is Hello or Heartbeat or GetStateSnapshot or SubscribeEvents;

    public static bool IsCriticalControl(string semanticType) =>
        semanticType is Hello or HelloAck or Heartbeat or HeartbeatAck or Cancel or GetStateSnapshot
            or SubscribeEvents;
}
