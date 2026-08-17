using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Editor;

public sealed class EditorLeaseCoordinator
{
    private readonly Dictionary<string, EditorLease> leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public EditorResult<EditorLease> TryAcquire(
        string leaseKey,
        EditorLeaseOwnerKind ownerKind,
        string ownerId,
        string editorSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorSessionId);
        lock (gate)
        {
            if (leases.TryGetValue(leaseKey, out var existing))
            {
                if (existing.OwnerKind == ownerKind
                    && StringComparer.Ordinal.Equals(existing.OwnerId, ownerId)
                    && StringComparer.Ordinal.Equals(existing.EditorSessionId, editorSessionId))
                {
                    return EditorResult<EditorLease>.Ok(existing);
                }

                return EditorResult<EditorLease>.Fail(IpcErrorCodes.EditorLeaseConflict);
            }

            var acquired = new EditorLease(leaseKey, ownerKind, ownerId, editorSessionId);
            leases[leaseKey] = acquired;
            return EditorResult<EditorLease>.Ok(acquired);
        }
    }

    public EditorLease? Get(string leaseKey)
    {
        lock (gate)
        {
            return leases.TryGetValue(leaseKey, out var lease) ? lease : null;
        }
    }

    public bool Owns(string leaseKey, EditorLeaseOwnerKind ownerKind, string ownerId, string editorSessionId)
    {
        lock (gate)
        {
            return leases.TryGetValue(leaseKey, out var lease)
                   && lease.OwnerKind == ownerKind
                   && StringComparer.Ordinal.Equals(lease.OwnerId, ownerId)
                   && StringComparer.Ordinal.Equals(lease.EditorSessionId, editorSessionId);
        }
    }

    public void Release(string leaseKey, EditorLeaseOwnerKind ownerKind, string ownerId, string? editorSessionId)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(leaseKey, out var lease))
            {
                return;
            }

            if (lease.OwnerKind != ownerKind || !StringComparer.Ordinal.Equals(lease.OwnerId, ownerId))
            {
                return;
            }

            if (editorSessionId is not null
                && !StringComparer.Ordinal.Equals(lease.EditorSessionId, editorSessionId))
            {
                return;
            }

            leases.Remove(leaseKey);
        }
    }

    public void ReleaseByOwner(EditorLeaseOwnerKind ownerKind, string ownerId)
    {
        lock (gate)
        {
            var keys = leases
                .Where(pair => pair.Value.OwnerKind == ownerKind
                               && StringComparer.Ordinal.Equals(pair.Value.OwnerId, ownerId))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys)
            {
                leases.Remove(key);
            }
        }
    }

    public T WithDocumentLock<T>(string leaseKey, Func<T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseKey);
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            return action();
        }
    }

    public EditorResult<EditorLease> Transfer(
        string leaseKey,
        EditorLeaseOwnerKind fromKind,
        string fromOwnerId,
        EditorLeaseOwnerKind toKind,
        string toOwnerId,
        string toEditorSessionId,
        string expectedDigest,
        string currentDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDigest);
        if (!StringComparer.Ordinal.Equals(expectedDigest, currentDigest))
        {
            return EditorResult<EditorLease>.Fail(IpcErrorCodes.EditorStaleBase);
        }

        lock (gate)
        {
            if (!leases.TryGetValue(leaseKey, out var existing)
                || existing.OwnerKind != fromKind
                || !StringComparer.Ordinal.Equals(existing.OwnerId, fromOwnerId))
            {
                return EditorResult<EditorLease>.Fail(IpcErrorCodes.EditorLeaseLost);
            }

            var transferred = new EditorLease(leaseKey, toKind, toOwnerId, toEditorSessionId);
            leases[leaseKey] = transferred;
            return EditorResult<EditorLease>.Ok(transferred);
        }
    }
}
