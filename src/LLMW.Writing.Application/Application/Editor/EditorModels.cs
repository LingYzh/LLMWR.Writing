using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Editor;

public readonly struct EditorResult<T>
{
    private EditorResult(T? value, string? errorCode)
    {
        Value = value;
        ErrorCode = errorCode;
    }

    public T? Value { get; }

    public string? ErrorCode { get; }

    public bool Succeeded => ErrorCode is null && Value is not null;

#pragma warning disable CA1000
    public static EditorResult<T> Ok(T value) => new(value, null);

    public static EditorResult<T> Fail(string errorCode) => new(default, errorCode);
#pragma warning restore CA1000
}

public enum EditorSaveFaultPoint
{
    None = 0,
    AfterUploadStaging,
    AfterUploadHashVerify,
    BeforeFinalFreshnessCheck,
    AfterTempFileWrite,
    AfterFlush,
    BeforeAtomicReplace,
    AfterAtomicReplace,
    BeforeIpcResponse
}

public interface IEditorSaveFaultInjector
{
    void ThrowIf(EditorSaveFaultPoint point);
}

public sealed class NoEditorSaveFaultInjector : IEditorSaveFaultInjector
{
    public static NoEditorSaveFaultInjector Instance { get; } = new();

    private NoEditorSaveFaultInjector()
    {
    }

    public void ThrowIf(EditorSaveFaultPoint point)
    {
        _ = point;
    }
}

public sealed class MutableEditorSaveFaultInjector : IEditorSaveFaultInjector
{
    public EditorSaveFaultPoint Fault { get; set; }

    public void ThrowIf(EditorSaveFaultPoint point)
    {
        if (Fault == point && point != EditorSaveFaultPoint.None)
        {
            throw new EditorSaveFaultInjectedException(point);
        }
    }
}

public sealed class EditorSaveFaultInjectedException : Exception
{
    public EditorSaveFaultInjectedException(EditorSaveFaultPoint point)
        : base("Editor save fault injected: " + point)
    {
        Point = point;
    }

    public EditorSaveFaultPoint Point { get; }
}

public sealed record ResolvedDraftDocument(
    string ChapterId,
    string DraftFileName,
    string RelativePath,
    EditorFormatKind FormatKind,
    string LogicalTitle);

public sealed record DraftFileSnapshot(
    string RelativePath,
    string LeaseKey,
    byte[] Bytes,
    string Digest,
    long Length);

public interface IDraftFileStore
{
    EditorResult<string> ResolveLeaseKey(string relativePath);

    EditorResult<DraftFileSnapshot> Read(string relativePath);

    EditorResult<DraftFileSnapshot> ReadFromLeaseKey(string leaseKey);

    string DigestOf(ReadOnlySpan<byte> bytes);

    EditorResult<DraftFileSnapshot> AtomicReplace(
        string relativePath,
        string expectedDigest,
        byte[] utf8NoBomLf,
        IEditorSaveFaultInjector faults);
}

public sealed class EditorSession
{
    public EditorSession(
        string editorSessionId,
        string projectId,
        string connectionId,
        ResolvedDraftDocument document,
        string leaseKey,
        string baseDiskDigest,
        string lastPersistedDigest,
        long lastPersistedRevision,
        EditorLeaseOwnerKind? leaseOwnerKind,
        bool writable)
    {
        EditorSessionId = editorSessionId;
        ProjectId = projectId;
        ConnectionId = connectionId;
        Document = document;
        LeaseKey = leaseKey;
        BaseDiskDigest = baseDiskDigest;
        LastPersistedDigest = lastPersistedDigest;
        LastPersistedRevision = lastPersistedRevision;
        LeaseOwnerKind = leaseOwnerKind;
        Writable = writable;
    }

    public string EditorSessionId { get; }
    public string ProjectId { get; }
    public string ConnectionId { get; }
    public ResolvedDraftDocument Document { get; }
    public string LeaseKey { get; }
    public string BaseDiskDigest { get; }
    public string LastPersistedDigest { get; set; }
    public long LastPersistedRevision { get; set; }
    public EditorLeaseOwnerKind? LeaseOwnerKind { get; set; }
    public bool Writable { get; set; }
}

public sealed record EditorLease(
    string LeaseKey,
    EditorLeaseOwnerKind OwnerKind,
    string OwnerId,
    string EditorSessionId);
