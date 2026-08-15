using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public static class Wp13CommandCatalog
{
    public static bool IsRuntimeManagement(string semanticType) => semanticType is
        IpcSemanticTypes.CreateResultDependency or
        IpcSemanticTypes.UpdateResultDependency or
        IpcSemanticTypes.RefreshResultDependencyStatus or
        IpcSemanticTypes.GetTaskHandoff;

    public static bool IsUiOwned(string semanticType) => semanticType is
        IpcSemanticTypes.SetOversightOverride or
        IpcSemanticTypes.CreateSpecialist or
        IpcSemanticTypes.UpdateSpecialist or
        IpcSemanticTypes.DuplicateSpecialist or
        IpcSemanticTypes.ValidateSpecialist or
        IpcSemanticTypes.CreateSpecialistTestRun;

    public static bool IsAgentSession(string semanticType) => semanticType is
        IpcSemanticTypes.SubmitResultArtifact or
        IpcSemanticTypes.RequestTaskCompletion or
        IpcSemanticTypes.ProposeResultDependencyChange;

    public static bool IsDualQuery(string semanticType) => semanticType is
        IpcSemanticTypes.GetEffectiveOversight or
        IpcSemanticTypes.GetResultArtifact or
        IpcSemanticTypes.ListPendingApprovals or
        IpcSemanticTypes.ListSpecialists or
        IpcSemanticTypes.GetSpecialist or
        IpcSemanticTypes.ListBackgroundTasks or
        IpcSemanticTypes.GetBackgroundTask or
        IpcSemanticTypes.StopBackgroundTask;

    public static bool IsDualUiOrAgent(string semanticType) =>
        semanticType is IpcSemanticTypes.ResolveRuntimeGrill;
}
