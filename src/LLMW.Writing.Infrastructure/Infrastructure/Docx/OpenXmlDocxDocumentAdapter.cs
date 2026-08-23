using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LLMW.Writing.Application.Editor;

namespace LLMW.Writing.Infrastructure.Docx;

/// <summary>
/// The sole Open XML SDK boundary. It accepts bytes only; callers never supply a path and
/// no relationship target is dereferenced by this adapter.
/// </summary>
public sealed class OpenXmlDocxDocumentAdapter : IDocxDocumentAdapter
{
    public const int MaximumPackageBytes = 20 * 1024 * 1024;
    private const long MaximumExpandedPackageBytes = 64L * 1024 * 1024;
    private const int MaximumPackageEntries = 512;

    public DocxAdapterResult<DocxEditorDocument> Read(ReadOnlyMemory<byte> packageBytes)
    {
        if (packageBytes.Length == 0 || packageBytes.Length > MaximumPackageBytes)
        {
            return DocxAdapterResult<DocxEditorDocument>.Fail(DocxDocumentFailure.Oversized);
        }

        var preflight = Preflight(packageBytes);
        if (preflight is not null)
        {
            return DocxAdapterResult<DocxEditorDocument>.Fail(preflight.Value);
        }

        try
        {
            using var stream = new MemoryStream(packageBytes.ToArray(), writable: false);
            using var document = WordprocessingDocument.Open(stream, isEditable: false, new OpenSettings
            {
                AutoSave = false
            });
            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return DocxAdapterResult<DocxEditorDocument>.Fail(DocxDocumentFailure.MalformedPackage);
            }

            var paragraphs = body.Elements<Paragraph>()
                .Select((paragraph, index) => new DocxParagraph($"p-{index:D8}", ExtractParagraphText(paragraph)))
                .ToArray();
            return DocxAdapterResult<DocxEditorDocument>.Ok(new DocxEditorDocument(paragraphs));
        }
        catch (OpenXmlPackageException)
        {
            return DocxAdapterResult<DocxEditorDocument>.Fail(DocxDocumentFailure.MalformedPackage);
        }
        catch (InvalidDataException)
        {
            return DocxAdapterResult<DocxEditorDocument>.Fail(DocxDocumentFailure.MalformedPackage);
        }
        catch (XmlException)
        {
            return DocxAdapterResult<DocxEditorDocument>.Fail(DocxDocumentFailure.MalformedXml);
        }
        catch (IOException)
        {
            return DocxAdapterResult<DocxEditorDocument>.Fail(DocxDocumentFailure.MalformedPackage);
        }
    }

    public DocxAdapterResult<byte[]> Create(DocxEditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Paragraphs.Count > 100_000 || document.LogicalText.Length > MaximumExpandedPackageBytes)
        {
            return DocxAdapterResult<byte[]>.Fail(DocxDocumentFailure.Oversized);
        }

        try
        {
            using var stream = new MemoryStream();
            using (var documentPackage = WordprocessingDocument.Create(
                       stream,
                       WordprocessingDocumentType.Document,
                       autoSave: true))
            {
                var main = documentPackage.AddMainDocumentPart();
                var body = new Body();
                foreach (var source in document.Paragraphs)
                {
                    body.Append(new Paragraph(new Run(new Text(source.Text ?? string.Empty)
                    {
                        Space = SpaceProcessingModeValues.Preserve
                    })));
                }

                main.Document = new Document(body);
                main.Document.Save();
            }

            var bytes = stream.ToArray();
            return bytes.Length <= MaximumPackageBytes
                ? DocxAdapterResult<byte[]>.Ok(bytes)
                : DocxAdapterResult<byte[]>.Fail(DocxDocumentFailure.Oversized);
        }
        catch (OpenXmlPackageException)
        {
            return DocxAdapterResult<byte[]>.Fail(DocxDocumentFailure.MalformedPackage);
        }
        catch (IOException)
        {
            return DocxAdapterResult<byte[]>.Fail(DocxDocumentFailure.MalformedPackage);
        }
    }

    private static string ExtractParagraphText(Paragraph paragraph)
    {
        var text = new System.Text.StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text runText:
                    text.Append(runText.Text);
                    break;
                case TabChar:
                    text.Append('\t');
                    break;
                case Break:
                    text.Append('\n');
                    break;
            }
        }

        return text.ToString();
    }

    private static DocxDocumentFailure? Preflight(ReadOnlyMemory<byte> packageBytes)
    {
        try
        {
            using var bytes = new MemoryStream(packageBytes.ToArray(), writable: false);
            using var archive = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumPackageEntries)
            {
                return DocxDocumentFailure.MalformedPackage;
            }

            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FullName)
                    || entry.FullName.Contains("..", StringComparison.Ordinal)
                    || entry.FullName.StartsWith('/'))
                {
                    return DocxDocumentFailure.MalformedPackage;
                }

                expanded += entry.Length;
                if (expanded > MaximumExpandedPackageBytes)
                {
                    return DocxDocumentFailure.Oversized;
                }

                if (IsMacroOrEncrypted(entry.FullName))
                {
                    return entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase)
                        ? DocxDocumentFailure.UnsupportedFeature
                        : DocxDocumentFailure.Encrypted;
                }

                if (IsUnsupportedPart(entry.FullName))
                {
                    return DocxDocumentFailure.UnsupportedFeature;
                }

                if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    var xmlFailure = ValidateXml(entry, entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase));
                    if (xmlFailure is not null)
                    {
                        return xmlFailure;
                    }
                }
            }

            return archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry("word/document.xml") is null
                ? DocxDocumentFailure.MalformedPackage
                : null;
        }
        catch (InvalidDataException)
        {
            return DocxDocumentFailure.MalformedPackage;
        }
        catch (IOException)
        {
            return DocxDocumentFailure.MalformedPackage;
        }
    }

    private static DocxDocumentFailure? ValidateXml(ZipArchiveEntry entry, bool isRelationshipPart)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumExpandedPackageBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        try
        {
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, settings);
            while (reader.Read())
            {
                if (isRelationshipPart
                    && reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "Relationship"
                    && string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                {
                    return DocxDocumentFailure.ExternalRelationship;
                }

                if (entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
                    && reader.NodeType == XmlNodeType.Element
                    && IsUnsupportedDocumentElement(reader.LocalName))
                {
                    return DocxDocumentFailure.UnsupportedFeature;
                }
            }

            return null;
        }
        catch (XmlException)
        {
            return DocxDocumentFailure.MalformedXml;
        }
    }

    private static bool IsMacroOrEncrypted(string name) =>
        name.Contains("vba", StringComparison.OrdinalIgnoreCase)
        || name.Contains("encryption", StringComparison.OrdinalIgnoreCase)
        || name.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedPart(string name) =>
        name.StartsWith("word/comments", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/activeX/", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/charts/", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/footnotes", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("word/endnotes", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedDocumentElement(string localName) => localName is
        "tbl" or "object" or "pict" or "drawing" or "altChunk" or "fldSimple" or "instrText"
        or "ins" or "del" or "moveFrom" or "moveTo" or "commentRangeStart" or "commentRangeEnd";
}
