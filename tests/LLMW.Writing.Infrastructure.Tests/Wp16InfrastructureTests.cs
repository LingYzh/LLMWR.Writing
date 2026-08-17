using System.Text;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp16InfrastructureTests()
    {
        Run(nameof(DraftAtomicReplaceIsUtf8NoBomLf), DraftAtomicReplaceIsUtf8NoBomLf);
        Run(nameof(DraftAtomicReplaceRejectsStaleBase), DraftAtomicReplaceRejectsStaleBase);
        Run(nameof(DraftStoreRejectsManuscriptAndEscapes), DraftStoreRejectsManuscriptAndEscapes);
        Run(nameof(DraftPrePublishFaultLeavesOriginalBytes), DraftPrePublishFaultLeavesOriginalBytes);
        Run(nameof(SchemaRemainsVersionOne), SchemaRemainsVersionOne);
    }

    private static void DraftAtomicReplaceIsUtf8NoBomLf()
    {
        var root = CreateDraftProject();
        try
        {
            var chapter = Id1;
            var relative = "Draft/" + chapter + "/chapter.md";
            var path = Path.Combine(root, "Draft", chapter, "chapter.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', (byte)'\r', (byte)'\n' });
            var store = new DraftFileStore(new ProjectPathResolver(root));
            var read = store.Read(relative);
            AssertTrue(read.Succeeded, "BOM CRLF Draft must be readable.");
            var logical = TextDocumentCodec.TryDecode(read.Value!.Bytes).Value!.LogicalText;
            var encoded = TextDocumentCodec.EncodeUtf8NoBomLf(logical);
            var written = store.AtomicReplace(relative, read.Value.Digest, encoded, NoEditorSaveFaultInjector.Instance);
            AssertTrue(written.Succeeded, "Atomic replace must succeed.");
            var onDisk = File.ReadAllBytes(path);
            AssertTrue(onDisk[0] != 0xEF, "Saved Draft must not keep BOM.");
            AssertEqual("A\n", Encoding.UTF8.GetString(onDisk), "Saved Draft must be LF.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void DraftAtomicReplaceRejectsStaleBase()
    {
        var root = CreateDraftProject();
        try
        {
            var relative = "Draft/" + Id1 + "/chapter.md";
            var path = Path.Combine(root, "Draft", Id1, "chapter.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, "A"u8.ToArray());
            var store = new DraftFileStore(new ProjectPathResolver(root));
            var first = store.Read(relative).Value!;
            File.WriteAllBytes(path, "C"u8.ToArray());
            var stale = store.AtomicReplace(relative, first.Digest, "B"u8.ToArray(), NoEditorSaveFaultInjector.Instance);
            AssertEqual(IpcErrorCodes.EditorStaleBase, stale.ErrorCode!, "External change must be stale.");
            AssertEqual("C", File.ReadAllText(path), "External bytes must be preserved.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void DraftStoreRejectsManuscriptAndEscapes()
    {
        var root = CreateDraftProject();
        try
        {
            var store = new DraftFileStore(new ProjectPathResolver(root));
            Directory.CreateDirectory(Path.Combine(root, "Manuscript", "current"));
            File.WriteAllText(Path.Combine(root, "Manuscript", "current", Id1 + ".md"), "canon");
            AssertEqual(
                IpcErrorCodes.EditorDocumentNotWritable,
                store.Read("Manuscript/current/" + Id1 + ".md").ErrorCode!,
                "Manuscript must not be readable as Draft.");
            AssertEqual(
                IpcErrorCodes.EditorDocumentNotWritable,
                store.Read("Draft/" + Id1 + "/../chapter.md").ErrorCode!,
                "Escape relative path must be denied.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void DraftPrePublishFaultLeavesOriginalBytes()
    {
        var root = CreateDraftProject();
        try
        {
            var relative = "Draft/" + Id1 + "/chapter.md";
            var path = Path.Combine(root, "Draft", Id1, "chapter.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, "OLD"u8.ToArray());
            var store = new DraftFileStore(new ProjectPathResolver(root));
            var first = store.Read(relative).Value!;
            var faults = new MutableEditorSaveFaultInjector { Fault = EditorSaveFaultPoint.BeforeAtomicReplace };
            try
            {
                store.AtomicReplace(relative, first.Digest, "NEW"u8.ToArray(), faults);
                throw new InvalidOperationException("fault must throw");
            }
            catch (EditorSaveFaultInjectedException)
            {
            }

            AssertEqual("OLD", File.ReadAllText(path), "Pre-publish fault must leave original Draft.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void SchemaRemainsVersionOne()
    {
        AssertEqual(1, SqliteMigrationRunner.CurrentSchemaVersion, "WP16 must not migrate schema.");
    }

    private static string CreateDraftProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP16", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
