using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Program
{
    private static readonly Guid RequestId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab");
    private static readonly Guid CorrelationId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ac");
    private static readonly Guid ProjectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ad");
    private static readonly Guid RunId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ae");
    private const long Timestamp = 1735689600000;
    private const string Workspace = "workspace-01";

    private static int Main()
    {
        try
        {
            EnvelopeSerializesToGoldenJson();
            HeartbeatRoundTripsThroughSourceGeneratedMetadata();
            FrameHeaderUsesLittleEndianAndRejectsInvalidLengths();
            ProtocolNegotiationHonorsV1Boundaries();
            PipeNamesAreWorkspaceSpecific();
            MessageIdsUseUuidV7Layout();
            RunSessionContractCannotSelectTrustedPrincipalOrBinding();
            Wp11ContractTests.Run();
            Wp12ContractTests.Run();
            Wp13ContractTests.Run();
            Wp14ContractTests.Run();
            Console.WriteLine("Contracts tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void EnvelopeSerializesToGoldenJson()
    {
        var envelope = new IpcEnvelope<HelloRequest>(
            1,
            IpcMessageType.Control,
            IpcSemanticTypes.Hello,
            RequestId,
            CorrelationId,
            ProjectId,
            Workspace,
            null,
            Timestamp,
            new HelloRequest(1, 1, "bootstrap-token", IpcClientKind.AgentRuntime, RunId));

        var actual = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, IpcJsonContext.Default.HelloRequestEnvelope));
        const string expected = "{\"protocolVersion\":1,\"messageType\":\"control\",\"semanticType\":\"hello\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"protocolMin\":1,\"protocolMax\":1,\"bootstrapToken\":\"bootstrap-token\",\"clientKind\":\"agentRuntime\",\"processInstanceId\":\"018f3e78-1234-7abc-8def-0123456789ae\"}}";
        AssertEqual(expected, actual, "Hello envelope golden JSON changed.");
    }

    private static void HeartbeatRoundTripsThroughSourceGeneratedMetadata()
    {
        var envelope = new IpcEnvelope<Heartbeat>(
            1,
            IpcMessageType.Control,
            IpcSemanticTypes.Heartbeat,
            RequestId,
            RequestId,
            null,
            Workspace,
            null,
            Timestamp,
            new Heartbeat(7));

        var serialized = IpcJson.Serialize(envelope, IpcJsonContext.Default.HeartbeatEnvelope);
        var roundTripped = IpcJson.Deserialize(serialized, IpcJsonContext.Default.HeartbeatEnvelope);
        AssertEqual(7L, roundTripped.Payload.Sequence, "Heartbeat sequence did not round-trip.");
        AssertEqual("workspace-01", roundTripped.WorkspaceInstanceId, "Workspace did not round-trip.");
        AssertEqual(IpcSemanticTypes.Heartbeat, roundTripped.SemanticType, "Heartbeat lost semanticType.");
    }

    private static void FrameHeaderUsesLittleEndianAndRejectsInvalidLengths()
    {
        var header = IpcFrameHeader.Create(0x00010203);
        AssertEqual("03020100", Convert.ToHexString(header), "Frame header is not little-endian.");
        AssertEqual(0x00010203, IpcFrameHeader.Parse(header), "Frame header did not parse.");
        AssertThrows<ArgumentOutOfRangeException>(() => IpcFrameHeader.Create(0), "Zero-length frames must be rejected.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => IpcFrameHeader.Create(IpcProtocol.MaximumFrameBytes + 1),
            "Oversize frames must be rejected.");
    }

    private static void ProtocolNegotiationHonorsV1Boundaries()
    {
        AssertTrue(IpcProtocol.TryNegotiate(1, 2, out var negotiated), "v1 should negotiate from an overlapping range.");
        AssertEqual(1, negotiated, "The highest common protocol must be selected.");
        AssertTrue(!IpcProtocol.TryNegotiate(2, 3, out _), "A non-overlapping range must fail.");
    }

    private static void PipeNamesAreWorkspaceSpecific()
    {
        AssertEqual("llmw-writing-workspace-01-runtime", IpcPipeNames.Runtime("workspace-01"), "Runtime pipe name changed.");
        AssertEqual("llmw-writing-workspace-01-w-0123456789abcdef", IpcPipeNames.Worker("workspace-01", "0123456789abcdef"), "Worker pipe name changed.");
        AssertTrue(
            !StringComparer.Ordinal.Equals(IpcPipeNames.Runtime("workspace-01"), IpcPipeNames.Runtime("workspace-02")),
            "Distinct workspace instances must not collide on a pipe name.");
    }

    private static void MessageIdsUseUuidV7Layout()
    {
        var messageId = IpcMessageIds.Create();
        var bytes = messageId.ToByteArray(bigEndian: true);
        AssertEqual(7, bytes[6] >> 4, "IPC message IDs must use UUIDv7.");
        AssertEqual(0x80, bytes[8] & 0xc0, "IPC message IDs must use the RFC 4122 variant.");
    }

    private static void RunSessionContractCannotSelectTrustedPrincipalOrBinding()
    {
        var envelope = new IpcEnvelope<CreateRunSessionRequest>(
            1,
            IpcMessageType.Request,
            IpcSemanticTypes.CreateRunSession,
            RequestId,
            CorrelationId,
            ProjectId,
            Workspace,
            RunId,
            Timestamp,
            new CreateRunSessionRequest("018f3e78-1234-7abc-8def-0123456789ae", 1735693200000));
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(
            envelope,
            IpcJsonContext.Default.CreateRunSessionRequestEnvelope));

        AssertTrue(!json.Contains("principalKind", StringComparison.OrdinalIgnoreCase),
            "RunSession IPC allowed caller-selected principalKind.");
        AssertTrue(!json.Contains("role", StringComparison.OrdinalIgnoreCase),
            "RunSession IPC allowed caller-selected role.");
        AssertTrue(!json.Contains("capability", StringComparison.OrdinalIgnoreCase),
            "RunSession IPC allowed caller-selected capability.");
        AssertTrue(!json.Contains("workerInstanceId", StringComparison.OrdinalIgnoreCase),
            "RunSession IPC accepted trusted worker binding from the ordinary payload.");
        AssertTrue(!json.Contains("channelInstanceId", StringComparison.OrdinalIgnoreCase),
            "RunSession IPC accepted trusted channel binding from the ordinary payload.");
        AssertTrue(!json.Contains("projectScope", StringComparison.OrdinalIgnoreCase),
            "RunSession IPC accepted trusted project scope from the ordinary payload.");
        var response = new CreateRunSessionResponse("handle", "run", "plaintext-secret", 1735693200000);
        var proof = new RunSessionProof("run", "plaintext-secret");
        AssertTrue(!response.ToString().Contains("plaintext-secret", StringComparison.Ordinal),
            "CreateRunSessionResponse.ToString leaked the opaque secret.");
        AssertTrue(!proof.ToString().Contains("plaintext-secret", StringComparison.Ordinal),
            "RunSessionProof.ToString leaked the opaque secret.");
        var hello = new HelloRequest(1, 1, "bootstrap-secret", IpcClientKind.AgentRuntime, Guid.NewGuid());
        AssertTrue(!hello.ToString().Contains("bootstrap-secret", StringComparison.Ordinal),
            "HelloRequest.ToString leaked the bootstrap secret.");
        var ack = new HelloAck(1, IpcServerCapabilities.V1, "stream", "conn", "rotated-secret");
        AssertTrue(!ack.ToString().Contains("rotated-secret", StringComparison.Ordinal),
            "HelloAck.ToString leaked the rotated bootstrap secret.");
    }

    internal static IpcEnvelope<T> Envelope<T>(
        IpcMessageType messageType,
        string semanticType,
        T payload,
        Guid? runId = null,
        Guid? projectId = null) =>
        new(
            1,
            messageType,
            semanticType,
            RequestId,
            CorrelationId,
            projectId ?? ProjectId,
            Workspace,
            runId,
            Timestamp,
            payload);

    internal static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void AssertEqual<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    internal static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
