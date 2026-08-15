using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp11ContractTests
{
    public static void Run()
    {
        SemanticDiscriminatorIsRequiredAndUnknownFailsClosed();
        MessageTypeIsNotAnOperationName();
        GoldenJsonForEveryV1Dto();
        OptionalExpiresAtMsIsOmittedAndUnknownFieldsAreIgnored();
        FramingRejectsZeroOversizePartialAndMalformedUtf8();
        GapSnapshotAndCancelGoldens();
        RunSessionProofWireShapeDoesNotCarryRoleOrCapability();
        SafeReplayCatalogExcludesMutations();
        LengthMismatchedBootstrapCompareFailsClosed();
        Console.WriteLine("WP11 contract tests passed.");
    }

    private static void SemanticDiscriminatorIsRequiredAndUnknownFailsClosed()
    {
        Program.AssertTrue(IpcSemanticTypes.IsKnown(IpcSemanticTypes.Hello), "hello must be a known semantic type.");
        Program.AssertTrue(!IpcSemanticTypes.IsKnown("System.IO.File"), "CLR type names must not be semantic types.");
        Program.AssertTrue(!IpcSemanticTypes.IsKnown("createRunSessionRequest"), "DTO class names must not be semantic types.");
        Program.AssertTrue(!IpcSemanticTypes.IsWellFormed(IpcSemanticTypes.Hello, IpcMessageType.Request),
            "hello must not be smuggled as a request.");
        Program.AssertTrue(IpcSemanticTypes.IsWellFormed(IpcSemanticTypes.CreateRunSession, IpcMessageType.Request),
            "createRunSession request pair must be well-formed.");
        Program.AssertTrue(IpcSemanticTypes.IsWellFormed(IpcSemanticTypes.CreateRunSession, IpcMessageType.Response),
            "createRunSession response pair must be well-formed.");
        Program.AssertTrue(!IpcSemanticTypes.IsKnown("notARealOperation"), "unknown semantic types must fail closed.");

        var json = "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"notARealOperation\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1,\"payload\":{}}";
        var wire = IpcJson.DeserializeWire(IpcJson.GetBytes(json));
        Program.AssertTrue(!IpcSemanticTypes.IsKnown(wire.SemanticType), "Wire parser must not treat unknown semanticType as known.");
        Program.AssertThrows<JsonException>(
            () => IpcJson.DeserializeWire(IpcJson.GetBytes(json.Replace("\"semanticType\":\"notARealOperation\",", ""))),
            "Missing semanticType must fail closed.");
    }

    private static void MessageTypeIsNotAnOperationName()
    {
        var messageTypeJson = Encoding.UTF8.GetString(IpcJson.Serialize(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.OpenProject, new OpenProjectRequest("p")),
            IpcJsonContext.Default.OpenProjectRequestEnvelope));
        Program.AssertTrue(messageTypeJson.Contains("\"messageType\":\"request\"", StringComparison.Ordinal),
            "messageType enum encoding changed.");
        foreach (var name in new[] { "createRunSession", "hello", "gap", "cancel" })
        {
            Program.AssertTrue(!Enum.TryParse<IpcMessageType>(name, ignoreCase: true, out _),
                $"messageType must not include operation {name}.");
        }
    }

    private static void GoldenJsonForEveryV1Dto()
    {
        AssertGolden(
            Program.Envelope(IpcMessageType.Control, IpcSemanticTypes.HelloAck, new HelloAck(
                1,
                IpcServerCapabilities.V1,
                "stream-1",
                "conn-1",
                "rotated-token")),
            IpcJsonContext.Default.HelloAckEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"control\",\"semanticType\":\"helloAck\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"negotiatedProtocol\":1,\"serverCapabilities\":[\"heartbeat\",\"multiplex\",\"snapshot\",\"cancel\",\"events\"],\"eventStreamId\":\"stream-1\",\"connectionId\":\"conn-1\",\"rotatedBootstrapToken\":\"rotated-token\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Control, IpcSemanticTypes.Heartbeat, new Heartbeat(7)),
            IpcJsonContext.Default.HeartbeatEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"control\",\"semanticType\":\"heartbeat\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"sequence\":7}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Control, IpcSemanticTypes.HeartbeatAck, new HeartbeatAck(7)),
            IpcJsonContext.Default.HeartbeatAckEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"control\",\"semanticType\":\"heartbeatAck\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"sequence\":7}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.Hello, new IpcError(IpcErrorCodes.UnsupportedSemanticType, "unknown", null, false)),
            IpcJsonContext.Default.ErrorEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"hello\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"code\":\"IPC_UNSUPPORTED_SEMANTIC_TYPE\",\"message\":\"unknown\",\"retryable\":false}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateRunSession, new CreateRunSessionRequest("run-1", 1735693200000), runId: Guid.Parse("018f3e78-1234-7abc-8def-0123456789ae")),
            IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"createRunSession\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"runId\":\"018f3e78-1234-7abc-8def-0123456789ae\",\"timestampMs\":1735689600000,\"payload\":{\"runId\":\"run-1\",\"expiresAtMs\":1735693200000}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateRunSession, new CreateRunSessionResponse("handle-1", "run-1", "opaque", 1735693200000)),
            IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"createRunSession\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"handleId\":\"handle-1\",\"runId\":\"run-1\",\"opaqueToken\":\"opaque\",\"expiresAtMs\":1735693200000}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.RevokeRunSession, new RevokeRunSessionRequest("handle-1", null)),
            IpcJsonContext.Default.RevokeRunSessionRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"revokeRunSession\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"handleId\":\"handle-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.RevokeRunSession, new RevokeRunSessionResponse(true)),
            IpcJsonContext.Default.RevokeRunSessionResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"revokeRunSession\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"revoked\":true}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetStateSnapshot, new GetStateSnapshotRequest(12, "stream-1")),
            IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"getStateSnapshot\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"lastKnownSeq\":12,\"lastEventStreamId\":\"stream-1\"}}");

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotResponse("stream-1", 12, false, new IpcTransportSnapshot(1, IpcServerCapabilities.V1))),
            IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"getStateSnapshot\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"eventStreamId\":\"stream-1\",\"snapshotSeq\":12,\"resyncRequired\":false,\"snapshot\":{\"protocolVersion\":1,\"capabilities\":[\"heartbeat\",\"multiplex\",\"snapshot\",\"cancel\",\"events\"]}}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.SubscribeEvents, new SubscribeEventsRequest("stream-1", 12)),
            IpcJsonContext.Default.SubscribeEventsRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"subscribeEvents\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"eventStreamId\":\"stream-1\",\"afterSeq\":12}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.SubscribeEvents, new SubscribeEventsResponse("stream-1", 12, 12)),
            IpcJsonContext.Default.SubscribeEventsResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"subscribeEvents\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"eventStreamId\":\"stream-1\",\"afterSeq\":12,\"headSeq\":12}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Control, IpcSemanticTypes.Cancel, new CancelRequest(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"))),
            IpcJsonContext.Default.CancelRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"control\",\"semanticType\":\"cancel\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ab\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Control, IpcSemanticTypes.Cancel, new CancelResponse(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), true, CancelResponse.StateUnknown)),
            IpcJsonContext.Default.CancelResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"control\",\"semanticType\":\"cancel\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"accepted\":true,\"state\":\"unknown\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Event, IpcSemanticTypes.Gap, new GapEvent("stream-1", 1, 4)),
            IpcJsonContext.Default.GapEventEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"event\",\"semanticType\":\"gap\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"eventStreamId\":\"stream-1\",\"fromSeq\":1,\"toSeq\":4}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Event, IpcSemanticTypes.CoreNotice, new CoreNoticeEvent("stream-1", 1, "notice", "detail")),
            IpcJsonContext.Default.CoreNoticeEventEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"event\",\"semanticType\":\"coreNotice\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"eventStreamId\":\"stream-1\",\"seq\":1,\"name\":\"notice\",\"detail\":\"detail\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.OpenProject, new OpenProjectRequest("C:/proj")),
            IpcJsonContext.Default.OpenProjectRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"openProject\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"requestedPath\":\"C:/proj\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.OpenProject, new OpenProjectResponse("project-1")),
            IpcJsonContext.Default.OpenProjectResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"openProject\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"projectId\":\"project-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetProjectState, new GetProjectStateRequest("project-1")),
            IpcJsonContext.Default.GetProjectStateRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"getProjectState\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"projectId\":\"project-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.GetProjectState, new GetProjectStateResponse("project-1", "open")),
            IpcJsonContext.Default.GetProjectStateResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"getProjectState\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"projectId\":\"project-1\",\"status\":\"open\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.SubmitCandidate, new SubmitCandidateRequest("ch-1", "draft.md", "key-1", null)),
            IpcJsonContext.Default.SubmitCandidateRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"submitCandidate\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"chapterId\":\"ch-1\",\"draftPath\":\"draft.md\",\"idempotencyKey\":\"key-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.SubmitCandidate, new SubmitCandidateResponse("cand-1")),
            IpcJsonContext.Default.SubmitCandidateResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"submitCandidate\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"candidateId\":\"cand-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CancelSubmission, new CancelSubmissionRequest("sub-1", null)),
            IpcJsonContext.Default.CancelSubmissionRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"cancelSubmission\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"submissionId\":\"sub-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CancelSubmission, new CancelSubmissionResponse(true)),
            IpcJsonContext.Default.CancelSubmissionResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"cancelSubmission\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"cancelled\":true}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.AcceptAuthority, new AcceptAuthorityRequest("cand-1", "key-1", "author")),
            IpcJsonContext.Default.AcceptAuthorityRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"acceptAuthority\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"candidateId\":\"cand-1\",\"idempotencyKey\":\"key-1\",\"acceptedBy\":\"author\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.AcceptAuthority, new AcceptAuthorityResponse("cand-1", "complete")),
            IpcJsonContext.Default.AcceptAuthorityResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"acceptAuthority\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"candidateId\":\"cand-1\",\"transactionState\":\"complete\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ApplyNarrativeChangeSet, new ApplyNarrativeChangeSetRequest("cs-1", "authorConfirmed", "author-1")),
            IpcJsonContext.Default.ApplyNarrativeChangeSetRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"applyNarrativeChangeSet\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"changeSetId\":\"cs-1\",\"decisionKind\":\"authorConfirmed\",\"actorId\":\"author-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ApplyNarrativeChangeSet, new ApplyNarrativeChangeSetResponse("cs-1")),
            IpcJsonContext.Default.ApplyNarrativeChangeSetResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"applyNarrativeChangeSet\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"changeSetId\":\"cs-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.RegisterProjectFile, new RegisterProjectFileRequest("Narrative/a.md")),
            IpcJsonContext.Default.RegisterProjectFileRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"registerProjectFile\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"relativePath\":\"Narrative/a.md\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.RegisterProjectFile, new RegisterProjectFileResponse("path-1")),
            IpcJsonContext.Default.RegisterProjectFileResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"registerProjectFile\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"pathId\":\"path-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ReconcileRegistryEntry, new ReconcileRegistryEntryRequest("path-1")),
            IpcJsonContext.Default.ReconcileRegistryEntryRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"reconcileRegistryEntry\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"pathId\":\"path-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ReconcileRegistryEntry, new ReconcileRegistryEntryResponse("path-1", "confirmed")),
            IpcJsonContext.Default.ReconcileRegistryEntryResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"reconcileRegistryEntry\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"pathId\":\"path-1\",\"status\":\"confirmed\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.SearchNarrative, new SearchNarrativeRequest("query", 20, null)),
            IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"searchNarrative\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"text\":\"query\",\"limit\":20}}");

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.SearchNarrative,
                new SearchNarrativeResponse([new SearchNarrativeHit("obj-1", "digest", "sec", "Title", "current", 1.5)])),
            IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"searchNarrative\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"hits\":[{\"objectId\":\"obj-1\",\"artifactDigest\":\"digest\",\"sectionKey\":\"sec\",\"title\":\"Title\",\"currentStatus\":\"current\",\"rank\":1.5}]}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.RestoreHistoryEntry, new RestoreHistoryEntryRequest("hist-1")),
            IpcJsonContext.Default.RestoreHistoryEntryRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"restoreHistoryEntry\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"historyId\":\"hist-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.RestoreHistoryEntry, new RestoreHistoryEntryResponse("hist-1", true)),
            IpcJsonContext.Default.RestoreHistoryEntryResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"restoreHistoryEntry\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"historyId\":\"hist-1\",\"restored\":true}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ActivateExtension, new ActivateExtensionRequest("ext-1")),
            IpcJsonContext.Default.ActivateExtensionRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"activateExtension\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"extensionId\":\"ext-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ActivateExtension, new ActivateExtensionResponse("ext-1", false)),
            IpcJsonContext.Default.ActivateExtensionResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"activateExtension\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"extensionId\":\"ext-1\",\"activated\":false}}");
    }

    private static void OptionalExpiresAtMsIsOmittedAndUnknownFieldsAreIgnored()
    {
        var omitted = Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateRunSession, new CreateRunSessionRequest("run-1", null));
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(omitted, IpcJsonContext.Default.CreateRunSessionRequestEnvelope));
        Program.AssertTrue(!json.Contains("expiresAtMs", StringComparison.Ordinal), "Null expiresAtMs must be omitted.");
        var extra = json[..^1] + ",\"unexpectedOptional\":true}";
        var wire = IpcJson.DeserializeWire(IpcJson.GetBytes(extra));
        Program.AssertEqual(IpcSemanticTypes.CreateRunSession, wire.SemanticType, "Unknown optional fields must be ignored.");
    }

    private static void FramingRejectsZeroOversizePartialAndMalformedUtf8()
    {
        Program.AssertEqual(IpcProtocol.MaximumFrameBytes, IpcFrameHeader.Parse(IpcFrameHeader.Create(IpcProtocol.MaximumFrameBytes)),
            "1 MiB frames must be accepted.");

        Span<byte> oversize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(oversize, (uint)IpcProtocol.MaximumFrameBytes + 1);
        Program.AssertTrue(!IpcFrameHeader.TryParse(oversize, out _, out var oversizeCode), "Oversize uint32 length must be rejected before body allocation.");
        Program.AssertEqual(IpcErrorCodes.InvalidFrame, oversizeCode!, "Oversize frames must use IPC_INVALID_FRAME.");

        Span<byte> zero = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(zero, 0);
        Program.AssertTrue(!IpcFrameHeader.TryParse(zero, out _, out var zeroCode), "Zero-length frames must be rejected.");
        Program.AssertEqual(IpcErrorCodes.InvalidFrame, zeroCode!, "Zero-length frames must use IPC_INVALID_FRAME.");

        Program.AssertTrue(!IpcFrameHeader.TryParse([0x01, 0x00], out _, out _), "Truncated headers must be rejected.");

        using var partial = new MemoryStream([0x05, 0x00]);
        var thrown = false;
        try
        {
            IpcFrameIO.ReadAsync(partial, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (EndOfStreamException)
        {
            thrown = true;
        }

        Program.AssertTrue(thrown, "Partial headers must not be parsed as a synchronized frame.");

        using var partialBody = new MemoryStream();
        partialBody.Write(IpcFrameHeader.Create(4));
        partialBody.Write([0x7b, 0x7d]);
        partialBody.Position = 0;
        thrown = false;
        try
        {
            IpcFrameIO.ReadAsync(partialBody, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (EndOfStreamException)
        {
            thrown = true;
        }

        Program.AssertTrue(thrown, "Truncated bodies must be rejected.");

        Program.AssertThrows<DecoderFallbackException>(
            () => IpcJson.DeserializeWire([0x7b, 0xff, 0x7d] ),
            "Malformed UTF-8 must be rejected.");

        var malformedJson = IpcJson.GetBytes("{\"protocolVersion\":1,");
        IpcFrameHeader.ValidateLength(malformedJson.Length);
        Program.AssertThrows<JsonException>(
            () => IpcJson.DeserializeWire(malformedJson),
            "Malformed JSON must be rejected.");
    }

    private static void GapSnapshotAndCancelGoldens()
    {
        Program.AssertEqual(256, IpcProtocol.SubscriberRingCapacity, "Subscriber ring capacity must remain 256.");
        Program.AssertEqual(1, IpcProtocol.FirstEventSequence, "First ordinary event seq must be 1.");
        Program.AssertEqual(0L, IpcProtocol.EmptySnapshotSequence, "Empty snapshot watermark must be 0.");
        Program.AssertTrue(IpcSemanticTypes.IsWellFormed(IpcSemanticTypes.Gap, IpcMessageType.Event), "GapEvent must be an event.");
        Program.AssertTrue(IpcSemanticTypes.IsWellFormed(IpcSemanticTypes.Cancel, IpcMessageType.Control), "Cancel must be control.");
    }

    private static void RunSessionProofWireShapeDoesNotCarryRoleOrCapability()
    {
        var proof = new RunSessionProof("run-1", "secret");
        var request = Program.Envelope(
            IpcMessageType.Request,
            IpcSemanticTypes.SearchNarrative,
            new SearchNarrativeRequest("q", 1, proof));
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(request, IpcJsonContext.Default.SearchNarrativeRequestEnvelope));
        Program.AssertTrue(json.Contains("\"session\":{\"runId\":\"run-1\",\"opaqueToken\":\"secret\"}", StringComparison.Ordinal),
            "RunSession proof wire shape changed.");
        Program.AssertTrue(!json.Contains("role", StringComparison.OrdinalIgnoreCase), "Proof must not serialize role.");
        Program.AssertTrue(!json.Contains("capability", StringComparison.OrdinalIgnoreCase), "Proof must not serialize capability.");
        Program.AssertTrue(!json.Contains("principalKind", StringComparison.OrdinalIgnoreCase), "Proof must not serialize principalKind.");
    }

    private static void SafeReplayCatalogExcludesMutations()
    {
        Program.AssertTrue(IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.Hello), "Hello must be replay-safe.");
        Program.AssertTrue(IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.Heartbeat), "Heartbeat must be replay-safe.");
        Program.AssertTrue(IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.GetStateSnapshot), "Snapshot must be replay-safe.");
        Program.AssertTrue(IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.SubscribeEvents), "Subscribe must be replay-safe.");
        Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.CreateRunSession), "CreateRunSession must not auto-replay.");
        Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.SubmitCandidate), "SubmitCandidate must not auto-replay.");
        Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.AcceptAuthority), "AcceptAuthority must not auto-replay.");
    }

    private static void LengthMismatchedBootstrapCompareFailsClosed()
    {
        var token = IpcBootstrapToken.Create();
        Program.AssertTrue(IpcBootstrapToken.FixedTimeEquals(token, token), "Equal bootstrap tokens must compare equal.");
        Program.AssertTrue(
            !IpcBootstrapToken.FixedTimeEquals(token, token[..^1]),
            "A truncated bootstrap token must be rejected without throwing.");
        Program.AssertTrue(
            !IpcBootstrapToken.FixedTimeEquals(token, token + "x"),
            "A lengthened bootstrap token must be rejected without throwing.");
    }

    private static void AssertGolden<T>(
        IpcEnvelope<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<T>> typeInfo,
        string expected)
    {
        var actual = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, typeInfo));
        Program.AssertEqual(expected, actual, $"{envelope.SemanticType} golden JSON changed.");
        var roundTrip = IpcJson.Deserialize(IpcJson.GetBytes(actual), typeInfo);
        Program.AssertEqual(envelope.SemanticType, roundTrip.SemanticType, $"{envelope.SemanticType} lost discriminator on round-trip.");
    }
}
