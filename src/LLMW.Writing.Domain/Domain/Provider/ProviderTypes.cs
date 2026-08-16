using System.Globalization;
using System.Text;

namespace LLMW.Writing.Domain.Provider;

public readonly record struct ProviderDefinitionId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ProviderRevision(int Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct ModelId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct CredentialRef(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct InvocationId(string Value)
{
    public override string ToString() => Value;
}

public enum ProtocolKind
{
    OpenAiResponses,
    AnthropicMessages,
    OpenAiCompatibleChatCompletions
}

public static class ProtocolKindCodec
{
    public static string ToDurableValue(ProtocolKind kind) => kind switch
    {
        ProtocolKind.OpenAiResponses => "openai_responses",
        ProtocolKind.AnthropicMessages => "anthropic_messages",
        ProtocolKind.OpenAiCompatibleChatCompletions => "openai_compatible_chat",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParse(string? value, out ProtocolKind kind)
    {
        kind = value switch
        {
            "openai_responses" => ProtocolKind.OpenAiResponses,
            "anthropic_messages" => ProtocolKind.AnthropicMessages,
            "openai_compatible_chat" => ProtocolKind.OpenAiCompatibleChatCompletions,
            _ => default
        };
        return value is "openai_responses" or "anthropic_messages" or "openai_compatible_chat";
    }
}

public enum ProviderDataBehavior
{
    Unknown,
    StatelessClientManaged,
    ProviderStored,
    ProviderBackgroundState
}

public static class ProviderDataBehaviorCodec
{
    public static string ToDurableValue(ProviderDataBehavior value) => value switch
    {
        ProviderDataBehavior.Unknown => "unknown",
        ProviderDataBehavior.StatelessClientManaged => "stateless_client_managed",
        ProviderDataBehavior.ProviderStored => "provider_stored",
        ProviderDataBehavior.ProviderBackgroundState => "provider_background_state",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}

public enum MetadataProvenance
{
    Unknown,
    UserConfigured,
    ProviderReported,
    BuiltInMetadata,
    CertifiedObserved,
    Derived
}

public enum CapabilitySupport
{
    Unknown,
    NotTested,
    Supported,
    Unsupported,
    ProbeFailed
}

public static class CapabilitySupportCodec
{
    public static bool IsUsableAsSupported(CapabilitySupport value) => value == CapabilitySupport.Supported;
}

public enum ReasoningCeiling
{
    Conservative,
    Guarded,
    Adaptive
}

public static class ReasoningCeilingCodec
{
    public static string ToDurableValue(ReasoningCeiling value) => value switch
    {
        ReasoningCeiling.Conservative => "conservative",
        ReasoningCeiling.Guarded => "guarded",
        ReasoningCeiling.Adaptive => "adaptive",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool CanDowngradeTo(ReasoningCeiling ceiling, ReasoningCeiling requested) =>
        Rank(requested) <= Rank(ceiling);

    public static ReasoningCeiling Downgrade(ReasoningCeiling ceiling, ReasoningCeiling requested) =>
        Rank(requested) <= Rank(ceiling) ? requested : ceiling;

    private static int Rank(ReasoningCeiling value) => value switch
    {
        ReasoningCeiling.Conservative => 0,
        ReasoningCeiling.Guarded => 1,
        ReasoningCeiling.Adaptive => 2,
        _ => 0
    };
}

public sealed record ProviderEndpoint
{
    public const string HttpsScheme = "https";
    public const string HttpScheme = "http";

    private ProviderEndpoint(string canonical, bool insecureLocal)
    {
        CanonicalUri = canonical;
        InsecureLocalHttp = insecureLocal;
    }

    public string CanonicalUri { get; }

    public bool InsecureLocalHttp { get; }

    public static ProviderEndpoint? TryCreate(string? raw, bool allowInsecureLocalHttp, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "endpoint-missing";
            return null;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            error = "endpoint-malformed";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "endpoint-userinfo-forbidden";
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != HttpsScheme && scheme != HttpScheme)
        {
            error = "endpoint-scheme-rejected";
            return null;
        }

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "endpoint-host-missing";
            return null;
        }

        var isLoopback = uri.IsLoopback ||
                         string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                         host is "127.0.0.1" or "::1";
        if (scheme == HttpScheme)
        {
            if (!allowInsecureLocalHttp || !isLoopback)
            {
                error = "endpoint-https-required";
                return null;
            }
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Host = host,
            UserName = "",
            Password = "",
            Fragment = ""
        };
        if ((scheme == HttpsScheme && uri.IsDefaultPort) || (scheme == HttpScheme && uri.IsDefaultPort))
        {
            builder.Port = -1;
        }

        var path = builder.Path;
        if (path.Length > 1)
        {
            builder.Path = path.TrimEnd('/');
        }

        return new ProviderEndpoint(builder.Uri.AbsoluteUri, scheme == HttpScheme);
    }
}

public sealed record ModelCatalogEntry(
    ModelId ModelId,
    string DisplayName,
    ProviderDefinitionId ProviderDefinitionId,
    int? DeclaredContextLimit,
    int? DeclaredMaxOutput,
    MetadataProvenance ContextLimitProvenance,
    MetadataProvenance MaxOutputProvenance,
    MetadataProvenance CapabilityProvenance,
    string DiscoverySource,
    long? ObservedAtMs);

public static class ProviderIdentity
{
    public static bool Same(ProviderDefinitionId left, ProviderDefinitionId right) =>
        string.Equals(left.Value, right.Value, StringComparison.Ordinal);

    public static string StableTieBreak(ProviderDefinitionId provider, ModelId model) =>
        provider.Value + "\u001f" + model.Value;
}

public static class Utf8Digest
{
    public static string Sha256Hex(string material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    public static string ArchitectureCanonical(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
    }
}
