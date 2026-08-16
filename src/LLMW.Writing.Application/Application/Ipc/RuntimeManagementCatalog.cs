using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public static class RuntimeManagementCatalog
{
    public static bool IsChannelScoped(string semanticType) => semanticType is
        IpcSemanticTypes.LoadSchedulerSnapshot or
        IpcSemanticTypes.CreateWorkflowRun or
        IpcSemanticTypes.CreateRun or
        IpcSemanticTypes.CreateTask or
        IpcSemanticTypes.DispatchReadyTask or
        IpcSemanticTypes.CancelRuntimeScope or
        IpcSemanticTypes.RetryTask or
        IpcSemanticTypes.PersistCheckpoint or
        IpcSemanticTypes.ClassifyResume or
        IpcSemanticTypes.LaunchRunWorker or
        IpcSemanticTypes.ReleaseRunWorker or
        IpcSemanticTypes.ReconcileRunWorkers or
        IpcSemanticTypes.CreateResultDependency or
        IpcSemanticTypes.UpdateResultDependency or
        IpcSemanticTypes.RefreshResultDependencyStatus or
        IpcSemanticTypes.GetTaskHandoff;
}
