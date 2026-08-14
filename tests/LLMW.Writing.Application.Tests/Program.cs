using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Domain.Narrative;

namespace LLMW.Writing.Application.Tests;

internal static class Program
{
    private const string ObjectId = "018f3e78-1234-7abc-8def-0123456789a1";

    private static int Main()
    {
        try
        {
            NoEvidenceUsesLightweightAssessmentWithoutImpactAnalyzer();
            DuplicateNarrativeObjectIsRejectedBeforeWorkingSetPersistence();
            Console.WriteLine("Application Narrative Change tests passed (2).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void NoEvidenceUsesLightweightAssessmentWithoutImpactAnalyzer()
    {
        var store = new FakeStore();
        var impact = new ImpactFake();
        var service = new NarrativeChangeService(new MemoryBlobStore(), store, new NoEvidenceSemanticFake(), impact);
        var created = Success(service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [new WorkingNarrativeChangeInput(ObjectId, "character", NarrativeChangeKind.Add, AfterPayload: Payload("A"))])));
        var applied = Success(service.Apply(new ApplyNarrativeChangeSetCommand(
            created.ChangeSetId, "application-fast-path", NarrativeDecisionKind.AuthorConfirmed, "author-1")));

        AssertEqual(0, impact.Calls, "NO_EVIDENCE_FOUND incorrectly invoked the heavy impact analyzer.");
        AssertEqual(NarrativeImpactAnalysisStatus.NoRelevantDependency, store.LastImpactStatus!.Value,
            "The lightweight dependency result was not persisted.");
        AssertEqual(created.ChangeSetId, applied.ChangeSetId, "Apply changed the durable change-set identity.");
    }

    private static void DuplicateNarrativeObjectIsRejectedBeforeWorkingSetPersistence()
    {
        var store = new FakeStore();
        var service = new NarrativeChangeService(new MemoryBlobStore(), store, new NoEvidenceSemanticFake(), new ImpactFake());
        var result = service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [
                new WorkingNarrativeChangeInput(ObjectId, "character", NarrativeChangeKind.Add, AfterPayload: Payload("A")),
                new WorkingNarrativeChangeInput(ObjectId, "character", NarrativeChangeKind.Add, AfterPayload: Payload("B"))
            ]));

        AssertEqual(NarrativeChangeError.PartialApplyForbidden, result.Failure?.Code,
            "Duplicate object mutation was not rejected before persistence.");
        AssertEqual(0, store.CreateCalls, "Duplicate object mutation reached the persistence port.");
    }

    private static T Success<T>(NarrativeChangeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static MemoryStream Payload(string content) => new(Encoding.UTF8.GetBytes(content));

    private static void AssertEqual<T>(T expected, T? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual!))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class NoEvidenceSemanticFake : ISemanticDependencyAssessor
    {
        public SemanticDependencyAssessment Assess(NarrativeChangeSetSnapshot changeSet, CancellationToken cancellationToken = default) =>
            new(SemanticDependencyFinding.NoEvidenceFound, "{\"evidence\":\"none\"}");
    }

    private sealed class ImpactFake : INarrativeImpactAnalyzer
    {
        public int Calls { get; private set; }

        public NarrativeImpactAnalysisResult Analyze(
            NarrativeChangeSetSnapshot changeSet,
            StructuralDependencyAssessment structuralAssessment,
            SemanticDependencyAssessment semanticAssessment,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return new NarrativeImpactAnalysisResult(NarrativeImpactAnalysisStatus.Affected, [], [], "{}", []);
        }
    }

    private sealed class MemoryBlobStore : IImmutableBlobStore
    {
        private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

        public BlobStageResult Stage(Stream source, string? expectedDigest = null, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bytes = memory.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (expectedDigest is not null && !StringComparer.Ordinal.Equals(expectedDigest, digest))
            {
                throw new InvalidOperationException("Digest mismatch.");
            }

            blobs[digest] = bytes;
            return new BlobStageResult(digest, digest, bytes.Length, Deduplicated: false);
        }

        public Stream OpenRead(string digest) => new MemoryStream(blobs[digest], writable: false);

        public bool Verify(string digest, CancellationToken cancellationToken = default) => blobs.ContainsKey(digest);
    }

    private sealed class FakeStore : INarrativeChangeStore
    {
        private const string ChangeSetId = "018f3e78-1234-7abc-8def-0123456789b1";
        private const string ImpactId = "018f3e78-1234-7abc-8def-0123456789b2";

        private NarrativeChangeSetSnapshot? snapshot;

        public int CreateCalls { get; private set; }

        public NarrativeImpactAnalysisStatus? LastImpactStatus { get; private set; }

        public NarrativeStoreResult<NarrativeChangeSetSnapshot> CreateWorkingChangeSet(PersistWorkingChangeSetRequest request)
        {
            CreateCalls++;
            snapshot = new NarrativeChangeSetSnapshot(
                ChangeSetId,
                request.ScopeKind,
                request.ScopeId,
                "working",
                request.ProposerKind,
                request.ProposerId,
                null,
                null,
                null,
                null,
                request.Changes.Select(change => new NarrativeChangeRecord(
                    "018f3e78-1234-7abc-8def-0123456789b3",
                    change.ObjectId,
                    change.ChangeKind,
                    change.BeforeRevisionRef,
                    change.BeforeDigest,
                    change.AfterPayloadDigest,
                    change.Ordinal)).ToArray());
            return NarrativeStoreResults.Success(snapshot);
        }

        public NarrativeChangeSetSnapshot? LoadChangeSet(string changeSetId) => snapshot;

        public NarrativeChangeFailure? ValidateApplyPreconditions(
            NarrativeChangeSetSnapshot changeSet,
            CancellationToken cancellationToken = default) => null;

        public StructuralDependencyAssessment AssessStructuralDependencies(NarrativeChangeSetSnapshot changeSet) => new([]);

        public NarrativeStoreResult<NarrativeImpactAnalysisRecord> PersistImpactAnalysis(PersistImpactAnalysisRequest request)
        {
            LastImpactStatus = request.Status;
            return NarrativeStoreResults.Success(new NarrativeImpactAnalysisRecord(
                ImpactId,
                request.Status,
                request.AffectedSetJson,
                request.EvidenceJson,
                request.WarningsJson,
                request.Warnings));
        }

        public NarrativeStoreResult<NarrativeApplyStoreResult> Apply(NarrativeApplyStoreRequest request, CancellationToken cancellationToken = default) =>
            NarrativeStoreResults.Success(new NarrativeApplyStoreResult(
                request.ChangeSetId,
                "018f3e78-1234-7abc-8def-0123456789b4",
                request.ImpactAnalysisId ?? ImpactId,
                AuthorityTransactionState.Complete,
                Existing: false));
    }
}
