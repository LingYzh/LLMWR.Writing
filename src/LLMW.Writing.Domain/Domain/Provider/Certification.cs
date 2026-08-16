using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Provider;

public enum CertificationState
{
    Unknown,
    DeclaredOnly,
    Certified,
    Partial,
    Failed,
    Stale,
    Uncertified
}

public static class CertificationStateCodec
{
    public static string ToDurableValue(CertificationState value) => value switch
    {
        CertificationState.Unknown => "unknown",
        CertificationState.DeclaredOnly => "declared_only",
        CertificationState.Certified => "certified",
        CertificationState.Partial => "partial",
        CertificationState.Failed => "failed",
        CertificationState.Stale => "stale",
        CertificationState.Uncertified => "uncertified",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}

public sealed record CertifiedCapability(
    string Name,
    CapabilitySupport Support,
    MetadataProvenance Provenance);

public sealed record ModelCertificationRecord(
    string CertificationId,
    int CertificationVersion,
    string ProbeSuiteVersion,
    ProviderDefinitionId ProviderDefinitionId,
    ProviderRevision ProviderRevision,
    string EndpointIdentity,
    string ProtocolAdapterId,
    string ProtocolAdapterVersion,
    ModelId ModelId,
    CertificationState State,
    ReasoningCeiling Ceiling,
    ProviderDataBehavior DataBehavior,
    IReadOnlyList<CertifiedCapability> Capabilities,
    long CertifiedAtMs,
    string? PromptBaselineDigest)
{
    public const string CurrentProbeSuiteVersion = "wp14-probe-v1";

    public CapabilitySupport SupportFor(string capabilityName)
    {
        foreach (var capability in Capabilities)
        {
            if (string.Equals(capability.Name, capabilityName, StringComparison.Ordinal))
            {
                return capability.Support;
            }
        }

        return CapabilitySupport.Unknown;
    }

    public bool IsStaleFor(
        ProviderRevision currentRevision,
        string currentEndpointIdentity,
        string currentAdapterId,
        string currentAdapterVersion) =>
        State is CertificationState.Stale or CertificationState.Failed ||
        currentRevision != ProviderRevision ||
        !string.Equals(currentEndpointIdentity, EndpointIdentity, StringComparison.Ordinal) ||
        !string.Equals(currentAdapterId, ProtocolAdapterId, StringComparison.Ordinal) ||
        !string.Equals(currentAdapterVersion, ProtocolAdapterVersion, StringComparison.Ordinal);

    public static ModelCertificationRecord Uncertified(
        ProviderDefinitionId provider,
        ProviderRevision revision,
        string endpointIdentity,
        string adapterId,
        string adapterVersion,
        ModelId model) =>
        new(
            "uncertified:" + provider.Value + ":" + model.Value,
            0,
            CurrentProbeSuiteVersion,
            provider,
            revision,
            endpointIdentity,
            adapterId,
            adapterVersion,
            model,
            CertificationState.Uncertified,
            ReasoningCeiling.Conservative,
            ProviderDataBehavior.Unknown,
            [],
            0,
            null);

    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("certificationId", CertificationId);
            writer.WriteNumber("certificationVersion", CertificationVersion);
            writer.WriteString("probeSuiteVersion", ProbeSuiteVersion);
            writer.WriteString("providerDefinitionId", ProviderDefinitionId.Value);
            writer.WriteNumber("providerRevision", ProviderRevision.Value);
            writer.WriteString("endpointIdentity", EndpointIdentity);
            writer.WriteString("protocolAdapterId", ProtocolAdapterId);
            writer.WriteString("protocolAdapterVersion", ProtocolAdapterVersion);
            writer.WriteString("modelId", ModelId.Value);
            writer.WriteString("state", CertificationStateCodec.ToDurableValue(State));
            writer.WriteString("ceiling", ReasoningCeilingCodec.ToDurableValue(Ceiling));
            writer.WriteString("dataBehavior", ProviderDataBehaviorCodec.ToDurableValue(DataBehavior));
            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (var capability in Capabilities.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", capability.Name);
                writer.WriteString("support", capability.Support.ToString());
                writer.WriteString("provenance", capability.Provenance.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("certifiedAtMs", CertifiedAtMs);
            if (!string.IsNullOrEmpty(PromptBaselineDigest))
            {
                writer.WriteString("promptBaselineDigest", PromptBaselineDigest);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class ModelCapabilityNames
{
    public const string BasicText = "basic_text";
    public const string Streaming = "streaming";
    public const string InstructionHierarchy = "instruction_hierarchy";
    public const string ToolCalling = "tool_calling";
    public const string ParallelToolCalls = "parallel_tool_calls";
    public const string StructuredJson = "structured_json";
    public const string ReasoningControl = "reasoning_control";
    public const string UsageReporting = "usage_reporting";
    public const string CacheUsageReporting = "cache_usage_reporting";
    public const string ProviderHostedTools = "provider_hosted_tools";
}
