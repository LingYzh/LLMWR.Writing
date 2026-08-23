using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.History;

/// <summary>
/// Core-side local history coordinator. It only snapshots and restores Draft bytes;
/// Authority, Candidate and Current Manuscript state are intentionally outside this service.
/// </summary>
public sealed class LocalHistoryService
{
    private readonly IImmutableBlobStore blobs;
    private readonly ILocalHistoryMetadataStore metadata;
    private readonly LocalHistoryRetentionPolicy retention;
    private readonly TimeProvider clock;
    private readonly object gate = new();

    public LocalHistoryService(
        IImmutableBlobStore blobs,
        ILocalHistoryMetadataStore metadata,
        LocalHistoryRetentionPolicy? retention = null,
        TimeProvider? clock = null)
    {
        this.blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        this.retention = retention ?? LocalHistoryRetentionPolicy.Default;
        this.retention.Validate();
        this.clock = clock ?? TimeProvider.System;
    }

    public EditorResult<LocalHistoryCaptureResult> Capture(
        LocalHistoryCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!IsCheckpointValid(checkpoint))
        {
            return EditorResult<LocalHistoryCaptureResult>.Fail(IpcErrorCodes.HistoryEntryInvalid);
        }

        if (StringComparer.Ordinal.Equals(
                ContentDigest.Normalize(checkpoint.BaseDigest),
                ContentDigest.Sha256Hex(checkpoint.Content)))
        {
            return EditorResult<LocalHistoryCaptureResult>.Ok(new LocalHistoryCaptureResult(null));
        }

        lock (gate)
        {
            try
            {
                using var content = new MemoryStream(checkpoint.Content, writable: false);
                var staged = blobs.Stage(content, cancellationToken: cancellationToken);
                var contentDigest = ContentDigest.Normalize(staged.Digest);
                if (!blobs.Verify(contentDigest, cancellationToken)
                    || !StringComparer.Ordinal.Equals(contentDigest, ContentDigest.Sha256Hex(checkpoint.Content)))
                {
                    return EditorResult<LocalHistoryCaptureResult>.Fail(IpcErrorCodes.HistoryStorageFailure);
                }

                var loaded = metadata.Load(cancellationToken);
                if (!loaded.Succeeded)
                {
                    return EditorResult<LocalHistoryCaptureResult>.Fail(loaded.ErrorCode!);
                }

                var isActiveRecoveryPoint = checkpoint.TriggerKind == HistoryCheckpointTriggerKind.CrashRecovery;
                var entry = new HistoryEntry(
                    IpcMessageIds.Create().ToString("D"),
                    checkpoint.ProjectId,
                    checkpoint.DocumentIdentity,
                    checkpoint.EditorSessionId,
                    ContentDigest.Normalize(checkpoint.BaseDigest),
                    contentDigest,
                    staged.Length,
                    clock.GetUtcNow(),
                    checkpoint.TriggerKind,
                    isActiveRecoveryPoint);
                var retained = loaded.Value!
                    .Select(existing => isActiveRecoveryPoint
                        && SameDocument(existing, checkpoint.ProjectId, checkpoint.DocumentIdentity)
                        && existing.IsActiveRecoveryPoint
                            ? existing with { IsActiveRecoveryPoint = false }
                            : existing)
                    .Append(entry)
                    .ToList();
                ApplyRetention(retained, clock.GetUtcNow());

                var saved = metadata.Replace(retained, cancellationToken);
                return !saved.Succeeded
                    ? EditorResult<LocalHistoryCaptureResult>.Fail(saved.ErrorCode!)
                    : retained.Any(item => StringComparer.Ordinal.Equals(item.HistoryId, entry.HistoryId))
                        ? EditorResult<LocalHistoryCaptureResult>.Ok(new LocalHistoryCaptureResult(entry))
                        : EditorResult<LocalHistoryCaptureResult>.Ok(new LocalHistoryCaptureResult(null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return EditorResult<LocalHistoryCaptureResult>.Fail(IpcErrorCodes.HistoryStorageFailure);
            }
        }
    }

    public EditorResult<IReadOnlyList<HistoryEntry>> List(
        string projectId,
        HistoryDocumentIdentity documentIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!IsIdentityValid(projectId, documentIdentity))
        {
            return EditorResult<IReadOnlyList<HistoryEntry>>.Fail(IpcErrorCodes.HistoryEntryInvalid);
        }

        lock (gate)
        {
            var loaded = metadata.Load(cancellationToken);
            if (!loaded.Succeeded)
            {
                return EditorResult<IReadOnlyList<HistoryEntry>>.Fail(loaded.ErrorCode!);
            }

            return EditorResult<IReadOnlyList<HistoryEntry>>.Ok(loaded.Value!
                .Where(entry => SameDocument(entry, projectId, documentIdentity))
                .OrderByDescending(entry => entry.CreatedAt)
                .ThenByDescending(entry => entry.HistoryId, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public EditorResult<LocalHistoryRestorePayload> ReadForRestore(
        LocalHistoryRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsIdentityValid(request.ProjectId, request.DocumentIdentity)
            || !Guid.TryParse(request.HistoryId, out _)
            || !ContentDigest.IsSha256Hex(request.CurrentDigest))
        {
            return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryEntryInvalid);
        }

        lock (gate)
        {
            var loaded = metadata.Load(cancellationToken);
            if (!loaded.Succeeded)
            {
                return EditorResult<LocalHistoryRestorePayload>.Fail(loaded.ErrorCode!);
            }

            var entry = loaded.Value!.SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item.HistoryId, request.HistoryId));
            if (entry is null)
            {
                return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryEntryNotFound);
            }

            if (!SameDocument(entry, request.ProjectId, request.DocumentIdentity))
            {
                return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryEntryInvalid);
            }

            if (!StringComparer.Ordinal.Equals(entry.BaseDigest, ContentDigest.Normalize(request.CurrentDigest)))
            {
                return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryRestoreConflict);
            }

            try
            {
                if (!blobs.Verify(entry.ContentDigest, cancellationToken))
                {
                    return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryStorageFailure);
                }

                using var stream = blobs.OpenRead(entry.ContentDigest);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var content = memory.ToArray();
                if (content.LongLength != entry.ContentLength
                    || !StringComparer.Ordinal.Equals(ContentDigest.Sha256Hex(content), entry.ContentDigest))
                {
                    return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryStorageFailure);
                }

                return EditorResult<LocalHistoryRestorePayload>.Ok(new LocalHistoryRestorePayload(entry, content));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return EditorResult<LocalHistoryRestorePayload>.Fail(IpcErrorCodes.HistoryStorageFailure);
            }
        }
    }

    private void ApplyRetention(List<HistoryEntry> entries, DateTimeOffset now)
    {
        foreach (var expired in entries
                     .Where(entry => !entry.IsActiveRecoveryPoint && now - entry.CreatedAt > retention.MaximumAge)
                     .OrderBy(entry => entry.CreatedAt)
                     .ThenBy(entry => entry.HistoryId, StringComparer.Ordinal)
                     .ToArray())
        {
            entries.Remove(expired);
        }

        foreach (var group in entries
                     .Select(entry => new { entry.ProjectId, entry.DocumentIdentity })
                     .Distinct()
                     .ToArray())
        {
            while (entries.Count(entry =>
                       StringComparer.Ordinal.Equals(entry.ProjectId, group.ProjectId)
                       && entry.DocumentIdentity == group.DocumentIdentity) > retention.MaximumEntriesPerDocument)
            {
                var removable = entries
                    .Where(entry =>
                        StringComparer.Ordinal.Equals(entry.ProjectId, group.ProjectId)
                        && entry.DocumentIdentity == group.DocumentIdentity)
                    .Where(entry => !entry.IsActiveRecoveryPoint)
                    .OrderBy(entry => entry.CreatedAt)
                    .ThenBy(entry => entry.HistoryId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (removable is null)
                {
                    break;
                }

                entries.Remove(removable);
            }
        }

        while (RetainedBytes(entries) > retention.MaximumProjectHistoryBytes)
        {
            var removable = entries
                .Where(entry => !entry.IsActiveRecoveryPoint)
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.HistoryId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (removable is null)
            {
                break;
            }

            entries.Remove(removable);
        }
    }

    private static long RetainedBytes(IEnumerable<HistoryEntry> entries) => entries.Sum(entry => entry.ContentLength);

    private static bool IsCheckpointValid(LocalHistoryCheckpoint checkpoint) =>
        IsIdentityValid(checkpoint.ProjectId, checkpoint.DocumentIdentity)
        && Guid.TryParse(checkpoint.EditorSessionId, out _)
        && ContentDigest.IsSha256Hex(checkpoint.BaseDigest)
        && checkpoint.Content is not null
        && checkpoint.Content.Length <= EditorTransportLimits.MaximumDocumentUtf8Bytes
        && Enum.IsDefined(checkpoint.TriggerKind);

    private static bool IsIdentityValid(string projectId, HistoryDocumentIdentity documentIdentity) =>
        Guid.TryParse(projectId, out _)
        && documentIdentity is not null
        && DraftDocumentResolver.Resolve(documentIdentity.ChapterId, documentIdentity.DraftFileName).Succeeded;

    private static bool SameDocument(HistoryEntry entry, string projectId, HistoryDocumentIdentity documentIdentity) =>
        StringComparer.Ordinal.Equals(entry.ProjectId, projectId)
        && entry.DocumentIdentity == documentIdentity;
}
