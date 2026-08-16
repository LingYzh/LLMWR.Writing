using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Provider;

public enum UsageStatus
{
    Unknown,
    Partial,
    Reported
}

public sealed record OptionalTokenCount(long? Value, UsageStatus Status)
{
    public static OptionalTokenCount Unknown { get; } = new(null, UsageStatus.Unknown);

    public static OptionalTokenCount Reported(long value) => new(value, UsageStatus.Reported);

    public static OptionalTokenCount Partial(long? value) => new(value, UsageStatus.Partial);

    public static bool IsZeroSynthesized => false;
}

public sealed record NormalizedUsage(
    UsageStatus Status,
    OptionalTokenCount InputTokens,
    OptionalTokenCount CachedInputReadTokens,
    OptionalTokenCount CacheWriteTokens,
    OptionalTokenCount OutputTokens,
    OptionalTokenCount ReasoningTokens,
    IReadOnlyDictionary<string, long> ProviderSpecificBillableUnits,
    string? RawProviderUsageCanonical)
{
    public static NormalizedUsage Unknown { get; } = new(
        UsageStatus.Unknown,
        OptionalTokenCount.Unknown,
        OptionalTokenCount.Unknown,
        OptionalTokenCount.Unknown,
        OptionalTokenCount.Unknown,
        OptionalTokenCount.Unknown,
        new Dictionary<string, long>(),
        null);

    public static NormalizedUsage MissingIsNotZero() => Unknown;

    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", Status.ToString());
            WriteToken(writer, "inputTokens", InputTokens);
            WriteToken(writer, "cachedInputReadTokens", CachedInputReadTokens);
            WriteToken(writer, "cacheWriteTokens", CacheWriteTokens);
            WriteToken(writer, "outputTokens", OutputTokens);
            WriteToken(writer, "reasoningTokens", ReasoningTokens);
            writer.WritePropertyName("providerSpecificBillableUnits");
            writer.WriteStartObject();
            foreach (var pair in ProviderSpecificBillableUnits.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            if (!string.IsNullOrEmpty(RawProviderUsageCanonical))
            {
                writer.WriteString("rawProviderUsageCanonical", RawProviderUsageCanonical);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteToken(Utf8JsonWriter writer, string name, OptionalTokenCount token)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("status", token.Status.ToString());
        if (token.Value is long value)
        {
            writer.WriteNumber("value", value);
        }
        else
        {
            writer.WriteNull("value");
        }

        writer.WriteEndObject();
    }
}

public sealed record PriceComponent(string Name, string Currency, decimal? AmountPerMillion, MetadataProvenance Provenance);

public sealed record PriceSnapshot(
    string PriceSnapshotId,
    string Currency,
    IReadOnlyList<PriceComponent> Components,
    string Source,
    long EffectiveAtMs)
{
    public string Digest => Utf8Digest.Sha256Hex(CanonicalJson());

    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("priceSnapshotId", PriceSnapshotId);
            writer.WriteString("currency", Currency);
            writer.WriteString("source", Source);
            writer.WriteNumber("effectiveAtMs", EffectiveAtMs);
            writer.WritePropertyName("components");
            writer.WriteStartArray();
            foreach (var component in Components.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", component.Name);
                writer.WriteString("currency", component.Currency);
                if (component.AmountPerMillion is decimal amount)
                {
                    writer.WriteNumber("amountPerMillion", amount);
                }
                else
                {
                    writer.WriteNull("amountPerMillion");
                }

                writer.WriteString("provenance", component.Provenance.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public enum CostKind
{
    PreflightEstimate,
    PostInvocationEstimate,
    CalculatedFromReportedUsage,
    Unknown
}

public sealed record CostEstimate(
    CostKind Kind,
    string Currency,
    decimal? Amount,
    string? PriceSnapshotId,
    string EstimatorIdentity,
    bool InvoiceTruth)
{
    public static CostEstimate Unknown(string? priceSnapshotId) =>
        new(CostKind.Unknown, "USD", null, priceSnapshotId, "none", false);
}

public sealed record EstimatedTokenCount(int? Tokens, string EstimatorIdentity, string Confidence)
{
    public static EstimatedTokenCount Char4(int characters) =>
        new(characters / 4, "llmw-char4-v1", "low");
}
