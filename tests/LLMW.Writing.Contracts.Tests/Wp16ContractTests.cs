using System.Text;
using System.Text.Json.Serialization.Metadata;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp16ContractTests
{
    private const string ChapterId = "018f3e78-1234-7abc-8def-0123456789a1";
    private const string SessionId = "018f3e78-1234-7abc-8def-0123456789b1";
    private const string DigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static void Run()
    {
        EditorSemanticTypesAreKnownAndAreNotReplaySafe();
        EditorCommandsAreNotGenericFilesystem();
        NonEquivalenceIdentitiesRemainDistinct();
        GoldenJsonForWp16Dtos();
        ContentDigestIsSha256Hex();
        FrameLimitIsUnchanged();
        Console.WriteLine("WP16 contract tests passed.");
    }

    private static void EditorSemanticTypesAreKnownAndAreNotReplaySafe()
    {
        foreach (var type in EditorTypes())
        {
            Program.AssertTrue(IpcSemanticTypes.IsKnown(type), type + " must be known.");
            Program.AssertTrue(
                IpcSemanticTypes.IsWellFormed(type, IpcMessageType.Request),
                type + " must be a request.");
            Program.AssertTrue(
                IpcSemanticTypes.IsWellFormed(type, IpcMessageType.Response),
                type + " must be a response.");
            Program.AssertTrue(
                !IpcSemanticTypes.IsSafeToReplayAfterReconnect(type),
                type + " must not auto-replay after reconnect.");
        }
    }

    private static void EditorCommandsAreNotGenericFilesystem()
    {
        foreach (var type in IpcSemanticTypes.All)
        {
            Program.AssertTrue(type is not "readFile" and not "writeFile" and not "file.invoke"
                and not "filesystem.request" and not "editor.invoke" and not "genericBlobWriteToPath",
                "Generic filesystem/editor RPC must not exist: " + type);
        }
    }

    private static void NonEquivalenceIdentitiesRemainDistinct()
    {
        Program.AssertTrue(
            !string.Equals(IpcSemanticTypes.OpenDraftEditorSession, IpcSemanticTypes.CreateRunSession, StringComparison.Ordinal),
            "EditorSession commands must not be RunSession commands.");
        Program.AssertTrue(
            !string.Equals(IpcSemanticTypes.SaveDraftEditorSession, IpcSemanticTypes.AcceptAuthority, StringComparison.Ordinal),
            "Draft save must not be Authority accept.");
        Program.AssertTrue(
            !string.Equals(IpcSemanticTypes.SaveDraftEditorSession, IpcSemanticTypes.SubmitCandidate, StringComparison.Ordinal),
            "Draft save must not be Candidate submit.");
        Program.AssertTrue(
            IpcErrorCodes.EditorStaleBase != IpcErrorCodes.EditorLeaseConflict,
            "Stale base and lease conflict must remain distinct.");
        Program.AssertTrue(
            IpcErrorCodes.EditorSaveOutcomeUnknown != IpcErrorCodes.EditorStaleBase,
            "Unknown save outcome must remain distinct from stale base.");
        Program.AssertTrue(
            IpcErrorCodes.HistoryRestoreConflict != IpcErrorCodes.EditorStaleBase,
            "Local History conflict must remain distinct from ordinary editor save conflict.");
    }

    private static void GoldenJsonForWp16Dtos()
    {
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(ChapterId, "chapter.md", true)),
            IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
            Payload("openDraftEditorSession", "request",
                "{\"chapterId\":\"" + ChapterId + "\",\"draftFileName\":\"chapter.md\",\"requestWritable\":true}"));
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionResponse(
                    SessionId,
                    "018f3e78-1234-7abc-8def-0123456789ad",
                    ChapterId,
                    "chapter.md",
                    "Draft/" + ChapterId + "/chapter.md",
                    EditorFormatKind.Md,
                    DigestA,
                    DigestA,
                    1,
                    EditorLeaseOwnerKind.UserEditor,
                    true,
                    "chapter.md",
                    12)),
            IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
            Payload("openDraftEditorSession", "response",
                "{\"editorSessionId\":\"" + SessionId +
                "\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"chapterId\":\"" + ChapterId +
                "\",\"draftFileName\":\"chapter.md\",\"relativeDraftPath\":\"Draft/" + ChapterId +
                "/chapter.md\",\"formatKind\":\"md\",\"baseDiskDigest\":\"" + DigestA +
                "\",\"lastPersistedDigest\":\"" + DigestA +
                "\",\"lastPersistedRevision\":1,\"leaseOwnerKind\":\"userEditor\",\"writable\":true,\"logicalTitle\":\"chapter.md\",\"utf8ByteLength\":12}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.GetDraftEditorSessionState, new GetDraftEditorSessionStateRequest(SessionId)),
            IpcJsonContext.Default.GetDraftEditorSessionStateRequestEnvelope,
            Payload("getDraftEditorSessionState", "request", "{\"editorSessionId\":\"" + SessionId + "\"}"));
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.GetDraftEditorSessionState,
                new GetDraftEditorSessionStateResponse(SessionId, DigestA, 1, EditorLeaseOwnerKind.UserEditor, true, true)),
            IpcJsonContext.Default.GetDraftEditorSessionStateResponseEnvelope,
            Payload("getDraftEditorSessionState", "response",
                "{\"editorSessionId\":\"" + SessionId + "\",\"lastPersistedDigest\":\"" + DigestA +
                "\",\"lastPersistedRevision\":1,\"leaseOwnerKind\":\"userEditor\",\"writable\":true,\"sessionValid\":true}"));

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ReleaseDraftEditorSession, new ReleaseDraftEditorSessionRequest(SessionId)),
            IpcJsonContext.Default.ReleaseDraftEditorSessionRequestEnvelope,
            Payload("releaseDraftEditorSession", "request", "{\"editorSessionId\":\"" + SessionId + "\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ReleaseDraftEditorSession, new ReleaseDraftEditorSessionResponse(true)),
            IpcJsonContext.Default.ReleaseDraftEditorSessionResponseEnvelope,
            Payload("releaseDraftEditorSession", "response", "{\"released\":true}"));

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.BeginEditorContentUpload,
                new BeginEditorContentUploadRequest(SessionId, "op-1", 4, DigestB)),
            IpcJsonContext.Default.BeginEditorContentUploadRequestEnvelope,
            Payload("beginEditorContentUpload", "request",
                "{\"editorSessionId\":\"" + SessionId + "\",\"saveOperationId\":\"op-1\",\"declaredUtf8Length\":4,\"declaredSha256\":\"" + DigestB + "\"}"));
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.BeginEditorContentUpload,
                new BeginEditorContentUploadResponse("upload-1", EditorTransportLimits.MaximumChunkUtf8Bytes)),
            IpcJsonContext.Default.BeginEditorContentUploadResponseEnvelope,
            Payload("beginEditorContentUpload", "response",
                "{\"uploadId\":\"upload-1\",\"maxChunkBytes\":" + EditorTransportLimits.MaximumChunkUtf8Bytes + "}"));

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.EditorContentUploadChunk,
                new EditorContentUploadChunkRequest("upload-1", 0, 1, "YQ==")),
            IpcJsonContext.Default.EditorContentUploadChunkRequestEnvelope,
            Payload("editorContentUploadChunk", "request",
                "{\"uploadId\":\"upload-1\",\"chunkIndex\":0,\"chunkCount\":1,\"dataBase64\":\"YQ==\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.EditorContentUploadChunk, new EditorContentUploadChunkResponse(0)),
            IpcJsonContext.Default.EditorContentUploadChunkResponseEnvelope,
            Payload("editorContentUploadChunk", "response", "{\"acceptedIndex\":0}"));

        var blob = new IpcBlobRef(DigestB, 4, "blob:" + DigestB);
        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CommitEditorContentUpload, new CommitEditorContentUploadRequest("upload-1")),
            IpcJsonContext.Default.CommitEditorContentUploadRequestEnvelope,
            Payload("commitEditorContentUpload", "request", "{\"uploadId\":\"upload-1\"}"));
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CommitEditorContentUpload, new CommitEditorContentUploadResponse(blob)),
            IpcJsonContext.Default.CommitEditorContentUploadResponseEnvelope,
            Payload("commitEditorContentUpload", "response",
                "{\"blobRef\":{\"digest\":\"" + DigestB + "\",\"size\":4,\"locator\":\"blob:" + DigestB + "\"}}"));

        AssertGolden(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionRequest(SessionId, "op-1", DigestA, blob, HistoryCheckpointTriggerKind.Autosave)),
            IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
            Payload("saveDraftEditorSession", "request",
                "{\"editorSessionId\":\"" + SessionId + "\",\"saveOperationId\":\"op-1\",\"expectedPersistedDigest\":\"" + DigestA +
                "\",\"content\":{\"digest\":\"" + DigestB + "\",\"size\":4,\"locator\":\"blob:" + DigestB + "\"},\"checkpointTrigger\":\"autosave\"}"));
        AssertGolden(
            Program.Envelope(
                IpcMessageType.Response,
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionResponse("op-1", DigestB, 2, false)),
            IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
            Payload("saveDraftEditorSession", "response",
                "{\"saveOperationId\":\"op-1\",\"persistedDigest\":\"" + DigestB + "\",\"persistedRevision\":2,\"idempotentReplay\":false}"));
    }

    private static void ContentDigestIsSha256Hex()
    {
        var hex = ContentDigest.Sha256Hex("hi"u8);
        Program.AssertTrue(ContentDigest.IsSha256Hex(hex), "SHA-256 hex must be 64 hex chars.");
        Program.AssertEqual(
            "8f434346648f6b96df89dda901c5176b10a6d83961dd3c1ac88b59b2dc327aa4",
            hex,
            "Content digest must be lowercase SHA-256.");
        Program.AssertTrue(!ContentDigest.IsSha256Hex("not-a-digest"), "Non-hex digest must be rejected.");
    }

    private static void FrameLimitIsUnchanged()
    {
        Program.AssertEqual(1024 * 1024, IpcProtocol.MaximumFrameBytes, "WP16 must not raise the 1 MiB IPC frame limit.");
        Program.AssertTrue(
            EditorTransportLimits.MaximumChunkUtf8Bytes < IpcProtocol.MaximumFrameBytes,
            "Editor chunks must stay below the IPC frame limit.");
        Program.AssertTrue(
            EditorTransportLimits.MaximumDocumentUtf8Bytes > IpcProtocol.MaximumFrameBytes,
            "The editor document bound is a resource cap, not the 1 MiB frame limit.");
    }

    private static string[] EditorTypes() =>
    [
        IpcSemanticTypes.OpenDraftEditorSession,
        IpcSemanticTypes.GetDraftEditorSessionState,
        IpcSemanticTypes.ReleaseDraftEditorSession,
        IpcSemanticTypes.BeginEditorContentUpload,
        IpcSemanticTypes.EditorContentUploadChunk,
        IpcSemanticTypes.CommitEditorContentUpload,
        IpcSemanticTypes.SaveDraftEditorSession,
        IpcSemanticTypes.RestoreHistoryEntry
    ];

    private static string Payload(string semanticType, string messageType, string payload) =>
        "{\"protocolVersion\":1,\"messageType\":\"" + messageType + "\",\"semanticType\":\"" + semanticType +
        "\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":" +
        payload + "}";

    private static void AssertGolden<T>(IpcEnvelope<T> envelope, JsonTypeInfo<IpcEnvelope<T>> typeInfo, string expected)
    {
        var actual = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, typeInfo));
        Program.AssertEqual(expected, actual, envelope.SemanticType + " golden JSON changed. Actual=" + actual);
        var roundTrip = IpcJson.Deserialize(IpcJson.GetBytes(actual), typeInfo);
        Program.AssertEqual(envelope.SemanticType, roundTrip.SemanticType, envelope.SemanticType + " lost discriminator.");
    }
}
