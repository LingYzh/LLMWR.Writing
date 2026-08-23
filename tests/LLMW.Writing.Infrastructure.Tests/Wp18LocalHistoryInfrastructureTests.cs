using LLMW.Writing.Application.History;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp18LocalHistoryInfrastructureTests()
    {
        Run(nameof(LocalHistoryMetadataIsAtomicAndDigestVerified), LocalHistoryMetadataIsAtomicAndDigestVerified);
        Run(nameof(LocalHistoryMetadataRejectsTamperingAndKeepsSchemaVersionOne), LocalHistoryMetadataRejectsTamperingAndKeepsSchemaVersionOne);
    }

    private static void LocalHistoryMetadataIsAtomicAndDigestVerified()
    {
        var root = CreateLocalHistoryProject();
        try
        {
            var store = new FileLocalHistoryMetadataStore(new ProjectPathResolver(root));
            var entry = Entry();
            var saved = store.Replace([entry]);
            AssertTrue(saved.Succeeded, "History metadata write must succeed.");
            var index = Path.Combine(root, ".llmw", "history", "index.json");
            AssertTrue(File.Exists(index), "History metadata must stay under the project-owned .llmw/history root.");
            AssertTrue(!Directory.EnumerateFiles(Path.GetDirectoryName(index)!, ".tmp-history-*").Any(), "Atomic metadata temporary files must not remain after publish.");

            var loaded = store.Load();
            AssertTrue(loaded.Succeeded, "Fresh history metadata digest must verify.");
            AssertEqual(entry.HistoryId, loaded.Value!.Single().HistoryId, "History metadata round-trip changed identity.");
            AssertEqual(entry.ContentDigest, loaded.Value!.Single().ContentDigest, "History metadata round-trip changed digest.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void LocalHistoryMetadataRejectsTamperingAndKeepsSchemaVersionOne()
    {
        var root = CreateLocalHistoryProject();
        try
        {
            var store = new FileLocalHistoryMetadataStore(new ProjectPathResolver(root));
            AssertTrue(store.Replace([Entry()]).Succeeded, "History metadata write must succeed before tamper test.");
            var index = Path.Combine(root, ".llmw", "history", "index.json");
            var text = File.ReadAllText(index);
            File.WriteAllText(index, text.Replace("aaaaaaaa", "bbbbbbbb", StringComparison.Ordinal));

            AssertEqual(IpcErrorCodes.HistoryStorageFailure, store.Load().ErrorCode!, "Tampered metadata digest must be rejected.");
            AssertEqual(1, SqliteMigrationRunner.CurrentSchemaVersion, "WP18 local history must not add a database migration.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HistoryEntry Entry() => new(
        "018f3e78-1234-7abc-8def-0123456789b1",
        "018f3e78-1234-7abc-8def-0123456789ad",
        new HistoryDocumentIdentity("018f3e78-1234-7abc-8def-0123456789a1", "chapter.md"),
        "018f3e78-1234-7abc-8def-0123456789b2",
        new string('a', 64),
        new string('b', 64),
        42,
        DateTimeOffset.Parse("2026-08-23T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
        HistoryCheckpointTriggerKind.Autosave,
        false);

    private static string CreateLocalHistoryProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP18", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
