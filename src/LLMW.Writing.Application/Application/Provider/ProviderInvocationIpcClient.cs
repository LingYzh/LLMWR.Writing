using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Application.Provider;

public interface IAuthenticatedIpcRequestClient
{
    IpcEnvelope<TResponse> Request<TRequest, TResponse>(
        string semanticType,
        TRequest payload,
        JsonTypeInfo<IpcEnvelope<TRequest>> requestInfo,
        JsonTypeInfo<IpcEnvelope<TResponse>> responseInfo,
        Guid? projectId,
        Guid? runId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production Agent Runtime transport over an authenticated WP11 <see cref="IpcClientSession"/>.
/// </summary>
public sealed class IpcClientSessionRequestClient : IAuthenticatedIpcRequestClient
{
    private readonly IpcClientSession session;

    public IpcClientSessionRequestClient(IpcClientSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public IpcEnvelope<TResponse> Request<TRequest, TResponse>(
        string semanticType,
        TRequest payload,
        JsonTypeInfo<IpcEnvelope<TRequest>> requestInfo,
        JsonTypeInfo<IpcEnvelope<TResponse>> responseInfo,
        Guid? projectId,
        Guid? runId,
        CancellationToken cancellationToken) =>
        session.RequestAsync(
                semanticType,
                payload,
                requestInfo,
                responseInfo,
                cancellationToken,
                projectId,
                runId)
            .GetAwaiter()
            .GetResult();
}

/// <summary>
/// In-process fake that calls an <see cref="IIpcApplicationCommandHandler"/> without WP11 framing.
/// Production composition must use <see cref="IpcClientSessionRequestClient"/>.
/// </summary>
public sealed class FakeIpcApplicationCommandTransport : IAuthenticatedIpcRequestClient
{
    private readonly IIpcApplicationCommandHandler handler;
    private readonly string workspaceInstanceId;
    private readonly CallerPrincipal? principal;
    private readonly AuthenticatedChannelContext? channel;

    public FakeIpcApplicationCommandTransport(
        IIpcApplicationCommandHandler handler,
        string workspaceInstanceId,
        CallerPrincipal? principal = null,
        AuthenticatedChannelContext? channel = null)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
        this.principal = principal;
        this.channel = channel;
    }

    public IpcEnvelope<TResponse> Request<TRequest, TResponse>(
        string semanticType,
        TRequest payload,
        JsonTypeInfo<IpcEnvelope<TRequest>> requestInfo,
        JsonTypeInfo<IpcEnvelope<TResponse>> responseInfo,
        Guid? projectId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        var envelope = IpcEnvelopeFactory.Create(
            IpcMessageType.Request,
            semanticType,
            workspaceInstanceId,
            payload,
            projectId,
            runId);
        var utf8 = IpcJson.Serialize(envelope, requestInfo);
        var wire = IpcJson.DeserializeWire(utf8);
        var context = new IpcApplicationCommandContext(
            IpcClientKind.AgentRuntime,
            "wp14-fake-ipc",
            channel,
            principal,
            wire.RequestId,
            wire.CorrelationId,
            projectId,
            runId,
            semanticType,
            wire.Payload.Clone(),
            cancellationToken);
        var result = handler.HandleAsync(context).GetAwaiter().GetResult()
                     ?? throw new InvalidOperationException("WP14 IPC command was not handled.");
        var responseWire = IpcJson.DeserializeWire(result.ResponseUtf8);
        if (responseWire.Payload.TryGetProperty("code", out _))
        {
            var error = IpcJson.DeserializePayload(responseWire.Payload, IpcJsonContext.Default.IpcError);
            throw new IpcProtocolException(error.Code, error.Message);
        }

        return IpcJson.Deserialize(result.ResponseUtf8, responseInfo);
    }
}

public sealed class AuthenticatedProviderInvocationStateClient : IProviderInvocationStatePort
{
    private readonly IAuthenticatedIpcRequestClient client;
    private readonly RunSessionProof proof;
    private readonly Guid? projectId;

    public AuthenticatedProviderInvocationStateClient(
        IAuthenticatedIpcRequestClient client,
        RunSessionProof proof,
        Guid? projectId = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.proof = proof ?? throw new ArgumentNullException(nameof(proof));
        this.projectId = projectId;
    }

    public GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request) =>
        Exchange(
            IpcSemanticTypes.GetTaskExecutionSnapshot,
            request with { Session = proof },
            IpcJsonContext.Default.GetTaskExecutionSnapshotRequestEnvelope,
            IpcJsonContext.Default.GetTaskExecutionSnapshotResponseEnvelope,
            request.RunId);

    public PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request)
    {
        AssertNoSecret(request.SnapshotJson);
        AssertNoSecret(request.RecordJson);
        return Exchange(
            IpcSemanticTypes.PersistProviderInvocation,
            request with { Session = proof },
            IpcJsonContext.Default.PersistProviderInvocationRequestEnvelope,
            IpcJsonContext.Default.PersistProviderInvocationResponseEnvelope,
            request.RunId);
    }

    public AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request) =>
        Exchange(
            IpcSemanticTypes.AuthorizeToolProposal,
            request with { Session = proof },
            IpcJsonContext.Default.AuthorizeToolProposalRequestEnvelope,
            IpcJsonContext.Default.AuthorizeToolProposalResponseEnvelope,
            request.RunId);

    private TResponse Exchange<TRequest, TResponse>(
        string semanticType,
        TRequest payload,
        JsonTypeInfo<IpcEnvelope<TRequest>> requestInfo,
        JsonTypeInfo<IpcEnvelope<TResponse>> responseInfo,
        string runId)
    {
        var envelopeRun = Guid.TryParse(runId, out var parsed) ? parsed : (Guid?)null;
        var envelope = client.Request(
            semanticType,
            payload,
            requestInfo,
            responseInfo,
            projectId,
            envelopeRun,
            CancellationToken.None);
        return envelope.Payload;
    }

    private static void AssertNoSecret(string? json)
    {
        if (!string.IsNullOrEmpty(json) && SecretRedaction.ContainsSecretMaterial(json))
        {
            throw new InvalidOperationException(IpcErrorCodes.ProviderSecretForbidden);
        }
    }
}

public static class ProviderRuntimeComposition
{
    public static ProviderInvocationCoordinator Create(
        IpcClientSession session,
        RunSessionProof proof,
        IProviderDefinitionStore definitions,
        IProviderCredentialResolver credentials,
        IModelCertificationStore protocolProfiles,
        IPriceSnapshotStore prices,
        IProviderAdapterResolver adapters,
        IModelCatalogStore? catalog = null,
        ITaskCertificationStore? taskCertifications = null,
        TimeProvider? clock = null) =>
        new(
            definitions,
            credentials,
            protocolProfiles,
            prices,
            adapters,
            new AuthenticatedProviderInvocationStateClient(new IpcClientSessionRequestClient(session), proof),
            catalog,
            taskCertifications,
            clock);
}
