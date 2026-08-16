using System.Text;
using System.Text.Json.Serialization.Metadata;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp13ContractTests
{
    public static void Run()
    {
        Wp13SemanticTypesAreKnownAndHaveNoDirectSpecialistMessage();
        GoldenJsonForWp13Dtos();
        Console.WriteLine("WP13 contract tests passed.");
    }

    private static void Wp13SemanticTypesAreKnownAndHaveNoDirectSpecialistMessage()
    {
        string[] types =
        [
            IpcSemanticTypes.RequestTaskCompletion,
            IpcSemanticTypes.SubmitResultArtifact,
            IpcSemanticTypes.GetResultArtifact,
            IpcSemanticTypes.GetTaskHandoff,
            IpcSemanticTypes.CreateResultDependency,
            IpcSemanticTypes.UpdateResultDependency,
            IpcSemanticTypes.ProposeResultDependencyChange,
            IpcSemanticTypes.RefreshResultDependencyStatus,
            IpcSemanticTypes.GetEffectiveOversight,
            IpcSemanticTypes.SetOversightOverride,
            IpcSemanticTypes.ListPendingApprovals,
            IpcSemanticTypes.ResolveRuntimeGrill,
            IpcSemanticTypes.ListSpecialists,
            IpcSemanticTypes.GetSpecialist,
            IpcSemanticTypes.CreateSpecialist,
            IpcSemanticTypes.UpdateSpecialist,
            IpcSemanticTypes.DuplicateSpecialist,
            IpcSemanticTypes.ValidateSpecialist,
            IpcSemanticTypes.CreateSpecialistTestRun,
            IpcSemanticTypes.ListBackgroundTasks,
            IpcSemanticTypes.GetBackgroundTask,
            IpcSemanticTypes.StopBackgroundTask
        ];
        foreach (var type in types)
        {
            Program.AssertTrue(IpcSemanticTypes.IsKnown(type), type + " must be known.");
            Program.AssertTrue(IpcSemanticTypes.IsWellFormed(type, IpcMessageType.Request), type + " must be a request.");
            Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(type), type + " must not auto-replay.");
        }

        Program.AssertTrue(!IpcSemanticTypes.IsKnown("sendMessageToSpecialist"), "Direct specialist message API is forbidden.");
        Program.AssertTrue(!IpcSemanticTypes.IsKnown("appendSpecialistInstruction"), "Direct specialist instruction API is forbidden.");
        Program.AssertTrue(!IpcSemanticTypes.IsKnown("forceSpecialistComplete"), "Force-complete API is forbidden.");
    }

    private static void GoldenJsonForWp13Dtos()
    {
        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.RequestTaskCompletion, new RequestTaskCompletionRequest("task-1", null)),
            IpcJsonContext.Default.RequestTaskCompletionRequestEnvelope,
            Payload("requestTaskCompletion", "request", "{\"taskId\":\"task-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.RequestTaskCompletion, new RequestTaskCompletionResponse("pass", "ra-1", [])),
            IpcJsonContext.Default.RequestTaskCompletionResponseEnvelope,
            Payload("requestTaskCompletion", "response", "{\"outcome\":\"pass\",\"resultArtifactId\":\"ra-1\",\"failures\":[]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.SubmitResultArtifact, new SubmitResultArtifactRequest("task-1", "complete", "{}", "{}", "{}", "{}", "{}", "{}", null)),
            IpcJsonContext.Default.SubmitResultArtifactRequestEnvelope,
            Payload("submitResultArtifact", "request", "{\"taskId\":\"task-1\",\"status\":\"complete\",\"conclusionJson\":\"{}\",\"findingsJson\":\"{}\",\"evidenceJson\":\"{}\",\"uncertaintyJson\":\"{}\",\"diagnosticsJson\":\"{}\",\"freshnessJson\":\"{}\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.SubmitResultArtifact, new SubmitResultArtifactResponse("ra-1", "complete")),
            IpcJsonContext.Default.SubmitResultArtifactResponseEnvelope,
            Payload("submitResultArtifact", "response", "{\"resultArtifactId\":\"ra-1\",\"status\":\"complete\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetResultArtifact, new GetResultArtifactRequest("task-1", null)),
            IpcJsonContext.Default.GetResultArtifactRequestEnvelope,
            Payload("getResultArtifact", "request", "{\"taskId\":\"task-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetResultArtifact, new GetResultArtifactResponse("ra-1", "task-1", "complete", "{}", "{}", "{}", "{}", "{}", "{}", 1)),
            IpcJsonContext.Default.GetResultArtifactResponseEnvelope,
            Payload("getResultArtifact", "response", "{\"resultArtifactId\":\"ra-1\",\"taskId\":\"task-1\",\"status\":\"complete\",\"conclusionJson\":\"{}\",\"findingsJson\":\"{}\",\"evidenceJson\":\"{}\",\"uncertaintyJson\":\"{}\",\"diagnosticsJson\":\"{}\",\"freshnessJson\":\"{}\",\"producedAtMs\":1}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetTaskHandoff, new GetTaskHandoffRequest("task-2", false)),
            IpcJsonContext.Default.GetTaskHandoffRequestEnvelope,
            Payload("getTaskHandoff", "request", "{\"consumerTaskId\":\"task-2\",\"includeEvidence\":false}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetTaskHandoff, new GetTaskHandoffResponse(
                "task-2",
                ["ra-1"],
                [],
                [new TaskHandoffEdgeDto("ra-1", "advisory", "warning", "stale", false, false, true)],
                ["advisory-stale"],
                false)),
            IpcJsonContext.Default.GetTaskHandoffResponseEnvelope,
            Payload("getTaskHandoff", "response", "{\"consumerTaskId\":\"task-2\",\"resultArtifactIds\":[\"ra-1\"],\"evidenceIds\":[],\"edges\":[{\"resultArtifactId\":\"ra-1\",\"dependencyKind\":\"advisory\",\"dependencyStatus\":\"warning\",\"freshnessState\":\"stale\",\"blocksDispatch\":false,\"blocksCompletion\":false,\"hasWarning\":true}],\"warnings\":[\"advisory-stale\"],\"includeTranscript\":false}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetTaskHandoff, new GetTaskHandoffResponse(
                "task-3",
                [],
                [],
                [new TaskHandoffEdgeDto(null, "required", "missing", "missing", true, true, false)],
                [],
                false)),
            IpcJsonContext.Default.GetTaskHandoffResponseEnvelope,
            Payload("getTaskHandoff", "response", "{\"consumerTaskId\":\"task-3\",\"resultArtifactIds\":[],\"evidenceIds\":[],\"edges\":[{\"dependencyKind\":\"required\",\"dependencyStatus\":\"missing\",\"freshnessState\":\"missing\",\"blocksDispatch\":true,\"blocksCompletion\":true,\"hasWarning\":false}],\"warnings\":[],\"includeTranscript\":false}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateResultDependency, new CreateResultDependencyRequest("c", "p", "required")),
            IpcJsonContext.Default.CreateResultDependencyRequestEnvelope,
            Payload("createResultDependency", "request", "{\"consumerTaskId\":\"c\",\"producerTaskId\":\"p\",\"dependencyKind\":\"required\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateResultDependency, new CreateResultDependencyResponse("dep-1", "missing")),
            IpcJsonContext.Default.CreateResultDependencyResponseEnvelope,
            Payload("createResultDependency", "response", "{\"dependencyId\":\"dep-1\",\"status\":\"missing\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.UpdateResultDependency, new UpdateResultDependencyRequest("dep-1", "required")),
            IpcJsonContext.Default.UpdateResultDependencyRequestEnvelope,
            Payload("updateResultDependency", "request", "{\"dependencyId\":\"dep-1\",\"dependencyKind\":\"required\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.UpdateResultDependency, new UpdateResultDependencyResponse("dep-1", "required", "current")),
            IpcJsonContext.Default.UpdateResultDependencyResponseEnvelope,
            Payload("updateResultDependency", "response", "{\"dependencyId\":\"dep-1\",\"dependencyKind\":\"required\",\"status\":\"current\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ProposeResultDependencyChange, new ProposeResultDependencyChangeRequest("dep-1", "advisory", "too strict", null)),
            IpcJsonContext.Default.ProposeResultDependencyChangeRequestEnvelope,
            Payload("proposeResultDependencyChange", "request", "{\"dependencyId\":\"dep-1\",\"proposedKind\":\"advisory\",\"reason\":\"too strict\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ProposeResultDependencyChange, new ProposeResultDependencyChangeResponse(true, "required")),
            IpcJsonContext.Default.ProposeResultDependencyChangeResponseEnvelope,
            Payload("proposeResultDependencyChange", "response", "{\"recorded\":true,\"effectiveKind\":\"required\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.RefreshResultDependencyStatus, new RefreshResultDependencyStatusRequest("p", null)),
            IpcJsonContext.Default.RefreshResultDependencyStatusRequestEnvelope,
            Payload("refreshResultDependencyStatus", "request", "{\"producerTaskId\":\"p\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.RefreshResultDependencyStatus, new RefreshResultDependencyStatusResponse(1)),
            IpcJsonContext.Default.RefreshResultDependencyStatusResponseEnvelope,
            Payload("refreshResultDependencyStatus", "response", "{\"updatedCount\":1}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetEffectiveOversight, new GetEffectiveOversightRequest(null, null, "task-1")),
            IpcJsonContext.Default.GetEffectiveOversightRequestEnvelope,
            Payload("getEffectiveOversight", "request", "{\"taskId\":\"task-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetEffectiveOversight, new GetEffectiveOversightResponse("author_confirmed_required", "ask", "application", null, true)),
            IpcJsonContext.Default.GetEffectiveOversightResponseEnvelope,
            Payload("getEffectiveOversight", "response", "{\"narrativeAuthority\":\"author_confirmed_required\",\"runtimePermissionMode\":\"ask\",\"winningScope\":\"application\",\"active\":true}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.SetOversightOverride, new SetOversightOverrideRequest("task", "task-1", "agent_delegated", "auto_approve_scoped", "cp-1")),
            IpcJsonContext.Default.SetOversightOverrideRequestEnvelope,
            Payload("setOversightOverride", "request", "{\"scopeKind\":\"task\",\"scopeId\":\"task-1\",\"narrativeAuthority\":\"agent_delegated\",\"runtimePermissionMode\":\"auto_approve_scoped\",\"effectiveAfterCheckpointId\":\"cp-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.SetOversightOverride, new SetOversightOverrideResponse("ov-1", false)),
            IpcJsonContext.Default.SetOversightOverrideResponseEnvelope,
            Payload("setOversightOverride", "response", "{\"overrideId\":\"ov-1\",\"active\":false}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ListPendingApprovals, new ListPendingApprovalsRequest("run-1")),
            IpcJsonContext.Default.ListPendingApprovalsRequestEnvelope,
            Payload("listPendingApprovals", "request", "{\"runId\":\"run-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ListPendingApprovals, new ListPendingApprovalsResponse([])),
            IpcJsonContext.Default.ListPendingApprovalsResponseEnvelope,
            Payload("listPendingApprovals", "response", "{\"items\":[]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ResolveRuntimeGrill, new ResolveRuntimeGrillRequest("appr-1", "CONTINUE", null, null)),
            IpcJsonContext.Default.ResolveRuntimeGrillRequestEnvelope,
            Payload("resolveRuntimeGrill", "request", "{\"approvalId\":\"appr-1\",\"resolution\":\"CONTINUE\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ResolveRuntimeGrill, new ResolveRuntimeGrillResponse("resolved", "CONTINUE", "CONTINUE")),
            IpcJsonContext.Default.ResolveRuntimeGrillResponseEnvelope,
            Payload("resolveRuntimeGrill", "response", "{\"status\":\"resolved\",\"resolution\":\"CONTINUE\",\"resumeDecision\":\"CONTINUE\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ListSpecialists, new ListSpecialistsRequest("project")),
            IpcJsonContext.Default.ListSpecialistsRequestEnvelope,
            Payload("listSpecialists", "request", "{\"scopeKind\":\"project\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ListSpecialists, new ListSpecialistsResponse([])),
            IpcJsonContext.Default.ListSpecialistsResponseEnvelope,
            Payload("listSpecialists", "response", "{\"items\":[]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetSpecialist, new GetSpecialistRequest("spec-1", "builtin")),
            IpcJsonContext.Default.GetSpecialistRequestEnvelope,
            Payload("getSpecialist", "request", "{\"profileId\":\"spec-1\",\"scopeKind\":\"builtin\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetSpecialist, new GetSpecialistResponse("spec-1", "builtin", "{}", true)),
            IpcJsonContext.Default.GetSpecialistResponseEnvelope,
            Payload("getSpecialist", "response", "{\"profileId\":\"spec-1\",\"scopeKind\":\"builtin\",\"definitionJson\":\"{}\",\"enabled\":true}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateSpecialist, new CreateSpecialistRequest("project", "{}")),
            IpcJsonContext.Default.CreateSpecialistRequestEnvelope,
            Payload("createSpecialist", "request", "{\"scopeKind\":\"project\",\"definitionJson\":\"{}\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateSpecialist, new CreateSpecialistResponse("spec-2", [])),
            IpcJsonContext.Default.CreateSpecialistResponseEnvelope,
            Payload("createSpecialist", "response", "{\"profileId\":\"spec-2\",\"validationErrors\":[]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.UpdateSpecialist, new UpdateSpecialistRequest("spec-2", "project", "{}")),
            IpcJsonContext.Default.UpdateSpecialistRequestEnvelope,
            Payload("updateSpecialist", "request", "{\"profileId\":\"spec-2\",\"scopeKind\":\"project\",\"definitionJson\":\"{}\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.UpdateSpecialist, new UpdateSpecialistResponse("spec-2", [])),
            IpcJsonContext.Default.UpdateSpecialistResponseEnvelope,
            Payload("updateSpecialist", "response", "{\"profileId\":\"spec-2\",\"validationErrors\":[]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.DuplicateSpecialist, new DuplicateSpecialistRequest("spec-1", "builtin", "user")),
            IpcJsonContext.Default.DuplicateSpecialistRequestEnvelope,
            Payload("duplicateSpecialist", "request", "{\"profileId\":\"spec-1\",\"sourceScopeKind\":\"builtin\",\"targetScopeKind\":\"user\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.DuplicateSpecialist, new DuplicateSpecialistResponse("spec-3", "digest")),
            IpcJsonContext.Default.DuplicateSpecialistResponseEnvelope,
            Payload("duplicateSpecialist", "response", "{\"profileId\":\"spec-3\",\"baseDefinitionDigest\":\"digest\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ValidateSpecialist, new ValidateSpecialistRequest("{}")),
            IpcJsonContext.Default.ValidateSpecialistRequestEnvelope,
            Payload("validateSpecialist", "request", "{\"definitionJson\":\"{}\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ValidateSpecialist, new ValidateSpecialistResponse(false, ["identity-required"])),
            IpcJsonContext.Default.ValidateSpecialistResponseEnvelope,
            Payload("validateSpecialist", "response", "{\"valid\":false,\"errors\":[\"identity-required\"]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateSpecialistTestRun, new CreateSpecialistTestRunRequest("spec-1", "project")),
            IpcJsonContext.Default.CreateSpecialistTestRunRequestEnvelope,
            Payload("createSpecialistTestRun", "request", "{\"profileId\":\"spec-1\",\"scopeKind\":\"project\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateSpecialistTestRun, new CreateSpecialistTestRunResponse("provider_unavailable", null)),
            IpcJsonContext.Default.CreateSpecialistTestRunResponseEnvelope,
            Payload("createSpecialistTestRun", "response", "{\"outcome\":\"provider_unavailable\"}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ListBackgroundTasks, new ListBackgroundTasksRequest("run-1")),
            IpcJsonContext.Default.ListBackgroundTasksRequestEnvelope,
            Payload("listBackgroundTasks", "request", "{\"ownerRunId\":\"run-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ListBackgroundTasks, new ListBackgroundTasksResponse([])),
            IpcJsonContext.Default.ListBackgroundTasksResponseEnvelope,
            Payload("listBackgroundTasks", "response", "{\"items\":[]}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetBackgroundTask, new GetBackgroundTaskRequest("bg-1")),
            IpcJsonContext.Default.GetBackgroundTaskRequestEnvelope,
            Payload("getBackgroundTask", "request", "{\"backgroundTaskId\":\"bg-1\"}"));
        var dto = new BackgroundTaskDto("bg-1", "run-1", "task-1", "sub_agent_run", "running", "{}", "cp-1", 1, null, null);
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetBackgroundTask, new GetBackgroundTaskResponse(dto)),
            IpcJsonContext.Default.GetBackgroundTaskResponseEnvelope,
            Payload("getBackgroundTask", "response", "{\"task\":{\"backgroundTaskId\":\"bg-1\",\"ownerRunId\":\"run-1\",\"ownerTaskId\":\"task-1\",\"kind\":\"sub_agent_run\",\"status\":\"running\",\"executionJson\":\"{}\",\"checkpointId\":\"cp-1\",\"startedAtMs\":1}}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.StopBackgroundTask, new StopBackgroundTaskRequest("bg-1")),
            IpcJsonContext.Default.StopBackgroundTaskRequestEnvelope,
            Payload("stopBackgroundTask", "request", "{\"backgroundTaskId\":\"bg-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.StopBackgroundTask, new StopBackgroundTaskResponse(true, "cancelled")),
            IpcJsonContext.Default.StopBackgroundTaskResponseEnvelope,
            Payload("stopBackgroundTask", "response", "{\"stopped\":true,\"status\":\"cancelled\"}"));
    }

    private static string Payload(string semanticType, string messageType, string payload) =>
        "{\"protocolVersion\":1,\"messageType\":\"" + messageType + "\",\"semanticType\":\"" + semanticType +
        "\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":" +
        payload + "}";

    private static void AssertGolden<T>(IpcEnvelope<T> envelope, JsonTypeInfo<IpcEnvelope<T>> typeInfo, string expected)
    {
        var actual = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, typeInfo));
        Program.AssertEqual(expected, actual, envelope.SemanticType + " golden JSON changed. Actual=" + actual);
        var roundTrip = IpcJson.Deserialize(IpcJson.GetBytes(actual), typeInfo);
        Program.AssertEqual(envelope.SemanticType, roundTrip.SemanticType, envelope.SemanticType + " lost discriminator.");
    }
}
