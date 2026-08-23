using System.Text.Json.Serialization;

namespace LLMW.Writing.Contracts.Ipc;

public enum EditorFormatKind
{
    Txt,
    Md,
    Docx
}

public enum EditorLeaseOwnerKind
{
    UserEditor,
    AgentWrite
}

public enum HistoryCheckpointTriggerKind
{
    ExplicitSave,
    Autosave,
    CrashRecovery
}

public sealed record IpcBlobRef(string Digest, long Size, string Locator);

public sealed record OpenDraftEditorSessionRequest(string ChapterId, string DraftFileName, bool RequestWritable);

public sealed record OpenDraftEditorSessionResponse(
    string EditorSessionId,
    string ProjectId,
    string ChapterId,
    string DraftFileName,
    string RelativeDraftPath,
    EditorFormatKind FormatKind,
    string BaseDiskDigest,
    string LastPersistedDigest,
    long LastPersistedRevision,
    EditorLeaseOwnerKind? LeaseOwnerKind,
    bool Writable,
    string LogicalTitle,
    long Utf8ByteLength);

public sealed record GetDraftEditorSessionStateRequest(string EditorSessionId);

public sealed record GetDraftEditorSessionStateResponse(
    string EditorSessionId,
    string LastPersistedDigest,
    long LastPersistedRevision,
    EditorLeaseOwnerKind? LeaseOwnerKind,
    bool Writable,
    bool SessionValid);

public sealed record ReleaseDraftEditorSessionRequest(string EditorSessionId);

public sealed record ReleaseDraftEditorSessionResponse(bool Released);

public sealed record BeginEditorContentDownloadRequest(string EditorSessionId, string ExpectedPersistedDigest);

public sealed record BeginEditorContentDownloadResponse(
    string DownloadId,
    int DeclaredUtf8Length,
    string DeclaredSha256,
    int ChunkCount,
    int MaxChunkBytes);

public sealed record EditorContentDownloadChunkRequest(string DownloadId, int ChunkIndex);

public sealed record EditorContentDownloadChunkResponse(int ChunkIndex, string DataBase64);

public sealed record BeginEditorContentUploadRequest(
    string EditorSessionId,
    string SaveOperationId,
    int DeclaredUtf8Length,
    string DeclaredSha256);

public sealed record BeginEditorContentUploadResponse(string UploadId, int MaxChunkBytes);

public sealed record EditorContentUploadChunkRequest(
    string UploadId,
    int ChunkIndex,
    int ChunkCount,
    string DataBase64);

public sealed record EditorContentUploadChunkResponse(int AcceptedIndex);

public sealed record CommitEditorContentUploadRequest(string UploadId);

public sealed record CommitEditorContentUploadResponse(IpcBlobRef BlobRef);

public sealed record SaveDraftEditorSessionRequest(
    string EditorSessionId,
    string SaveOperationId,
    string ExpectedPersistedDigest,
    IpcBlobRef Content,
    HistoryCheckpointTriggerKind CheckpointTrigger = HistoryCheckpointTriggerKind.ExplicitSave);

public sealed record SaveDraftEditorSessionResponse(
    string SaveOperationId,
    string PersistedDigest,
    long PersistedRevision,
    bool IdempotentReplay);

public sealed class EditorFormatKindJsonConverter : JsonStringEnumConverter<EditorFormatKind>
{
    public EditorFormatKindJsonConverter()
        : base(System.Text.Json.JsonNamingPolicy.CamelCase)
    {
    }
}

public sealed class EditorLeaseOwnerKindJsonConverter : JsonStringEnumConverter<EditorLeaseOwnerKind>
{
    public EditorLeaseOwnerKindJsonConverter()
        : base(System.Text.Json.JsonNamingPolicy.CamelCase)
    {
    }
}

public sealed class HistoryCheckpointTriggerKindJsonConverter : JsonStringEnumConverter<HistoryCheckpointTriggerKind>
{
    public HistoryCheckpointTriggerKindJsonConverter()
        : base(System.Text.Json.JsonNamingPolicy.CamelCase)
    {
    }
}
