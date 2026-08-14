using LLMW.Writing.Application.Registry;

namespace LLMW.Writing.Infrastructure.Projection;

internal static class DeterministicNarrativeChunker
{
    public static IReadOnlyList<NarrativeSearchDocument> Chunk(
        string objectId,
        string objectType,
        string artifactDigest,
        string body,
        string currentStatus)
    {
        var normalized = ProjectionCanonicalization.NormalizeText(body);
        var lines = normalized.Split('\n');
        List<NarrativeSearchDocument> documents = [];
        List<string> paragraph = [];
        var heading = string.Empty;
        var headingOrdinal = 0;
        var paragraphOrdinal = 0;

        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            var locator = $"h{headingOrdinal:0000}-p{paragraphOrdinal:0000}";
            var sectionKey = $"v1:{objectId}:{artifactDigest}:{locator}";
            documents.Add(new NarrativeSearchDocument(
                objectId,
                artifactDigest,
                sectionKey,
                heading.Length == 0 ? $"{objectType}-{objectId}" : heading,
                string.Join("\n", paragraph),
                currentStatus));
            paragraph.Clear();
            paragraphOrdinal++;
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                Flush();
                headingOrdinal++;
                paragraphOrdinal = 0;
                heading = trimmed.TrimStart('#').Trim();
                continue;
            }

            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            paragraph.Add(line);
        }

        Flush();
        if (documents.Count == 0)
        {
            documents.Add(new NarrativeSearchDocument(
                objectId,
                artifactDigest,
                $"v1:{objectId}:{artifactDigest}:h0000-p0000",
                $"{objectType}-{objectId}",
                string.Empty,
                currentStatus));
        }

        return documents;
    }
}

internal sealed record NarrativeSearchDocument(
    string ObjectId,
    string ArtifactDigest,
    string SectionKey,
    string Title,
    string Body,
    string CurrentStatus);
