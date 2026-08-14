using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Application.Registry;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.NarrativeChange;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Projection;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private const string Wp07ObjectA = "018f3e78-1234-7abc-8def-0123456789b1";
    private const string Wp07ObjectB = "018f3e78-1234-7abc-8def-0123456789b2";
    private const string Wp07ObjectC = "018f3e78-1234-7abc-8def-0123456789b3";
    private const string Wp07Edge = "018f3e78-1234-7abc-8def-0123456789d7";
    private static readonly List<string> Wp07PassedTests = [];

    private static void RunWp07Tests()
    {
        RunWp07(nameof(ApplyProjectsOnlyCurrentAuthorityAndSearchExcludesRaw), ApplyProjectsOnlyCurrentAuthorityAndSearchExcludesRaw);
        RunWp07(nameof(RemovalCreatesTombstoneAndRebuildIsByteIdentical), RemovalCreatesTombstoneAndRebuildIsByteIdentical);
        RunWp07(nameof(RegistryGateCannotBeBypassedAndExternalBytesAreUntrusted), RegistryGateCannotBeBypassedAndExternalBytesAreUntrusted);
        RunWp07(nameof(TransactionAndFtsFaultsRespectAuthorityBoundary), TransactionAndFtsFaultsRespectAuthorityBoundary);

        Console.WriteLine($"WP07 integration tests passed ({Wp07PassedTests.Count}).");
        foreach (var test in Wp07PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void ApplyProjectsOnlyCurrentAuthorityAndSearchExcludesRaw()
    {
        using var fixture = Wp07Fixture.Create();
        var working = fixture.CreateAdd(Wp07ObjectA, "character", "# Aurora\r\n\r\nCafe\u0301 beaconwpseven\r\n");
        var objectPath = fixture.ObjectProjectionPath("character", Wp07ObjectA);
        AssertWp07False(File.Exists(objectPath), "A working/proposed object was projected before Authority Apply.");

        var applied = Wp07Success(fixture.Apply(working, "wp07-first"));
        AssertWp07Equal(AuthorityTransactionState.Complete, applied.TransactionState, "Projected Apply did not reach COMPLETE.");
        AssertWp07True(File.Exists(objectPath), "Current Narrative Object projection is missing.");
        AssertWp07True(File.Exists(Path.Combine(fixture.Directory, "Narrative", "state", "narrative-state.json")), "Narrative State view is missing.");
        AssertWp07True(File.Exists(Path.Combine(fixture.Directory, "Narrative", "state", "dependencies.json")), "Dependency view is missing.");
        AssertWp07True(File.Exists(Path.Combine(fixture.Directory, "Narrative", "state", "registry.json")), "Registry view is missing.");

        var bytes = File.ReadAllBytes(objectPath);
        AssertWp07False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "Projection contains a UTF-8 BOM.");
        AssertWp07False(bytes.Contains((byte)'\r'), "Projection contains a CR line ending.");
        AssertWp07True(Encoding.UTF8.GetString(bytes).Contains("Café beaconwpseven", StringComparison.Ordinal), "Projection was not NFC-normalized.");
        fixture.AssertRegistryTrusted(Wp07ObjectA, objectPath);
        AssertUuidV7(fixture.Scalar<string>("SELECT path_id FROM object_paths WHERE object_id=$id;", ("$id", Wp07ObjectA)), "WP07 path ID");
        AssertUuidV7(fixture.Scalar<string>("SELECT registry_entry_id FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA)), "WP07 Registry entry ID");

        var firstSearch = Wp07SearchSuccess(fixture.Search("beaconwpseven"));
        var secondSearch = Wp07SearchSuccess(fixture.Search("beaconwpseven"));
        AssertWp07Equal(1, firstSearch.Count, "Current trusted Narrative Object was not retrievable.");
        AssertWp07Equal(firstSearch[0].SectionKey, secondSearch[0].SectionKey, "Search section key was not stable.");
        AssertWp07Equal(fixture.CurrentDigest(Wp07ObjectA), firstSearch[0].ArtifactDigest, "Search result does not identify its Authority artifact.");

        var uncommitted = fixture.CreateAdd(Wp07ObjectC, "character", "workingonlywpseven");
        AssertWp07False(File.Exists(fixture.ObjectProjectionPath("character", Wp07ObjectC)), "Working-only object leaked into projection.");
        AssertWp07Equal(0, Wp07SearchSuccess(fixture.Search("workingonlywpseven")).Count, "Working-only content leaked into retrieval.");
        _ = uncommitted;

        var raw = fixture.CreateAdd(Wp07ObjectB, "raw", "rawonlywpseven");
        Wp07Success(fixture.Apply(raw, "wp07-raw"));
        AssertWp07Equal(0, Wp07SearchSuccess(fixture.Search("rawonlywpseven")).Count, "Raw object bypassed the retrieval exclusion.");
        AssertWp07Equal(SqliteNarrativeSearchIndex.BaselineTokenizerProfile, "unicode61", "Tokenizer baseline changed.");
    }

    private static void RemovalCreatesTombstoneAndRebuildIsByteIdentical()
    {
        using var fixture = Wp07Fixture.Create();
        Wp07Success(fixture.Apply(fixture.CreateAdd(Wp07ObjectA, "character", "tombstonesecretwpseven"), "wp07-tombstone-add"));
        Wp07Success(fixture.Apply(fixture.CreateAdd(Wp07ObjectB, "location", "harborwpseven"), "wp07-rebuild-add"));
        fixture.Execute(
            "INSERT INTO dependency_edges(edge_id,from_object_id,to_object_id,edge_type,validation_status,created_at_ms,updated_at_ms) VALUES($edge,$from,$to,'canon_reference','valid',1,1);",
            ("$edge", Wp07Edge), ("$from", Wp07ObjectA), ("$to", Wp07ObjectB));
        Wp07ProjectionSuccess(fixture.Rebuilder.Rebuild());

        var state = fixture.CurrentState(Wp07ObjectA);
        var remove = fixture.CreateChange(
            Wp07ObjectA,
            "character",
            NarrativeChangeKind.Remove,
            state.StateRevisionId,
            state.Digest,
            payload: null);
        Wp07Success(fixture.Apply(remove, "wp07-remove"));

        var tombstonePath = fixture.ObjectProjectionPath("character", Wp07ObjectA);
        var tombstone = File.ReadAllText(tombstonePath, Encoding.UTF8);
        AssertWp07True(tombstone.Contains("status: \"removed\"", StringComparison.Ordinal), "Removed object projection is not an explicit tombstone.");
        AssertWp07False(tombstone.Contains("tombstonesecretwpseven", StringComparison.Ordinal), "Removed projection retained former Authority body content.");
        AssertWp07True(tombstone.EndsWith("extensions: {}\n---\n", StringComparison.Ordinal), "Tombstone is not metadata-only.");
        AssertWp07Equal("removed", fixture.Scalar<string>("SELECT status FROM objects WHERE object_id=$id;", ("$id", Wp07ObjectA)), "Authority removal state changed.");
        AssertWp07Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id=$id;", ("$id", Wp07ObjectA)), "Removal deleted Authority history.");
        AssertWp07Equal(0, Wp07SearchSuccess(fixture.Search("tombstonesecretwpseven")).Count, "Removed content remained retrievable.");
        AssertWp07True(File.ReadAllText(Path.Combine(fixture.Directory, "Narrative", "state", "dependencies.json"), Encoding.UTF8).Contains(Wp07Edge, StringComparison.Ordinal), "Dependency projection omitted the Authority edge.");

        var expectedFiles = fixture.ProjectionDigests();
        var authorityBefore = fixture.AuthoritySignature();
        fixture.DeleteProjectionTree();
        var first = Wp07ProjectionSuccess(fixture.Rebuilder.Rebuild());
        AssertWp07Equal(5, first.Artifacts.Count, "Rebuild returned an unexpected artifact set.");
        AssertWp07Dictionary(expectedFiles, fixture.ProjectionDigests(), "First rebuild was not byte-identical.");
        Wp07ProjectionSuccess(fixture.Rebuilder.Rebuild());
        AssertWp07Dictionary(expectedFiles, fixture.ProjectionDigests(), "Second rebuild was not idempotent.");
        AssertWp07Equal(authorityBefore, fixture.AuthoritySignature(), "Derived rebuild mutated Authority tables.");
    }

    private static void RegistryGateCannotBeBypassedAndExternalBytesAreUntrusted()
    {
        using var fixture = Wp07Fixture.Create();
        Wp07Success(fixture.Apply(fixture.CreateAdd(Wp07ObjectA, "character", "trustedgatewpseven"), "wp07-gate"));
        AssertWp07Equal(1, Wp07SearchSuccess(fixture.Search("trustedgatewpseven")).Count, "Baseline Registry retrieval failed.");

        var originalPhysical = fixture.Scalar<string>("SELECT trusted_physical_digest FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA));
        var originalSemantic = fixture.Scalar<string>("SELECT trusted_semantic_digest FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA));
        var cases = new (string Column, object? Value)[]
        {
            ("registration_state", "unregistered"),
            ("registration_state", "ignored"),
            ("registration_state", "missing"),
            ("retrieval_availability", "unavailable"),
            ("retrieval_availability", "stale"),
            ("reconcile_state", "dirty"),
            ("reconcile_state", "pending_confirm"),
            ("reconcile_state", "reconciling"),
            ("reconcile_state", "needs_attention"),
            ("trusted_physical_digest", null),
            ("trusted_semantic_digest", null)
        };
        foreach (var testCase in cases)
        {
            fixture.Execute($"UPDATE registry_entries SET {testCase.Column}=$value WHERE object_id=$id;", ("$value", testCase.Value), ("$id", Wp07ObjectA));
            AssertWp07Equal(0, Wp07SearchSuccess(fixture.Search("trustedgatewpseven")).Count, $"Registry gate admitted {testCase.Column}={testCase.Value ?? "NULL"}.");
            fixture.Execute(
                "UPDATE registry_entries SET registration_state='registered',retrieval_availability='available',reconcile_state='clean',trusted_physical_digest=$physical,trusted_semantic_digest=$semantic WHERE object_id=$id;",
                ("$physical", originalPhysical), ("$semantic", originalSemantic), ("$id", Wp07ObjectA));
        }

        fixture.Execute(
            "INSERT INTO search_documents(search_rowid,object_id,artifact_digest,section_key,title,body,current_status) VALUES(9001,$id,$digest,'injected','Injected','ftsinjectedwpseven','current');",
            ("$id", Wp07ObjectC), ("$digest", new string('a', 64)));
        fixture.Execute("INSERT INTO search_fts(search_fts) VALUES('rebuild');");
        AssertWp07Equal(0, Wp07SearchSuccess(fixture.Search("ftsinjectedwpseven")).Count, "Direct FTS injection bypassed the Registry/object_paths JOIN gate.");

        var authorityBefore = fixture.AuthoritySignature();
        var path = fixture.ObjectProjectionPath("character", Wp07ObjectA);
        File.AppendAllText(path, "externaluntrustedwpseven\n", new UTF8Encoding(false));
        AssertWp07Equal(0, Wp07SearchSuccess(fixture.Search("externaluntrustedwpseven")).Count, "External projection bytes became search truth.");
        AssertWp07Equal(1, Wp07SearchSuccess(fixture.Search("trustedgatewpseven")).Count, "External projection bytes replaced trusted Authority search truth.");
        AssertWp07Equal(originalPhysical, fixture.Scalar<string>("SELECT trusted_physical_digest FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA)), "External bytes changed the trusted physical baseline.");
        AssertWp07Equal(authorityBefore, fixture.AuthoritySignature(), "External projection bytes mutated Authority.");
    }

    private static void TransactionAndFtsFaultsRespectAuthorityBoundary()
    {
        var preCommit = new Wp07TransactionFault(AuthorityTransactionFaultPoint.BeforeSqliteCommit);
        using (var fixture = Wp07Fixture.Create(transactionFault: preCommit))
        {
            var working = fixture.CreateAdd(Wp07ObjectA, "character", "precommitwpseven");
            Wp07Failure(NarrativeChangeError.InfrastructureFailure, fixture.Apply(working, "wp07-precommit").Failure);
            AssertWp07Equal("proposed", fixture.Scalar<string>("SELECT status FROM objects WHERE object_id=$id;", ("$id", Wp07ObjectA)), "Pre-COMMIT fault mutated Authority current state.");
            AssertWp07Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM registry_entries;"), "Pre-COMMIT fault finalized Registry trust.");
            AssertWp07False(File.Exists(fixture.ObjectProjectionPath("character", Wp07ObjectA)), "Pre-COMMIT fault materialized projection bytes.");
        }

        var postCommit = new Wp07TransactionFault(AuthorityTransactionFaultPoint.AfterSqliteCommit);
        using (var fixture = Wp07Fixture.Create(transactionFault: postCommit))
        {
            var working = fixture.CreateAdd(Wp07ObjectA, "character", "postcommitwpseven");
            Wp07Failure(NarrativeChangeError.AuthorityDirty, fixture.Apply(working, "wp07-postcommit").Failure);
            AssertWp07Equal("current", fixture.Scalar<string>("SELECT status FROM objects WHERE object_id=$id;", ("$id", Wp07ObjectA)), "Post-COMMIT fault rolled back committed Authority.");
            AssertWp07Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM registry_entries;"), "Dirty projection was marked trusted before verification.");
            postCommit.Enabled = false;
            var recovered = Wp07Success(fixture.Apply(working, "wp07-postcommit"));
            AssertWp07Equal(AuthorityTransactionState.Complete, recovered.TransactionState, "Roll-forward recovery did not reach COMPLETE.");
            fixture.AssertRegistryTrusted(Wp07ObjectA, fixture.ObjectProjectionPath("character", Wp07ObjectA));
            AssertWp07Equal(1, Wp07SearchSuccess(fixture.Search("postcommitwpseven")).Count, "Recovered projection was not retrievable.");
        }

        var searchFault = new Wp07SearchFault(SearchIndexFaultPoint.BeforeFtsRebuild);
        using (var fixture = Wp07Fixture.Create(searchFault: searchFault))
        {
            var applied = Wp07Success(fixture.Apply(fixture.CreateAdd(Wp07ObjectA, "character", "ftsrepairwpseven"), "wp07-fts-fault"));
            AssertWp07Equal(AuthorityTransactionState.Complete, applied.TransactionState, "FTS failure incorrectly dirtied the Authority projection transaction.");
            AssertWp07True(fixture.ProjectionMaterializer.LastSearchIndexFailure is not null, "FTS failure was not reported by the derived index boundary.");
            Wp07RegistryFailure(RegistryQueryError.SearchIndexDirty, fixture.Search("ftsrepairwpseven").Failure);
            var authorityBefore = fixture.AuthoritySignature();
            searchFault.Enabled = false;
            fixture.SearchIndex.Rebuild();
            AssertWp07Equal(1, Wp07SearchSuccess(fixture.Search("ftsrepairwpseven")).Count, "FTS rebuild did not restore retrieval.");
            AssertWp07Equal(authorityBefore, fixture.AuthoritySignature(), "FTS rebuild mutated Authority tables.");
        }
    }

    private static void RunWp07(string name, Action test)
    {
        test();
        Wp07PassedTests.Add(name);
    }

    private static T Wp07Success<T>(NarrativeChangeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected WP07 success, received {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static ProjectionBuild Wp07ProjectionSuccess(ProjectionResult<ProjectionBuild> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected projection success, received {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static IReadOnlyList<NarrativeSearchHit> Wp07SearchSuccess(RegistryQueryResult<IReadOnlyList<NarrativeSearchHit>> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected search success, received {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static void Wp07Failure(NarrativeChangeError expected, NarrativeChangeFailure? failure)
    {
        AssertWp07True(failure is not null, $"Expected Narrative failure {expected}, but the operation succeeded.");
        AssertWp07Equal(expected, failure!.Code, "WP07 Narrative failure code changed.");
    }

    private static void Wp07RegistryFailure(RegistryQueryError expected, RegistryQueryFailure? failure)
    {
        AssertWp07True(failure is not null, $"Expected Registry failure {expected}, but the query succeeded.");
        AssertWp07Equal(expected, failure!.Code, "WP07 Registry failure code changed.");
    }

    private static void AssertWp07Dictionary(
        Dictionary<string, string> expected,
        Dictionary<string, string> actual,
        string message)
    {
        AssertWp07Equal(expected.Count, actual.Count, message);
        foreach (var pair in expected)
        {
            AssertWp07True(actual.TryGetValue(pair.Key, out var digest) && StringComparer.Ordinal.Equals(pair.Value, digest), $"{message} Path: {pair.Key}");
        }
    }

    private static void AssertWp07Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertWp07True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertWp07False(bool condition, string message) => AssertWp07True(!condition, message);

    private sealed class Wp07Fixture : IDisposable
    {
        private readonly SqliteDatabaseConnectionFactory connectionFactory = new();

        private Wp07Fixture(
            string directory,
            string databasePath,
            ImmutableBlobStore blobStore,
            AuthorityTransactionCoordinator coordinator,
            ProjectionAuthorityMaterializer projectionMaterializer,
            SqliteNarrativeSearchIndex searchIndex,
            NarrativeChangeService service,
            SearchNarrativeService search,
            ProjectionRebuilder rebuilder)
        {
            Directory = directory;
            DatabasePath = databasePath;
            BlobStore = blobStore;
            Coordinator = coordinator;
            ProjectionMaterializer = projectionMaterializer;
            SearchIndex = searchIndex;
            Service = service;
            SearchService = search;
            Rebuilder = rebuilder;
        }

        public string Directory { get; }
        public string DatabasePath { get; }
        public ImmutableBlobStore BlobStore { get; }
        public AuthorityTransactionCoordinator Coordinator { get; }
        public ProjectionAuthorityMaterializer ProjectionMaterializer { get; }
        public SqliteNarrativeSearchIndex SearchIndex { get; }
        public NarrativeChangeService Service { get; }
        public SearchNarrativeService SearchService { get; }
        public ProjectionRebuilder Rebuilder { get; }

        public static Wp07Fixture Create(Wp07TransactionFault? transactionFault = null, Wp07SearchFault? searchFault = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP07", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp07-tests");
            var blobs = new ImmutableBlobStore(directory);
            var searchIndex = new SqliteNarrativeSearchIndex(databasePath, blobs, faultInjector: searchFault);
            var projectionMaterializer = new ProjectionAuthorityMaterializer(
                databasePath,
                blobs,
                new AtomicAuthorityMaterializer(directory, blobs),
                searchIndex);
            var coordinator = new AuthorityTransactionCoordinator(
                databasePath,
                blobs,
                projectionMaterializer,
                faultInjector: transactionFault);
            var store = new SqliteNarrativeChangeStore(databasePath, blobs, coordinator, enableProjection: true);
            var service = new NarrativeChangeService(blobs, store, new SemanticFake(), new ImpactFake());
            var search = new SearchNarrativeService(new SqliteNarrativeSearchStore(databasePath));
            var rebuilder = new ProjectionRebuilder(databasePath, blobs, projectionMaterializer);
            return new Wp07Fixture(directory, databasePath, blobs, coordinator, projectionMaterializer, searchIndex, service, search, rebuilder);
        }

        public string CreateAdd(string objectId, string objectType, string payload) =>
            CreateChange(objectId, objectType, NarrativeChangeKind.Add, null, null, payload);

        public string CreateChange(
            string objectId,
            string objectType,
            NarrativeChangeKind kind,
            string? beforeRevision,
            string? beforeDigest,
            string? payload)
        {
            var input = new WorkingNarrativeChangeInput(
                objectId,
                objectType,
                kind,
                beforeRevision,
                beforeDigest,
                payload is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(payload)));
            var created = Wp07Success(Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
                "storyline", "wp07-storyline", "author", "wp07-author", [input])));
            return created.ChangeSetId;
        }

        public NarrativeChangeResult<ApplyNarrativeChangeSetResult> Apply(string changeSetId, string idempotencyKey) =>
            Service.Apply(new ApplyNarrativeChangeSetCommand(
                changeSetId,
                idempotencyKey,
                NarrativeDecisionKind.AuthorConfirmed,
                "wp07-author"));

        public RegistryQueryResult<IReadOnlyList<NarrativeSearchHit>> Search(string text) =>
            SearchService.Search(new SearchNarrativeQuery(text));

        public string ObjectProjectionPath(string objectType, string objectId) =>
            Path.Combine(Directory, "Narrative", "objects", $"{objectType}-{objectId}.md");

        public (string StateRevisionId, string Digest) CurrentState(string objectId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT state_revision_id,snapshot_digest FROM narrative_state_revisions state WHERE scope_object_id=$id AND NOT EXISTS(SELECT 1 FROM narrative_state_revisions successor WHERE successor.supersedes_state_revision_id=state.state_revision_id) ORDER BY created_at_ms DESC,state_revision_id DESC LIMIT 1;";
            Add(command, "$id", objectId);
            using var reader = command.ExecuteReader();
            AssertWp07True(reader.Read(), $"No current state exists for {objectId}.");
            return (reader.GetString(0), reader.GetString(1));
        }

        public string CurrentDigest(string objectId) => CurrentState(objectId).Digest;

        public void AssertRegistryTrusted(string objectId, string fullPath)
        {
            var physical = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
            AssertWp07Equal("registered", Scalar<string>("SELECT registration_state FROM registry_entries WHERE object_id=$id;", ("$id", objectId)), "Registry entry is not registered.");
            AssertWp07Equal("available", Scalar<string>("SELECT retrieval_availability FROM registry_entries WHERE object_id=$id;", ("$id", objectId)), "Registry entry is not available.");
            AssertWp07Equal("clean", Scalar<string>("SELECT reconcile_state FROM registry_entries WHERE object_id=$id;", ("$id", objectId)), "Registry entry is not clean.");
            AssertWp07Equal(physical, Scalar<string>("SELECT trusted_physical_digest FROM registry_entries WHERE object_id=$id;", ("$id", objectId)), "Trusted physical digest does not match verified bytes.");
            AssertWp07Equal(physical, Scalar<string>("SELECT physical_digest FROM object_paths WHERE object_id=$id;", ("$id", objectId)), "object_paths physical digest does not match verified bytes.");
            AssertWp07True(!string.IsNullOrWhiteSpace(Scalar<string>("SELECT trusted_semantic_digest FROM registry_entries WHERE object_id=$id;", ("$id", objectId))), "Trusted semantic digest is missing.");
        }

        public Dictionary<string, string> ProjectionDigests()
        {
            var root = Path.Combine(Directory, "Narrative");
            return System.IO.Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToDictionary(
                    value => Path.GetRelativePath(Directory, value).Replace('\\', '/'),
                    value => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(value))).ToLowerInvariant(),
                    StringComparer.Ordinal);
        }

        public string AuthoritySignature() => string.Join("|", new[]
        {
            Scalar<string>("SELECT COALESCE(json_group_array(json_array(object_id,object_type,schema_version,revision_no,status,deleted_at_ms)), '[]') FROM (SELECT * FROM objects ORDER BY object_id);"),
            Scalar<string>("SELECT COALESCE(json_group_array(json_array(state_revision_id,scope_object_id,transaction_id,snapshot_digest,supersedes_state_revision_id,created_at_ms)), '[]') FROM (SELECT * FROM narrative_state_revisions ORDER BY state_revision_id);"),
            Scalar<string>("SELECT COALESCE(json_group_array(json_array(edge_id,from_object_id,to_object_id,edge_type,validation_status)), '[]') FROM (SELECT * FROM dependency_edges ORDER BY edge_id);"),
            Scalar<string>("SELECT COALESCE(json_group_array(json_array(event_id,transaction_id,event_seq,event_type,event_payload_json)), '[]') FROM (SELECT * FROM authority_events ORDER BY transaction_id,event_seq);")
        });

        public void DeleteProjectionTree()
        {
            var target = Path.GetFullPath(Path.Combine(Directory, "Narrative"));
            var root = Path.GetFullPath(Directory) + Path.DirectorySeparatorChar;
            AssertWp07True(target.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Projection delete target escaped the fixture root.");
            System.IO.Directory.Delete(target, recursive: true);
        }

        public T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
            var value = command.ExecuteScalar();
            return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private DbConnection Open() => connectionFactory.OpenConfigured(DatabasePath);

        private static void Add(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class Wp07TransactionFault(AuthorityTransactionFaultPoint target) : ITransactionFaultInjector
    {
        public bool Enabled { get; set; } = true;

        public void Inject(AuthorityTransactionFaultPoint point)
        {
            if (Enabled && point == target)
            {
                throw new IOException($"Injected WP07 transaction fault at {point}.");
            }
        }
    }

    private sealed class Wp07SearchFault(SearchIndexFaultPoint target) : ISearchIndexFaultInjector
    {
        public bool Enabled { get; set; } = true;

        public void Inject(SearchIndexFaultPoint point)
        {
            if (Enabled && point == target)
            {
                throw new IOException($"Injected WP07 search fault at {point}.");
            }
        }
    }
}
