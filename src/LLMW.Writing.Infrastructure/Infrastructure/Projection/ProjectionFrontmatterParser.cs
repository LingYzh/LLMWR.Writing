using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Projection;

namespace LLMW.Writing.Infrastructure.Projection;

public sealed class ProjectionFrontmatterParser : IProjectionFrontmatterParser
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "objectId",
        "objectType",
        "revision",
        "status",
        "stateRevisionId",
        "artifactDigest",
        "extensions"
    };

    public ProjectionResult<ParsedProjectionFrontmatter> Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            {
                return ProjectionResults.Fail<ParsedProjectionFrontmatter>(
                    ProjectionError.ProjectionSerializationFailed,
                    "UTF-8 BOM is not part of the canonical projection profile.");
            }

            var text = ProjectionCanonicalization.StrictUtf8.GetString(bytes);
            if (text.Contains('\r', StringComparison.Ordinal) || !text.StartsWith("---\n", StringComparison.Ordinal))
            {
                return ProjectionResults.Fail<ParsedProjectionFrontmatter>(
                    ProjectionError.ProjectionSerializationFailed,
                    "Projection frontmatter must use canonical LF delimiters.");
            }

            var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0)
            {
                return ProjectionResults.Fail<ParsedProjectionFrontmatter>(
                    ProjectionError.ProjectionSerializationFailed,
                    "Projection frontmatter closing delimiter is missing.");
            }

            Dictionary<string, string?> known = new(StringComparer.Ordinal);
            Dictionary<string, string?> compatible = new(StringComparer.Ordinal);
            List<string> warnings = [];
            foreach (var line in text[4..end].Split('\n'))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    return ProjectionResults.Fail<ParsedProjectionFrontmatter>(
                        ProjectionError.ProjectionSerializationFailed,
                        $"Invalid strict frontmatter line: {line}");
                }

                var key = line[..separator];
                var raw = line[(separator + 1)..].TrimStart();
                var value = ParseScalar(raw);
                if (KnownKeys.Contains(key))
                {
                    known.Add(key, value);
                }
                else if (key.StartsWith("x-", StringComparison.Ordinal) || key.Contains('.', StringComparison.Ordinal))
                {
                    compatible.Add(key, value);
                    warnings.Add($"Compatible unknown projection field '{key}' was preserved.");
                }
                else
                {
                    return ProjectionResults.Fail<ParsedProjectionFrontmatter>(
                        ProjectionError.ProjectionSerializationFailed,
                        $"Unknown field '{key}' is not namespaced.");
                }
            }

            return ProjectionResults.Success(new ParsedProjectionFrontmatter(
                known,
                compatible,
                warnings,
                text[(end + 5)..]));
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException or ArgumentException)
        {
            return ProjectionResults.Fail<ParsedProjectionFrontmatter>(
                ProjectionError.ProjectionSerializationFailed,
                exception.Message);
        }
    }

    private static string? ParseScalar(string raw)
    {
        if (StringComparer.Ordinal.Equals(raw, "null"))
        {
            return null;
        }

        if (raw.StartsWith('"'))
        {
            return JsonSerializer.Deserialize<string>(raw)
                ?? throw new JsonException("A quoted frontmatter scalar cannot deserialize to null.");
        }

        return raw;
    }
}
