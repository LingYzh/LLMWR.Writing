using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Application.Security;

namespace LLMW.Writing.Application.Tests;

internal static class Program
{
    private const string ObjectId = "018f3e78-1234-7abc-8def-0123456789a1";
    private static readonly CallerPrincipal UserPrincipal =
        new TrustedNativePrincipalSource("application-tests").ResolveUserInteractive();
    private static readonly CoreAuthorizationService Authorization = new(new TrustedTestSecurityPolicySource());

    private static int Main()
    {
        try
        {
            NoEvidenceUsesLightweightAssessmentWithoutImpactAnalyzer();
            DuplicateNarrativeObjectIsRejectedBeforeWorkingSetPersistence();
            SearchNarrativeRejectsInvalidQueryBeforeStore();
            TrustedPrincipalConstructionBoundariesAreExplicit();
            AuthorizationDenialPrecedesSearchSideEffects();
            MissingSecurityPolicyFailsClosed();
            Console.WriteLine("Application Narrative Change/Registry/Security tests passed (6).");
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
        var service = new NarrativeChangeService(
            new MemoryBlobStore(), store, new NoEvidenceSemanticFake(), impact,
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            Authorization);
        var created = Success(service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [new WorkingNarrativeChangeInput(ObjectId, "character", NarrativeChangeKind.Add, AfterPayload: Payload("A"))],
            UserPrincipal)));
        var applied = Success(service.Apply(new ApplyNarrativeChangeSetCommand(
            created.ChangeSetId, "application-fast-path", NarrativeDecisionKind.AuthorConfirmed, "author-1",
            Principal: UserPrincipal)));

        AssertEqual(0, impact.Calls, "NO_EVIDENCE_FOUND incorrectly invoked the heavy impact analyzer.");
        AssertEqual(NarrativeImpactAnalysisStatus.NoRelevantDependency, store.LastImpactStatus!.Value,
            "The lightweight dependency result was not persisted.");
        AssertEqual(created.ChangeSetId, applied.ChangeSetId, "Apply changed the durable change-set identity.");
    }

    private static void DuplicateNarrativeObjectIsRejectedBeforeWorkingSetPersistence()
    {
        var store = new FakeStore();
        var service = new NarrativeChangeService(
            new MemoryBlobStore(), store, new NoEvidenceSemanticFake(), new ImpactFake(),
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            Authorization);
        var result = service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [
                new WorkingNarrativeChangeInput(ObjectId, "character", NarrativeChangeKind.Add, AfterPayload: Payload("A")),
                new WorkingNarrativeChangeInput(ObjectId, "character", NarrativeChangeKind.Add, AfterPayload: Payload("B"))
            ],
            UserPrincipal));

        AssertEqual(NarrativeChangeError.PartialApplyForbidden, result.Failure?.Code,
            "Duplicate object mutation was not rejected before persistence.");
        AssertEqual(0, store.CreateCalls, "Duplicate object mutation reached the persistence port.");
    }

    private static void SearchNarrativeRejectsInvalidQueryBeforeStore()
    {
        var store = new SearchStoreFake();
        var service = new LLMW.Writing.Application.Registry.SearchNarrativeService(store, Authorization);
        var result = service.Search(new LLMW.Writing.Application.Registry.SearchNarrativeQuery(" ", Principal: UserPrincipal));

        AssertEqual(LLMW.Writing.Application.Registry.RegistryQueryError.SearchQueryInvalid, result.Failure?.Code,
            "An empty Normal Retrieval query was not rejected with a typed result.");
        AssertEqual(0, store.Calls, "An invalid search query reached Infrastructure.");
    }

    private static void TrustedPrincipalConstructionBoundariesAreExplicit()
    {
        AssertEqual(LLMW.Writing.Domain.Security.PrincipalKind.UserInteractive, UserPrincipal.Kind,
            "Trusted Native composition did not create USER_INTERACTIVE.");
        var publicCoreFactory = typeof(CallerPrincipal).GetMethods(System.Reflection.BindingFlags.Public |
                                                                  System.Reflection.BindingFlags.Static)
            .Any(method => method.Name.Contains("CoreInternal", StringComparison.Ordinal));
        AssertEqual(false, publicCoreFactory, "CallerPrincipal exposes a public CORE_INTERNAL factory.");
        AssertEqual(0, typeof(CallerPrincipal).GetConstructors().Length,
            "CallerPrincipal has a public constructor that ordinary callers can use to select a trusted principal.");
        AssertEqual(false, typeof(LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest).GetProperties()
                .Any(property => property.Name.Contains("Role", StringComparison.OrdinalIgnoreCase) ||
                                 property.Name.Contains("Capability", StringComparison.OrdinalIgnoreCase) ||
                                 property.Name.Contains("Principal", StringComparison.OrdinalIgnoreCase)),
            "RunSession request exposes caller-selected role/capability/principal.");
        var commandTypes = new[]
        {
            typeof(LLMW.Writing.Application.ChapterAuthority.SubmitChapterDraftCommand),
            typeof(LLMW.Writing.Application.ChapterAuthority.ReviewChapterCandidateCommand),
            typeof(LLMW.Writing.Application.ChapterAuthority.AcceptChapterCandidateCommand),
            typeof(CreateWorkingNarrativeChangeSetCommand),
            typeof(ApplyNarrativeChangeSetCommand),
            typeof(LLMW.Writing.Application.Registry.SearchNarrativeQuery)
        };
        AssertEqual(false, commandTypes.SelectMany(type => type.GetProperties())
                .Any(property => property.Name.Contains("PermissionMode", StringComparison.OrdinalIgnoreCase) ||
                                 property.Name.Contains("AcceptanceAuthorized", StringComparison.OrdinalIgnoreCase)),
            "A public command can supply permission mode or an authorization boolean.");
    }

    private static void AuthorizationDenialPrecedesSearchSideEffects()
    {
        var store = new SearchStoreFake();
        var service = new LLMW.Writing.Application.Registry.SearchNarrativeService(store, Authorization);
        var result = service.Search(new LLMW.Writing.Application.Registry.SearchNarrativeQuery("authority", Principal: null));

        AssertEqual(LLMW.Writing.Application.Registry.RegistryQueryError.InvalidPrincipal, result.Failure?.Code,
            "Missing principal did not return a typed security error.");
        AssertEqual(0, store.Calls, "Authorization denial occurred after the Registry store side effect.");
    }

    private static void MissingSecurityPolicyFailsClosed()
    {
        var decision = new CoreAuthorizationService().Authorize(
            UserPrincipal,
            new AuthorizationRequest(LLMW.Writing.Domain.Security.Capability.RegistryQuery));

        AssertEqual(LLMW.Writing.Domain.Security.CapabilityDecisionKind.Denied, decision.Decision,
            "The production/default authorization service allowed a missing policy source.");
        AssertEqual(LLMW.Writing.Domain.Security.CapabilityDecisionReason.ProductDenied, decision.Reasons.Single(),
            "Missing policy did not produce a structured fail-closed reason.");
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

    private sealed class SearchStoreFake : LLMW.Writing.Application.Registry.INarrativeSearchStore
    {
        public int Calls { get; private set; }

        public LLMW.Writing.Application.Registry.RegistryQueryResult<
            IReadOnlyList<LLMW.Writing.Application.Registry.NarrativeSearchHit>> Search(
            LLMW.Writing.Application.Registry.SearchNarrativeQuery query,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return LLMW.Writing.Application.Registry.RegistryQueryResults.Success<
                IReadOnlyList<LLMW.Writing.Application.Registry.NarrativeSearchHit>>([]);
        }
    }

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

    private sealed class TrustedTestSecurityPolicySource : ISecurityPolicySource
    {
        public SecurityPolicySnapshot Resolve(CallerPrincipal principal, LLMW.Writing.Domain.Security.Capability capability) =>
            new(
                ProductAllowed: true,
                ToolGranted: true,
                ExtensionGranted: true,
                ProjectTrusted: true,
                LLMW.Writing.Domain.Security.SecurityScopeClassification.InScope,
                LLMW.Writing.Domain.Security.HardDeny.None,
                NarrativeAuthorityAvailable: false,
                ExplicitUserTask: false);
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
