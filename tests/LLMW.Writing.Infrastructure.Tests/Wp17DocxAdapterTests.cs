using System.IO.Compression;
using System.Text;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Infrastructure.Docx;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp17DocxAdapterTests()
    {
        var adapter = new OpenXmlDocxDocumentAdapter();
        RoundTripSupportedText(adapter);
        RejectsUntrustedInput(adapter);
        ConfinesOpenXmlToInfrastructure();
        Run("WP17 adapter never accepts paths", AdapterDoesNotAcceptPaths);
    }

    private static void RoundTripSupportedText(OpenXmlDocxDocumentAdapter adapter)
    {
        var source = DocxEditorDocument.FromLogicalText("First paragraph\n\nThird paragraph");
        var created = adapter.Create(source);
        AssertTrue(created.Succeeded, "DOCX creation must succeed for plain editor text.");
        var read = adapter.Read(created.Value!);
        AssertTrue(read.Succeeded, "Created DOCX must be readable.");
        AssertEqual(source.LogicalText, read.Value!.LogicalText, "DOCX text round-trip must be deterministic.");
        AssertEqual("p-00000000", read.Value.Paragraphs[0].Anchor, "Paragraph anchors must be project-owned and stable.");
        Run("WP17 DOCX text round-trip", () => { });
    }

    private static void RejectsUntrustedInput(OpenXmlDocxDocumentAdapter adapter)
    {
        AssertFailure(DocxDocumentFailure.MalformedPackage, adapter.Read("not a zip"u8.ToArray()), "Corrupted package must use a typed failure.");
        AssertFailure(DocxDocumentFailure.Oversized, adapter.Read(new byte[OpenXmlDocxDocumentAdapter.MaximumPackageBytes + 1]), "Oversized package must be rejected before parsing.");
        AssertFailure(DocxDocumentFailure.MalformedXml, adapter.Read(CreatePackage("<w:document", null, null)), "Malformed XML must use a typed failure.");
        AssertFailure(DocxDocumentFailure.UnsupportedFeature, adapter.Read(CreatePackage("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:tbl /></w:body></w:document>", null, null)), "Tables are unsupported in the WP17 plain-text scope.");
        AssertFailure(DocxDocumentFailure.ExternalRelationship, adapter.Read(CreatePackage(ValidDocumentXml, "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"https://example.invalid/payload\" TargetMode=\"External\" /></Relationships>", null)), "External relationships must be rejected without dereferencing their targets.");
        AssertFailure(DocxDocumentFailure.UnsupportedFeature, adapter.Read(CreatePackage(ValidDocumentXml, null, "word/vbaProject.bin")), "Macro-bearing packages must be rejected and never executed.");
        Run("WP17 corrupted, malformed, unsupported, external, macro and oversized failures", () => { });
    }

    private static void ConfinesOpenXmlToInfrastructure()
    {
        var applicationRoot = Path.Combine(Environment.CurrentDirectory, "src", "LLMW.Writing.Application");
        var contractsRoot = Path.Combine(Environment.CurrentDirectory, "src", "LLMW.Writing.Contracts");
        AssertTrue(
            !Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(contractsRoot, "*.cs", SearchOption.AllDirectories))
                .Select(File.ReadAllText)
                .Any(source => source.Contains("DocumentFormat.OpenXml", StringComparison.Ordinal)),
            "OpenXml APIs must not leak into Application or Contracts source.");
        AssertTrue(
            !typeof(IDocxDocumentAdapter).Assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name?.Contains("DocumentFormat.OpenXml", StringComparison.Ordinal) == true),
            "The Application adapter contract must not reference the OpenXml assembly.");
        Run("WP17 OpenXml isolation", () => { });
    }

    private static void AdapterDoesNotAcceptPaths()
    {
        AssertTrue(
            typeof(OpenXmlDocxDocumentAdapter).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(method => method.DeclaringType == typeof(OpenXmlDocxDocumentAdapter))
                .SelectMany(method => method.GetParameters())
                .All(parameter => parameter.ParameterType != typeof(string)),
            "DOCX adapter public operations must accept bytes/project-owned models, never filesystem paths.");
    }

    private static void AssertFailure<T>(DocxDocumentFailure expected, DocxAdapterResult<T> result, string message)
    {
        AssertTrue(!result.Succeeded && result.Failure == expected, message);
    }

    private static byte[] CreatePackage(string documentXml, string? relationshipsXml, string? additionalEntry)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            WriteEntry(archive, "word/document.xml", documentXml);
            if (relationshipsXml is not null)
            {
                WriteEntry(archive, "word/_rels/document.xml.rels", relationshipsXml);
            }

            if (additionalEntry is not null)
            {
                WriteEntry(archive, additionalEntry, "macro");
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private const string ValidDocumentXml = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>safe</w:t></w:r></w:p></w:body></w:document>";
}
