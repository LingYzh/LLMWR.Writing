using System.Text;
using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Program
{
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
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"),
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789ac"),
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789ad"),
            "workspace-01",
            null,
            1735689600000,
            new HelloRequest(1, 1, "bootstrap-token", IpcClientKind.AgentRuntime, Guid.Parse("018f3e78-1234-7abc-8def-0123456789ae")));

        var actual = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, IpcJsonContext.Default.HelloRequestEnvelope));
        const string expected = "{\"protocolVersion\":1,\"messageType\":\"control\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"protocolMin\":1,\"protocolMax\":1,\"bootstrapToken\":\"bootstrap-token\",\"clientKind\":\"agentRuntime\",\"processInstanceId\":\"018f3e78-1234-7abc-8def-0123456789ae\"}}";
        AssertEqual(expected, actual, "Hello envelope golden JSON changed.");
    }

    private static void HeartbeatRoundTripsThroughSourceGeneratedMetadata()
    {
        var envelope = new IpcEnvelope<Heartbeat>(
            1,
            IpcMessageType.Control,
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"),
            Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"),
            null,
            "workspace-01",
            null,
            1735689600000,
            new Heartbeat(7));

        var serialized = IpcJson.Serialize(envelope, IpcJsonContext.Default.HeartbeatEnvelope);
        var roundTripped = IpcJson.Deserialize(serialized, IpcJsonContext.Default.HeartbeatEnvelope);
        AssertEqual(7L, roundTripped.Payload.Sequence, "Heartbeat sequence did not round-trip.");
        AssertEqual("workspace-01", roundTripped.WorkspaceInstanceId, "Workspace did not round-trip.");
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

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
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
