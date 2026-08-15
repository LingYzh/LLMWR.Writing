using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// Worker RunSession issuance. The opaque token stays in process memory only.
/// Failure fails closed; callers must not swallow <see cref="IpcProtocolException"/>.
/// </summary>
public static class WorkerSessionBootstrap
{
    public static async Task<CreateRunSessionResponse> EstablishAsync(
        IpcClientSession session,
        string boundRunId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundRunId);
        var issued = await session.RequestAsync(
                IpcSemanticTypes.CreateRunSession,
                new CreateRunSessionRequest(boundRunId, null),
                IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(issued.Payload.OpaqueToken) ||
            !StringComparer.Ordinal.Equals(issued.Payload.RunId, boundRunId))
        {
            throw new InvalidOperationException("Worker RunSession issuance failed closed.");
        }

        return issued.Payload;
    }
}
