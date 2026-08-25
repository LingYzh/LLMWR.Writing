using System.Diagnostics;
using System.IO.Pipes;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Application.Security;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly CallerPrincipal Wp09UserPrincipal =
        new TrustedNativePrincipalSource("integration-tests").ResolveUserInteractive();
    private static readonly CoreAuthorizationService Wp09Authorization = new(new Wp09TestSecurityPolicySource());
    private static async Task<int> Main()
    {
        try
        {
            RunWp05Tests();
            RunWp06Tests();
            RunWp07Tests();
            RunWp08Tests();
            RunWp09Tests();
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("WP10 Windows sandbox tests cannot be skipped on a non-Windows runner.");
            }

            RunWp10Tests();
            await RunWp11TestsAsync();
            await RunWp12TestsAsync();
            RunWp13Tests();
            RunWp14Tests();
            await RunWp16TestsAsync();
            await RunWp17TestsAsync();
            RunWp20Tests();
            RunWp21Tests();
            await ReconnectsAfterCoreRestartAsync();
            Console.WriteLine("Integration tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task ReconnectsAfterCoreRestartAsync()
    {
        var workspaceInstanceId = $"smoke-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var firstCore = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        Process? restartedCore = null;

        try
        {
            var ack = await AssertHandshakeAndHeartbeatAsync(workspaceInstanceId, bootstrapToken, 1, testTimeout.Token);
            await AssertClientKindIsRejectedAsync(workspaceInstanceId, ack.RotatedBootstrapToken ?? bootstrapToken, testTimeout.Token);
            StopCore(firstCore);

            restartedCore = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
            await AssertHandshakeAndHeartbeatAsync(workspaceInstanceId, bootstrapToken, 2, testTimeout.Token);
        }
        finally
        {
            StopCore(firstCore);
            if (restartedCore is not null)
            {
                StopCore(restartedCore);
                restartedCore.Dispose();
            }
        }
    }

    private static Process StartCore(
        string workspaceInstanceId,
        string uiBootstrapToken,
        string runtimeBootstrapToken,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var coreAssembly = Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "LLMW.Writing.Core",
            "bin",
            "Release",
            "net8.0",
            "LLMW.Writing.Core.dll");
        if (!File.Exists(coreAssembly))
        {
            throw new FileNotFoundException("Build the Release Core shell before running integration tests.", coreAssembly);
        }

        var startInfo = new ProcessStartInfo("dotnet", $"\"{coreAssembly}\"")
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["LLMW_WORKSPACE_INSTANCE_ID"] = workspaceInstanceId;
        startInfo.Environment["LLMW_UI_BOOTSTRAP_TOKEN"] = uiBootstrapToken;
        startInfo.Environment["LLMW_RUNTIME_BOOTSTRAP_TOKEN"] = runtimeBootstrapToken;
        if (extraEnvironment is not null)
        {
            foreach (var pair in extraEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Core process did not start.");
    }

    private static void StopCore(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    private static async Task<HelloAck> AssertHandshakeAndHeartbeatAsync(
        string workspaceInstanceId,
        string bootstrapToken,
        long sequence,
        CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(
            ".",
            IpcPipeNames.Runtime(workspaceInstanceId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(cancellationToken).WaitAsync(cancellationToken);

        var hello = new HelloRequest(1, 1, bootstrapToken, IpcClientKind.AgentRuntime, Guid.NewGuid());
        await WriteAsync(
            client,
            IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, workspaceInstanceId, hello),
            IpcJsonContext.Default.HelloRequestEnvelope,
            cancellationToken);
        var helloAck = await ReadAsync(client, IpcJsonContext.Default.HelloAckEnvelope, cancellationToken);
        AssertEqual(1, helloAck.Payload.NegotiatedProtocol, "Core did not negotiate IPC v1.");

        await WriteAsync(
            client,
            IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Heartbeat, workspaceInstanceId, new Heartbeat(sequence)),
            IpcJsonContext.Default.HeartbeatEnvelope,
            cancellationToken);
        var heartbeatAck = await ReadAsync(client, IpcJsonContext.Default.HeartbeatAckEnvelope, cancellationToken);
        AssertEqual(sequence, heartbeatAck.Payload.Sequence, "Core did not acknowledge the heartbeat.");
        return helloAck.Payload;
    }

    private static async Task AssertClientKindIsRejectedAsync(
        string workspaceInstanceId,
        string runtimeBootstrapToken,
        CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(
            ".",
            IpcPipeNames.Runtime(workspaceInstanceId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(cancellationToken).WaitAsync(cancellationToken);

        var hello = new HelloRequest(1, 1, runtimeBootstrapToken, IpcClientKind.Ui, Guid.NewGuid());
        await WriteAsync(
            client,
            IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, workspaceInstanceId, hello),
            IpcJsonContext.Default.HelloRequestEnvelope,
            cancellationToken);
        var error = await ReadAsync(client, IpcJsonContext.Default.ErrorEnvelope, cancellationToken);
        AssertEqual(IpcErrorCodes.AuthBootstrapRejected, error.Payload.Code, "Core must bind bootstrap tokens to their client kind.");
    }

    private static async Task WriteAsync<TPayload>(
        Stream stream,
        IpcEnvelope<TPayload> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = IpcJson.Serialize(envelope, typeInfo);
        await stream.WriteAsync(IpcFrameHeader.Create(payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<IpcEnvelope<TPayload>> ReadAsync<TPayload>(
        Stream stream,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var payload = new byte[IpcFrameHeader.Parse(header)];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return IpcJson.Deserialize(payload, typeInfo);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Core closed the connection before its frame was complete.");
            }

            totalRead += read;
        }
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
}
