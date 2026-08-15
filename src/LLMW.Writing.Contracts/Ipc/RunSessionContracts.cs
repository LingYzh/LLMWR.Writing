namespace LLMW.Writing.Contracts.Ipc;

/// <summary>
/// Routing data only. Authenticated channel, worker and project bindings come from Core transport state.
/// <see cref="ExpiresAtMs"/> is a caller-requested upper bound, never Core authorization truth.
/// </summary>
public sealed record CreateRunSessionRequest(string RunId, long? ExpiresAtMs);

public sealed record CreateRunSessionResponse(
    string HandleId,
    string RunId,
    string OpaqueToken,
    long ExpiresAtMs)
{
    public override string ToString() =>
        $"CreateRunSessionResponse {{ HandleId = {HandleId}, RunId = {RunId}, OpaqueToken = [REDACTED], ExpiresAtMs = {ExpiresAtMs} }}";
}

/// <summary>
/// A presented opaque secret plus routing Run ID; it is not a CallerPrincipal selector.
/// </summary>
public sealed record RunSessionProof(string RunId, string OpaqueToken)
{
    public override string ToString() =>
        $"RunSessionProof {{ RunId = {RunId}, OpaqueToken = [REDACTED] }}";
}

public sealed record RevokeRunSessionRequest(string HandleId, RunSessionProof? Session);

public sealed record RevokeRunSessionResponse(bool Revoked);
