using System.Text;
using System.Text.Json.Serialization.Metadata;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp14ContractTests
{
    public static void Run()
    {
        Wp14SemanticTypesAreKnownAndDoNotReplayProviderHttp();
        GoldenJsonForWp14Dtos();
        Console.WriteLine("WP14 contract tests passed.");
    }

    private static void Wp14SemanticTypesAreKnownAndDoNotReplayProviderHttp()
    {
        Program.AssertTrue(IpcSemanticTypes.IsKnown(IpcSemanticTypes.GetTaskExecutionSnapshot), "getTaskExecutionSnapshot must be known.");
        Program.AssertTrue(IpcSemanticTypes.IsKnown(IpcSemanticTypes.PersistProviderInvocation), "persistProviderInvocation must be known.");
        Program.AssertTrue(IpcSemanticTypes.IsKnown(IpcSemanticTypes.AuthorizeToolProposal), "authorizeToolProposal must be known.");
        Program.AssertTrue(
            IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.GetTaskExecutionSnapshot),
            "Task execution snapshot is a Core read and may reconnect-replay.");
        Program.AssertTrue(
            !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.PersistProviderInvocation),
            "Provider HTTP must not auto-replay; persist is not reconnect-safe even when idempotent.");
        Program.AssertTrue(
            !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.AuthorizeToolProposal),
            "Tool authorization must not auto-replay after reconnect.");
        foreach (var type in new[]
                 {
                     IpcSemanticTypes.GetTaskExecutionSnapshot,
                     IpcSemanticTypes.PersistProviderInvocation,
                     IpcSemanticTypes.AuthorizeToolProposal
                 })
        {
            Program.AssertTrue(!type.Contains("sandbox", StringComparison.OrdinalIgnoreCase), "IPC must not expose sandbox.");
            Program.AssertTrue(!type.Contains("secret", StringComparison.OrdinalIgnoreCase), "IPC must not name secrets.");
            Program.AssertTrue(!type.Contains("credential", StringComparison.OrdinalIgnoreCase), "IPC must not name credentials.");
        }
    }

    private static void GoldenJsonForWp14Dtos()
    {
        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetTaskExecutionSnapshot, new GetTaskExecutionSnapshotRequest("run-1", "task-1", null)),
            IpcJsonContext.Default.GetTaskExecutionSnapshotRequestEnvelope,
            Payload("getTaskExecutionSnapshot", "request", "{\"runId\":\"run-1\",\"taskId\":\"task-1\"}"));
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.GetTaskExecutionSnapshot,
                new GetTaskExecutionSnapshotResponse("g1", true, true, null, [])),
            IpcJsonContext.Default.GetTaskExecutionSnapshotResponseEnvelope,
            Payload("getTaskExecutionSnapshot", "response", "{\"snapshotGeneration\":\"g1\",\"ownershipValid\":true,\"attemptLegal\":true,\"requiredResults\":[]}"));

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.PersistProviderInvocation,
                new PersistProviderInvocationRequest("inv-1", "run-1", "task-1", null, "{}", null, "{}", "g1")),
            IpcJsonContext.Default.PersistProviderInvocationRequestEnvelope,
            Payload("persistProviderInvocation", "request", "{\"invocationId\":\"inv-1\",\"runId\":\"run-1\",\"taskId\":\"task-1\",\"snapshotJson\":\"{}\",\"inputDigestSetJson\":\"{}\",\"snapshotGeneration\":\"g1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.PersistProviderInvocation, new PersistProviderInvocationResponse("cp-1", false)),
            IpcJsonContext.Default.PersistProviderInvocationResponseEnvelope,
            Payload("persistProviderInvocation", "response", "{\"checkpointId\":\"cp-1\",\"idempotentReplay\":false}"));

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.AuthorizeToolProposal,
                new AuthorizeToolProposalRequest("run-1", "task-1", "lookup", "{}", "Registry.Query", null)),
            IpcJsonContext.Default.AuthorizeToolProposalRequestEnvelope,
            Payload("authorizeToolProposal", "request", "{\"runId\":\"run-1\",\"taskId\":\"task-1\",\"toolName\":\"lookup\",\"argumentsJson\":\"{}\",\"capabilityName\":\"Registry.Query\"}"));
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.AuthorizeToolProposal,
                new AuthorizeToolProposalResponse(false, "awaitingAuthorization", null, "Registry.Query")),
            IpcJsonContext.Default.AuthorizeToolProposalResponseEnvelope,
            Payload("authorizeToolProposal", "response", "{\"allowed\":false,\"status\":\"awaitingAuthorization\",\"capabilityName\":\"Registry.Query\"}"));
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
