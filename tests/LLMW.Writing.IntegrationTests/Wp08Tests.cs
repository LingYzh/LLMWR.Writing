using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Domain.Registry;
using LLMW.Writing.Infrastructure.ChapterAuthority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.NarrativeChange;
using LLMW.Writing.Infrastructure.Reconcile;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly List<string> Wp08PassedTests = [];

    private static void RunWp08Tests()
    {
        RunWp08(nameof(StartupScanQuarantinesExternalAndUnregisteredBytes), StartupScanQuarantinesExternalAndUnregisteredBytes);
        RunWp08(nameof(ConfirmedNarrativeModifyUsesWp06AuthorityCommit), ConfirmedNarrativeModifyUsesWp06AuthorityCommit);
        RunWp08(nameof(MissingRenameAndMachineProjectionRequireExplicitResolution), MissingRenameAndMachineProjectionRequireExplicitResolution);
        RunWp08(nameof(MonitorHandlesPollingOverflowBatchStormAndCancellation), MonitorHandlesPollingOverflowBatchStormAndCancellation);
        RunWp08(nameof(FreshAuthorityGateClosesDebounceRaceAndIgnoresDrafts), FreshAuthorityGateClosesDebounceRaceAndIgnoresDrafts);
        RunWp08(nameof(CurrentManuscriptExternalEditBlocksChapterSubmissionAndRestores), CurrentManuscriptExternalEditBlocksChapterSubmissionAndRestores);
        RunWp08(nameof(NativeWatcherIsolatesInternalStateAndUsesConfiguredDebounce), NativeWatcherIsolatesInternalStateAndUsesConfiguredDebounce);
        RunWp08(nameof(MissingWatchSurfacesUsePollingAndReattach), MissingWatchSurfacesUsePollingAndReattach);
        RunWp08(nameof(FullScanSkipsOutsideReparseTargetsAndLoops), FullScanSkipsOutsideReparseTargetsAndLoops);
        RunWp08(nameof(NativeEventTokenReachesPrimarySuppression), NativeEventTokenReachesPrimarySuppression);

        Console.WriteLine($"WP08 integration tests passed ({Wp08PassedTests.Count}).");
        foreach (var test in Wp08PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void StartupScanQuarantinesExternalAndUnregisteredBytes()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "character", "trustedwp08"), "wp08-startup-add"));
        var path = environment.Fixture.ObjectProjectionPath("character", Wp07ObjectA);
        var trusted = environment.Fixture.Scalar<string>(
            "SELECT trusted_physical_digest FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA));
        File.WriteAllText(path, File.ReadAllText(path, Encoding.UTF8).Replace("trustedwp08", "externalwp08", StringComparison.Ordinal), new UTF8Encoding(false));
        var unregistered = Path.Combine(environment.Fixture.Directory, "Narrative", "notes", "external.md");
        Directory.CreateDirectory(Path.GetDirectoryName(unregistered)!);
        File.WriteAllText(unregistered, "unregisteredwp08", new UTF8Encoding(false));

        var report = environment.Monitor.StartupScan();
        var modified = Wp08Observation(report, path, environment.Fixture.Directory);
        Wp08Equal(ReconcileClassification.RegisteredModified, modified.Classification,
            "Offline external edit was not quarantined at startup.");
        Wp08Equal(RegistryRetrievalAvailability.Stale, modified.RetrievalAvailability,
            "Externally edited registered bytes remained normally available.");
        Wp08Equal(trusted, environment.Fixture.Scalar<string>(
            "SELECT trusted_physical_digest FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA)),
            "Watcher evidence overwrote the trusted physical baseline.");
        Wp08Equal(0, Wp07SearchSuccess(environment.Fixture.Search("externalwp08")).Count,
            "External bytes entered Normal Retrieval.");
        var inspection = Wp08Success(environment.Reconcile.Analyze(Wp08Relative(environment.Fixture.Directory, path)));
        Wp08True(inspection.CurrentAuthorityBody!.Contains("trustedwp08", StringComparison.Ordinal),
            "Analyze did not retain the current Authority evidence.");
        Wp08True(inspection.ObservedBody!.Contains("externalwp08", StringComparison.Ordinal),
            "Analyze did not expose observed external evidence.");
        var inspected = Wp08Success(environment.Reconcile.Inspect(Wp08Relative(environment.Fixture.Directory, path)));
        Wp08Equal(inspection.Observation.TrustedPhysicalDigest, inspected.Observation.TrustedPhysicalDigest,
            "Inspect did not preserve Analyze evidence.");
        Wp08Equal(ReconcileClassification.UnregisteredNew,
            Wp08Observation(report, unregistered, environment.Fixture.Directory).Classification,
            "Unregistered Narrative file was not surfaced for confirmation.");
        Wp08Equal(0, Wp07SearchSuccess(environment.Fixture.Search("unregisteredwp08")).Count,
            "Unregistered file entered Normal Retrieval.");
        var ignored = Wp08Success(environment.Reconcile.Ignore(Wp08Relative(environment.Fixture.Directory, unregistered)));
        Wp08Equal(ReconcileClassification.Ignored,
            Wp08Observation(ignored, unregistered, environment.Fixture.Directory).Classification,
            "Explicit Ignore did not remain restricted to runtime-only unregistered state.");
        var cannotIgnoreRegistered = environment.Reconcile.Ignore(Wp08Relative(environment.Fixture.Directory, path));
        Wp08Equal(ReconcileError.ReconcileNotSupported, cannotIgnoreRegistered.Failure!.Code,
            "Ignore bypassed a registered digest mismatch.");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Wp08Equal(ReconcileClassification.FileTemporarilyUnavailable,
                Wp08Observation(environment.Engine.Scan(FileEventSource.FullRescan), path, environment.Fixture.Directory).Classification,
                "Transient registered-file read failure was not surfaced as unavailable.");
        }
    }

    private static void ConfirmedNarrativeModifyUsesWp06AuthorityCommit()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "character", "beforeconfirmwp08"), "wp08-confirm-add"));
        var path = environment.Fixture.ObjectProjectionPath("character", Wp07ObjectA);
        var revisionBefore = environment.Fixture.CurrentState(Wp07ObjectA).StateRevisionId;
        File.WriteAllText(path, File.ReadAllText(path, Encoding.UTF8).Replace("beforeconfirmwp08", "afterconfirmwp08", StringComparison.Ordinal), new UTF8Encoding(false));

        var racingGate = new MutatingAuthorityGate(
            new SqliteAuthoritySurfaceHealthGate(environment.Engine),
            () => File.AppendAllText(path, "raced", new UTF8Encoding(false)));
        var racingStore = new SqliteNarrativeChangeStore(
            environment.Fixture.DatabasePath,
            environment.Fixture.BlobStore,
            environment.Fixture.Coordinator,
            enableProjection: true);
        var racingService = new NarrativeChangeService(
            environment.Fixture.BlobStore, racingStore, new SemanticFake(), new ImpactFake(), racingGate);
        var racingReconcile = new NarrativeReconcileService(
            environment.Fixture.DatabasePath,
            environment.Engine,
            environment.Fixture.BlobStore,
            environment.Fixture.Rebuilder,
            racingService,
            new AtomicAuthorityMaterializer(environment.Fixture.Directory, environment.Fixture.BlobStore, environment.Tracker));
        var raced = racingReconcile.ConfirmNarrativeModify(
            Wp08Relative(environment.Fixture.Directory, path), "wp08-confirm-raced", "wp08-author");
        Wp08Equal(ReconcileError.AuthoritySurfaceDirty, raced.Failure!.Code,
            "Confirmed reconcile did not reject bytes changed between its two fresh health checks.");
        Wp08Equal(revisionBefore, environment.Fixture.CurrentState(Wp07ObjectA).StateRevisionId,
            "TOCTOU rejection partially changed Narrative Authority.");
        File.WriteAllText(path,
            File.ReadAllText(path, Encoding.UTF8).Replace("raced", string.Empty, StringComparison.Ordinal),
            new UTF8Encoding(false));

        var confirmed = Wp08Success(environment.Reconcile.ConfirmNarrativeModify(
            Wp08Relative(environment.Fixture.Directory, path), "wp08-confirm-modify", "wp08-author"));
        Wp08True(confirmed.AuthorityChanged, "Confirmed Narrative modify did not traverse WP06 Apply.");
        Wp08True(environment.Fixture.CurrentState(Wp07ObjectA).StateRevisionId != revisionBefore,
            "Confirmed Narrative modify did not create a new state revision.");
        Wp08True(File.ReadAllText(path, Encoding.UTF8).Contains("afterconfirmwp08", StringComparison.Ordinal),
            "Confirmed Narrative modify did not rematerialize the deterministic projection.");
        environment.Fixture.AssertRegistryTrusted(Wp07ObjectA, path);

        var revisionAfter = environment.Fixture.CurrentState(Wp07ObjectA).StateRevisionId;
        File.AppendAllText(path, "\n", new UTF8Encoding(false));
        var equivalent = Wp08Success(environment.Reconcile.ConfirmNarrativeModify(
            Wp08Relative(environment.Fixture.Directory, path), "wp08-confirm-equivalent", "wp08-author"));
        Wp08False(equivalent.AuthorityChanged, "Semantically equivalent bytes created a needless Authority revision.");
        Wp08Equal(revisionAfter, environment.Fixture.CurrentState(Wp07ObjectA).StateRevisionId,
            "Equivalent reconcile changed the Authority revision.");
        environment.Fixture.AssertRegistryTrusted(Wp07ObjectA, path);
    }

    private static void MissingRenameAndMachineProjectionRequireExplicitResolution()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "location", "renamewp08"), "wp08-rename-add"));
        var canonical = environment.Fixture.ObjectProjectionPath("location", Wp07ObjectA);
        var moved = Path.Combine(environment.Fixture.Directory, "Narrative", "moved", "renamed.md");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(canonical, moved);
        var renamed = environment.Engine.Scan(FileEventSource.FullRescan);
        var missing = Wp08Observation(renamed, canonical, environment.Fixture.Directory);
        Wp08Equal(ReconcileClassification.SuspectedRename, missing.Classification,
            "Same-object move was not detected as a suspected rename.");
        Wp08Equal(RenameConfidence.High, missing.RenameEvidence!.Confidence,
            "Exact objectId+digest rename did not receive high confidence.");
        Wp08False(File.Exists(canonical), "Rename heuristic silently adopted or rewrote the canonical path.");

        File.WriteAllText(moved,
            File.ReadAllText(moved, Encoding.UTF8).Replace("renamewp08", "weakrenamewp08", StringComparison.Ordinal),
            new UTF8Encoding(false));
        var weak = Wp08Observation(
            environment.Engine.Scan(FileEventSource.FullRescan), canonical, environment.Fixture.Directory);
        Wp08Equal(ReconcileClassification.SuspectedRename, weak.Classification,
            "Weak same-object rename evidence was not left pending confirmation.");
        Wp08True(weak.RenameEvidence!.Confidence != RenameConfidence.High,
            "Digest-mismatched rename evidence was incorrectly treated as exact.");
        File.WriteAllText(moved,
            File.ReadAllText(moved, Encoding.UTF8).Replace(Wp07ObjectA, Wp07ObjectB, StringComparison.Ordinal),
            new UTF8Encoding(false));
        Wp08Equal(ReconcileClassification.RegisteredMissing,
            Wp08Observation(environment.Engine.Scan(FileEventSource.FullRescan), canonical, environment.Fixture.Directory).Classification,
            "Wrong-objectId move was incorrectly bound to the registered object.");

        Wp08Success(environment.Reconcile.Restore(Wp08Relative(environment.Fixture.Directory, canonical)));
        Wp08True(File.Exists(canonical), "Explicit Restore did not recreate the registered canonical projection.");
        Wp08Success(environment.Reconcile.Delete(Wp08Relative(environment.Fixture.Directory, moved)));
        Wp08False(File.Exists(moved), "Explicit Delete did not remove the unregistered rename candidate.");
        var machine = Path.Combine(environment.Fixture.Directory, "Narrative", "state", "registry.json");
        File.AppendAllText(machine, " ", new UTF8Encoding(false));
        var dirtyMachine = environment.Engine.Scan(FileEventSource.FullRescan);
        Wp08Equal(ReconcileClassification.ProjectionModified,
            Wp08Observation(dirtyMachine, machine, environment.Fixture.Directory).Classification,
            "Machine JSON edit was not classified as a rebuild-only projection change.");
        Wp08Success(environment.Reconcile.Restore(Wp08Relative(environment.Fixture.Directory, machine)));
        Wp08Equal(ReconcileClassification.Unchanged,
            Wp08Observation(environment.Engine.Scan(FileEventSource.FullRescan), machine, environment.Fixture.Directory).Classification,
            "Machine projection remained dirty after deterministic rebuild.");

        File.Delete(canonical);
        var deleted = environment.Engine.Scan(FileEventSource.Polling);
        Wp08Equal(ReconcileClassification.RegisteredMissing,
            Wp08Observation(deleted, canonical, environment.Fixture.Directory).Classification,
            "Registered deletion was interpreted as Authority REMOVE.");
        Wp08Equal("missing", environment.Fixture.Scalar<string>(
            "SELECT registration_state FROM registry_entries WHERE object_id=$id;", ("$id", Wp07ObjectA)),
            "Missing registered path did not remain an explicit Registry state.");
        Wp08Success(environment.Reconcile.Restore(Wp08Relative(environment.Fixture.Directory, canonical)));
        environment.Fixture.AssertRegistryTrusted(Wp07ObjectA, canonical);
    }

    private static void MonitorHandlesPollingOverflowBatchStormAndCancellation()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "character", "monitorwp08"), "wp08-monitor-add"));
        var path = environment.Fixture.ObjectProjectionPath("character", Wp07ObjectA);
        var selfWriteBefore = environment.Monitor.HeavyReconcilePassCount;
        Wp08Success(environment.Monitor.BeginExternalBatch("git-self-write"));
        Wp07ProjectionSuccess(environment.Fixture.Rebuilder.Rebuild());
        environment.Monitor.InjectNativeEvent(FileEventKind.Modified,
            Wp08Relative(environment.Fixture.Directory, path));
        var selfWriteBatch = Wp08Success(environment.Monitor.EndExternalBatch("git-self-write"));
        Wp08Equal(selfWriteBefore + 1, environment.Monitor.HeavyReconcilePassCount,
            "Core self-write batch did not collapse to one reconcile pass.");
        Wp08Equal(ReconcileClassification.Unchanged,
            Wp08Observation(selfWriteBatch, path, environment.Fixture.Directory).Classification,
            "Core self-write inside a batch dirtied its own trusted projection.");

        File.AppendAllText(path, "external", new UTF8Encoding(false));
        Wp08Equal(ReconcileClassification.RegisteredModified,
            Wp08Observation(environment.Monitor.PollOnce(), path, environment.Fixture.Directory).Classification,
            "Five-second fallback scan path could not detect a missed native event.");

        environment.Monitor.MarkNativeWatcherUnreliable();
        var overflow = Wp08Success(environment.Monitor.FlushPending(force: true));
        Wp08True(overflow is not null && overflow.Source == FileEventSource.FullRescan,
            "Native watcher overflow did not force a full rescan.");

        var before = environment.Monitor.HeavyReconcilePassCount;
        Wp08Success(environment.Monitor.BeginExternalBatch("git"));
        for (var index = 0; index < 1000; index++)
        {
            environment.Monitor.InjectNativeEvent(FileEventKind.Modified,
                Wp08Relative(environment.Fixture.Directory, path));
        }

        var batch = Wp08Success(environment.Monitor.EndExternalBatch("git"));
        Wp08Equal(before + 1, environment.Monitor.HeavyReconcilePassCount,
            "A 1000-event Git storm triggered more than one heavy reconcile pass.");
        Wp08Equal(ReconcileClassification.RegisteredModified,
            Wp08Observation(batch, path, environment.Fixture.Directory).Classification,
            "Batch-end rescan lost the final external state.");

        var bulkRoot = Path.Combine(environment.Fixture.Directory, "Narrative", "bulk");
        Directory.CreateDirectory(bulkRoot);
        for (var index = 0; index < 260; index++)
        {
            File.WriteAllText(
                Path.Combine(bulkRoot, $"{index:D4}.md"),
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
        }

        var batched = environment.Engine.Scan(FileEventSource.FullRescan, batchSize: 50);
        Wp08True(batched.BatchCount >= 6, "Full scan did not honor bounded persistence batches.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Wp08Throws<OperationCanceledException>(() => environment.Engine.Scan(
            FileEventSource.FullRescan, cancellationToken: cancelled.Token),
            "Cancelled full scan continued into persistence.");
    }

    private static void FreshAuthorityGateClosesDebounceRaceAndIgnoresDrafts()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "character", "gatebasewp08"), "wp08-gate-add"));
        var second = environment.Fixture.CreateAdd(Wp07ObjectB, "location", "gateblockedwp08");
        var path = environment.Fixture.ObjectProjectionPath("character", Wp07ObjectA);
        File.AppendAllText(path, "race", new UTF8Encoding(false));
        var blocked = environment.GatedService.Apply(new ApplyNarrativeChangeSetCommand(
            second, "wp08-gate-blocked", NarrativeDecisionKind.AuthorConfirmed, "wp08-author"));
        Wp08Equal(NarrativeChangeError.AuthorityDirty, blocked.Failure!.Code,
            "Apply passed while disk was dirty but the debounce queue had not fired.");

        Wp08Success(environment.Reconcile.Restore(Wp08Relative(environment.Fixture.Directory, path)));
        var draftDirectory = Path.Combine(environment.Fixture.Directory, "Draft", "chapter");
        Directory.CreateDirectory(draftDirectory);
        File.WriteAllText(Path.Combine(draftDirectory, "chapter.md"), "draft changes are working state", new UTF8Encoding(false));
        Wp08Success(environment.GatedService.Apply(new ApplyNarrativeChangeSetCommand(
            second, "wp08-gate-clean", NarrativeDecisionKind.AuthorConfirmed, "wp08-author")));
        Wp08Equal(1, Wp07SearchSuccess(environment.Fixture.Search("gateblockedwp08")).Count,
            "Draft-only change blocked or corrupted a clean Authority commit.");
    }

    private static void CurrentManuscriptExternalEditBlocksChapterSubmissionAndRestores()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(fixture.DraftPath, "manuscript baseline", new UTF8Encoding(false));
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp08-manuscript-base")));
        Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId)));
        Success(fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "wp08-manuscript-base")));

        var tracker = new SelfWriteTracker();
        var engine = new ProjectReconcileEngine(fixture.Root, fixture.DatabasePath, fixture.BlobStore, tracker);
        var gate = new SqliteAuthoritySurfaceHealthGate(engine);
        var guardedService = new ChapterAuthorityService(
            fixture.BlobStore,
            fixture.Coordinator,
            new SqliteChapterAuthorityStore(fixture.DatabasePath, fixture.Coordinator),
            fixture.Reviewer,
            gate);
        File.AppendAllText(fixture.CurrentManuscriptPath, "external", new UTF8Encoding(false));
        var blocked = guardedService.SubmitChapterDraft(new SubmitChapterDraftCommand(
            fixture.ChapterId, fixture.DraftPath, "wp08-manuscript-blocked"));
        Wp08Equal(ChapterAuthorityError.AuthorityDirty, blocked.Failure!.Code,
            "External Current Manuscript edit did not block chapter mutation.");

        var restored = engine.RestoreManuscript(
            Wp08Relative(fixture.Root, fixture.CurrentManuscriptPath),
            new AtomicAuthorityMaterializer(fixture.Root, fixture.BlobStore, tracker));
        Wp08Success(restored);
        Wp08True(gate.Check(AuthoritySurfaceHealthRequest.Standard).IsHealthy,
            "Restoring Current Manuscript did not clear the Authority health gate.");
    }

    private static void NativeWatcherIsolatesInternalStateAndUsesConfiguredDebounce()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "character", "nativewatchwp08"), "wp08-native-watch"));
        Directory.CreateDirectory(Path.Combine(environment.Fixture.Directory, "Manuscript", "current"));
        Directory.CreateDirectory(Path.Combine(environment.Fixture.Directory, ".llmw"));
        Directory.CreateDirectory(Path.Combine(environment.Fixture.Directory, "Draft"));
        var configuredDebounce = TimeSpan.FromSeconds(2);
        using var monitor = new ProjectFileMonitor(
            environment.Engine,
            environment.Tracker,
            pollingInterval: TimeSpan.FromHours(1),
            debounce: configuredDebounce);
        monitor.Start();

        Wp08True(monitor.NativeWatcherAvailable,
            "Relevant native watcher surfaces were not available after Start.");
        Wp08Equal(2, monitor.NativeWatchRoots.Count,
            "Native monitor did not attach exactly the two existing reconcile surfaces.");
        Wp08True(monitor.NativeWatchRoots.Any(root => StringComparer.OrdinalIgnoreCase.Equals(
                root, Path.Combine(environment.Fixture.Directory, "Narrative"))),
            "Narrative native watcher root is missing.");
        Wp08True(monitor.NativeWatchRoots.Any(root => StringComparer.OrdinalIgnoreCase.Equals(
                root, Path.Combine(environment.Fixture.Directory, "Manuscript", "current"))),
            "Current Manuscript native watcher root is missing.");
        Wp08False(monitor.NativeWatchRoots.Any(root => StringComparer.OrdinalIgnoreCase.Equals(
                root, environment.Fixture.Directory)),
            "Project-root catch-all native watcher is still active.");
        Wp08False(monitor.NativeWatchRoots.Any(root => root.Contains(".llmw", StringComparison.OrdinalIgnoreCase)),
            "Internal .llmw state is still a native watcher root.");
        Wp08False(monitor.NativeWatchRoots.Any(root => root.Contains("Draft", StringComparison.OrdinalIgnoreCase)),
            "Draft is still a native watcher root.");

        environment.Engine.Scan(FileEventSource.FullRescan);
        using (var internalBytes = new MemoryStream(Encoding.UTF8.GetBytes("internal blob activity"), writable: false))
        {
            environment.Fixture.BlobStore.Stage(internalBytes);
        }

        monitor.InjectNativeEvent(FileEventKind.Modified, ".llmw/project.db");
        monitor.InjectNativeEvent(FileEventKind.Modified, "Draft/chapter.md");
        Wp08False(SpinWait.SpinUntil(
                () => monitor.PendingEventCount > 0,
                TimeSpan.FromMilliseconds(250)),
            "Core DB/WAL/blob or ignored Draft activity entered the native event queue.");

        var projectionPath = environment.Fixture.ObjectProjectionPath("character", Wp07ObjectA);
        File.AppendAllText(projectionPath, "external", new UTF8Encoding(false));
        Wp08True(SpinWait.SpinUntil(
                () => monitor.PendingEventCount > 0,
                TimeSpan.FromSeconds(3)),
            "Narrative external modification did not enter the native watcher path.");
        Wp08Equal(configuredDebounce, monitor.ConfiguredDebounce,
            "Monitor did not retain the custom debounce.");
        Wp08Equal(configuredDebounce, monitor.LastScheduledDebounce,
            "Native event scheduling did not use the configured debounce value.");
        Wp08Equal(0L, monitor.HeavyReconcilePassCount,
            "Internal Core activity formed a native reconcile feedback chain.");
    }

    private static void FullScanSkipsOutsideReparseTargetsAndLoops()
    {
        using var environment = Wp08Environment.Create();
        Wp08Success(environment.Fixture.Apply(
            environment.Fixture.CreateAdd(Wp07ObjectA, "character", "reparsewp08"), "wp08-reparse"));
        var outside = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP08.Outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideSecret = Path.Combine(outside, "outside-secret.md");
        File.WriteAllText(outsideSecret, "outside-secret-wp08", new UTF8Encoding(false));
        var narrativeRoot = Path.Combine(environment.Fixture.Directory, "Narrative");
        var outsideLink = Path.Combine(narrativeRoot, "outside-link");
        var loopLink = Path.Combine(narrativeRoot, "loop");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(outsideLink, outside);
                Directory.CreateSymbolicLink(loopLink, narrativeRoot);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var report = environment.Engine.Scan(FileEventSource.FullRescan);
            Wp08False(report.Observations.Any(observation =>
                    observation.RelativePath.Contains("outside-link", StringComparison.OrdinalIgnoreCase) ||
                    observation.RelativePath.Contains("outside-secret", StringComparison.OrdinalIgnoreCase)),
                "Full scan traversed a reparse target outside the project.");
            Wp08False(report.Observations.Any(observation =>
                    observation.RelativePath.Contains("/loop/", StringComparison.OrdinalIgnoreCase)),
                "Full scan recursed through a symbolic-link loop.");
            using var monitor = new ProjectFileMonitor(environment.Engine, environment.Tracker);
            Wp08Throws<UnauthorizedAccessException>(
                () => monitor.InjectNativeEvent(FileEventKind.Modified, "Narrative/outside-link/outside-secret.md"),
                "Native event validation accepted an existing reparse traversal.");
            Wp08Equal(0, monitor.PendingEventCount,
                "Rejected reparse traversal entered the native event queue.");
        }
        finally
        {
            DeleteDirectoryLinkIfPresent(outsideLink);
            DeleteDirectoryLinkIfPresent(loopLink);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void MissingWatchSurfacesUsePollingAndReattach()
    {
        using var fixture = Wp07Fixture.Create();
        var tracker = new SelfWriteTracker();
        var engine = new ProjectReconcileEngine(
            fixture.Directory, fixture.DatabasePath, fixture.BlobStore, tracker);
        using var monitor = new ProjectFileMonitor(
            engine,
            tracker,
            pollingInterval: TimeSpan.FromHours(1));
        monitor.Start();
        Wp08False(monitor.NativeWatcherAvailable,
            "Native watcher reported available when no relevant surface existed.");
        Wp08Equal(0, monitor.NativeWatchRoots.Count,
            "Monitor created or attached a semantic directory that did not exist.");
        Wp08False(Directory.Exists(Path.Combine(fixture.Directory, "Narrative")),
            "Monitor Start created the Narrative semantic directory.");
        Wp08False(Directory.Exists(Path.Combine(fixture.Directory, "Manuscript", "current")),
            "Monitor Start created the Current Manuscript semantic directory.");

        Directory.CreateDirectory(Path.Combine(fixture.Directory, "Narrative"));
        monitor.PollOnce();
        Wp08True(monitor.NativeWatcherAvailable,
            "Polling fallback did not reattach after a relevant surface appeared.");
        Wp08Equal(1, monitor.NativeWatchRoots.Count,
            "Polling fallback attached an unexpected native watcher set.");
    }

    private static void NativeEventTokenReachesPrimarySuppression()
    {
        using var fixture = Wp07Fixture.Create();
        Wp08Success(fixture.Apply(
            fixture.CreateAdd(Wp07ObjectA, "character", "tokenwp08"), "wp08-token"));
        var recording = new RecordingSelfWriteTracker { ActiveToken = "wp08-primary-token" };
        var engine = new ProjectReconcileEngine(
            fixture.Directory, fixture.DatabasePath, fixture.BlobStore, recording);
        using var monitor = new ProjectFileMonitor(engine, recording);
        var relativePath = Wp08Relative(
            fixture.Directory,
            fixture.ObjectProjectionPath("character", Wp07ObjectA));
        monitor.InjectNativeEvent(FileEventKind.Modified, relativePath);
        Wp08Success(monitor.FlushPending(force: true));
        Wp08True(recording.SuppressionCalls.Any(call =>
                StringComparer.OrdinalIgnoreCase.Equals(call.RelativePath, relativePath) &&
                StringComparer.Ordinal.Equals(call.OperationToken, "wp08-primary-token")),
            "Coalesced native event token did not reach classifier suppression.");

        recording.SuppressionCalls.Clear();
        recording.ActiveToken = null;
        monitor.PollOnce();
        Wp08True(recording.SuppressionCalls.Any(call =>
                StringComparer.OrdinalIgnoreCase.Equals(call.RelativePath, relativePath) &&
                call.OperationToken is null),
            "Polling/offline scan did not use token-absent path+digest fallback semantics.");
    }

    private static void DeleteDirectoryLinkIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
    }

    private static void RunWp08(string name, Action test)
    {
        test();
        Wp08PassedTests.Add(name);
    }

    private static T Wp08Success<T>(ReconcileResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected reconcile success; failure={result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static T Wp08Success<T>(NarrativeChangeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected Narrative success; failure={result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static ReconcileObservation Wp08Observation(ReconcileScanReport report, string fullPath, string root)
    {
        var relative = Wp08Relative(root, fullPath);
        return report.Observations.Single(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.RelativePath, relative));
    }

    private static string Wp08Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static void Wp08Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void Wp08True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Wp08False(bool condition, string message) => Wp08True(!condition, message);

    private static void Wp08Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class Wp08Environment : IDisposable
    {
        private Wp08Environment(
            Wp07Fixture fixture,
            SelfWriteTracker tracker,
            ProjectReconcileEngine engine,
            NarrativeChangeService gatedService,
            NarrativeReconcileService reconcile,
            ProjectFileMonitor monitor)
        {
            Fixture = fixture;
            Tracker = tracker;
            Engine = engine;
            GatedService = gatedService;
            Reconcile = reconcile;
            Monitor = monitor;
        }

        public Wp07Fixture Fixture { get; }
        public SelfWriteTracker Tracker { get; }
        public ProjectReconcileEngine Engine { get; }
        public NarrativeChangeService GatedService { get; }
        public NarrativeReconcileService Reconcile { get; }
        public ProjectFileMonitor Monitor { get; }

        public static Wp08Environment Create()
        {
            var tracker = new SelfWriteTracker();
            var fixture = Wp07Fixture.Create(selfWriteTracker: tracker);
            var engine = new ProjectReconcileEngine(
                fixture.Directory, fixture.DatabasePath, fixture.BlobStore, tracker);
            var gate = new SqliteAuthoritySurfaceHealthGate(engine);
            var store = new SqliteNarrativeChangeStore(
                fixture.DatabasePath, fixture.BlobStore, fixture.Coordinator, enableProjection: true);
            var service = new NarrativeChangeService(
                fixture.BlobStore, store, new SemanticFake(), new ImpactFake(), gate);
            var reconcile = new NarrativeReconcileService(
                fixture.DatabasePath,
                engine,
                fixture.BlobStore,
                fixture.Rebuilder,
                service,
                new AtomicAuthorityMaterializer(fixture.Directory, fixture.BlobStore, tracker));
            var monitor = new ProjectFileMonitor(engine, tracker);
            return new Wp08Environment(fixture, tracker, engine, service, reconcile, monitor);
        }

        public void Dispose()
        {
            Monitor.Dispose();
            Fixture.Dispose();
        }
    }

    private sealed class MutatingAuthorityGate(
        IAuthoritySurfaceHealthGate inner,
        Action mutateAfterFirstCheck) : IAuthoritySurfaceHealthGate
    {
        private int calls;

        public AuthoritySurfaceHealth Check(
            AuthoritySurfaceHealthRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = inner.Check(request, cancellationToken);
            if (Interlocked.Increment(ref calls) == 1)
            {
                mutateAfterFirstCheck();
            }

            return result;
        }
    }

    private sealed class RecordingSelfWriteTracker : ISelfWriteTracker
    {
        public string? ActiveToken { get; set; }

        public List<(string? OperationToken, string RelativePath, string? Digest)> SuppressionCalls { get; } = [];

        public ISelfWriteOperation BeginOperation(IReadOnlyList<SelfWriteExpectation> expectations) =>
            new RecordingSelfWriteOperation();

        public string? TryGetActiveToken(string relativePath) => ActiveToken;

        public bool ShouldSuppress(
            string? operationToken,
            string relativePath,
            string? observedPhysicalDigest)
        {
            SuppressionCalls.Add((operationToken, relativePath, observedPhysicalDigest));
            return false;
        }

        private sealed class RecordingSelfWriteOperation : ISelfWriteOperation
        {
            public string Token => "recording-operation";

            public void Dispose()
            {
            }
        }
    }
}
