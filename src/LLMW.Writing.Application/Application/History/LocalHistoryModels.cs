using LLMW.Writing.Application.Editor;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.History;

/// <summary>
/// Stable, project-owned identity for a writable Draft document. It is deliberately
/// constructed by Core from an EditorSession rather than accepted as a filesystem path.
/// </summary>
public sealed record HistoryDocumentIdentity(string ChapterId, string DraftFileName)
{
    public static HistoryDocumentIdentity From(ResolvedDraftDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HistoryDocumentIdentity(document.ChapterId, document.DraftFileName);
    }
}

/// <summary>
/// Local recovery metadata. This is not an Authority revision, Candidate, or Manuscript record.
/// </summary>
public sealed record HistoryEntry(
    string HistoryId,
    string ProjectId,
    HistoryDocumentIdentity DocumentIdentity,
    string EditorSessionId,
    string BaseDigest,
    string ContentDigest,
    long ContentLength,
    DateTimeOffset CreatedAt,
    HistoryCheckpointTriggerKind TriggerKind,
    bool IsActiveRecoveryPoint);

public sealed record LocalHistoryRetentionPolicy(
    int MaximumEntriesPerDocument,
    long MaximumProjectHistoryBytes,
    TimeSpan MaximumAge)
{
    public static LocalHistoryRetentionPolicy Default { get; } = new(
        MaximumEntriesPerDocument: 200,
        MaximumProjectHistoryBytes: 2L * 1024 * 1024 * 1024,
        MaximumAge: TimeSpan.FromDays(30));

    public void Validate()
    {
        if (MaximumEntriesPerDocument <= 0
            || MaximumProjectHistoryBytes <= 0
            || MaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LocalHistoryRetentionPolicy));
        }
    }
}

public sealed record LocalHistoryCheckpoint(
    string ProjectId,
    HistoryDocumentIdentity DocumentIdentity,
    string EditorSessionId,
    string BaseDigest,
    byte[] Content,
    HistoryCheckpointTriggerKind TriggerKind);

public sealed record LocalHistoryRestoreRequest(
    string ProjectId,
    HistoryDocumentIdentity DocumentIdentity,
    string HistoryId,
    string CurrentDigest);

public sealed record LocalHistoryRestorePayload(HistoryEntry Entry, byte[] Content);

public sealed record LocalHistoryCaptureResult(HistoryEntry? Entry);

/// <summary>
/// Metadata persistence boundary for local history. Implementations do not leak their
/// indexing format or physical paths into Application/Core behavior.
/// </summary>
public interface ILocalHistoryMetadataStore
{
    EditorResult<IReadOnlyList<HistoryEntry>> Load(CancellationToken cancellationToken = default);

    EditorResult<bool> Replace(IReadOnlyList<HistoryEntry> entries, CancellationToken cancellationToken = default);
}
