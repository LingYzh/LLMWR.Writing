using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Runtime;

public static class AgentTaskOwnership
{
    public static RuntimeResult<DurableTaskRecord> RequireOwnedAgentTask(
        IRuntimePersistence store,
        CallerPrincipal? principal,
        string taskId)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (principal is null || principal.Kind != PrincipalKind.AgentRun)
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.TaskOwnershipDenied, "agent-session-required");
        }

        if (string.IsNullOrWhiteSpace(principal.RunId))
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.TaskOwnershipDenied, "missing-run-id");
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.NotFound, "task");
        }

        var task = store.GetTask(taskId);
        if (task is null)
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.NotFound, "task");
        }

        if (!StringComparer.Ordinal.Equals(task.RunId, principal.RunId))
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.TaskOwnershipDenied, "run-task-mismatch");
        }

        if (TaskStatusCodec.TryParse(task.Status, out var status) &&
            status is RuntimeTaskStatus.Cancelled or RuntimeTaskStatus.Failed)
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.IllegalCompletionLifecycle, "terminal-task");
        }

        return RuntimeResults.Success(task);
    }
}
