using System.Text.Json;
using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Domain.Provider;

public enum InvocationPersistDecision
{
    NewIdentity,
    IdempotentReplay,
    PersistRefinement,
    IdentityConflict,
    LifecycleConflict
}

public static class InvocationPersistClassifier
{
    public static InvocationPersistDecision Classify(
        string? historicalIdentity,
        string? incomingIdentity,
        string? historicalPayload,
        string incomingPayload)
    {
        if (string.IsNullOrWhiteSpace(historicalPayload))
        {
            return InvocationPersistDecision.NewIdentity;
        }

        if (incomingIdentity is not null &&
            historicalIdentity is not null &&
            !string.Equals(historicalIdentity, incomingIdentity, StringComparison.Ordinal))
        {
            return InvocationPersistDecision.IdentityConflict;
        }

        if (string.Equals(historicalPayload, incomingPayload, StringComparison.Ordinal) ||
            RecordFactsEquivalent(historicalPayload, incomingPayload))
        {
            return InvocationPersistDecision.IdempotentReplay;
        }

        var from = ReadLifecycle(historicalPayload);
        var to = ReadLifecycle(incomingPayload);
        if (!IsLegalRefinement(from, to) || !ProvenanceCompatible(historicalPayload, incomingPayload))
        {
            return InvocationPersistDecision.LifecycleConflict;
        }

        return InvocationPersistDecision.PersistRefinement;
    }

    public static InvocationLifecycle ReadLifecycle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !TryGetString(json, "lifecycle", out var raw) || raw is null)
        {
            return InvocationLifecycle.Prepared;
        }

        return Enum.TryParse(raw, ignoreCase: false, out InvocationLifecycle lifecycle)
            ? lifecycle
            : InvocationLifecycle.Prepared;
    }

    public static bool IsTerminal(InvocationLifecycle lifecycle) =>
        lifecycle is
            InvocationLifecycle.Completed or
            InvocationLifecycle.Incomplete or
            InvocationLifecycle.Rejected or
            InvocationLifecycle.FailedBeforeSend or
            InvocationLifecycle.FailedAfterPossibleSend or
            InvocationLifecycle.OutcomeUnknown or
            InvocationLifecycle.CancelConfirmed;

    public static bool IsLegalRefinement(InvocationLifecycle from, InvocationLifecycle to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (InvocationLifecycle.Prepared, _) => true,
            (InvocationLifecycle.Dispatching, InvocationLifecycle.Prepared) => false,
            (InvocationLifecycle.Dispatching, _) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.ProviderAccepted) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.Streaming) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.Completed) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.Incomplete) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.Rejected) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.FailedAfterPossibleSend) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.OutcomeUnknown) => true,
            (InvocationLifecycle.PossiblySent, InvocationLifecycle.CancelRequested) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.Streaming) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.Completed) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.Incomplete) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.Rejected) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.FailedAfterPossibleSend) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.OutcomeUnknown) => true,
            (InvocationLifecycle.ProviderAccepted, InvocationLifecycle.CancelRequested) => true,
            (InvocationLifecycle.Streaming, InvocationLifecycle.Completed) => true,
            (InvocationLifecycle.Streaming, InvocationLifecycle.Incomplete) => true,
            (InvocationLifecycle.Streaming, InvocationLifecycle.Rejected) => true,
            (InvocationLifecycle.Streaming, InvocationLifecycle.FailedAfterPossibleSend) => true,
            (InvocationLifecycle.Streaming, InvocationLifecycle.OutcomeUnknown) => true,
            (InvocationLifecycle.Streaming, InvocationLifecycle.CancelRequested) => true,
            (InvocationLifecycle.CancelRequested, InvocationLifecycle.CancelConfirmed) => true,
            (InvocationLifecycle.CancelRequested, InvocationLifecycle.OutcomeUnknown) => true,
            _ => false
        };
    }

    public static bool RecordFactsEquivalent(string historicalPayload, string incomingPayload)
    {
        return ReadLifecycle(historicalPayload) == ReadLifecycle(incomingPayload) &&
               CompatibleOptional(historicalPayload, incomingPayload, "providerRequestId") &&
               CompatibleOptional(incomingPayload, historicalPayload, "providerRequestId") &&
               CompatibleOptional(historicalPayload, incomingPayload, "providerResponseId") &&
               CompatibleOptional(incomingPayload, historicalPayload, "providerResponseId") &&
               CompatibleOptional(historicalPayload, incomingPayload, "providerReportedModel") &&
               CompatibleOptional(incomingPayload, historicalPayload, "providerReportedModel") &&
               CompatibleFailure(historicalPayload, incomingPayload) &&
               CompatibleFailure(incomingPayload, historicalPayload) &&
               CompatibleUsage(historicalPayload, incomingPayload) &&
               CompatibleUsage(incomingPayload, historicalPayload) &&
               EquivalentFlag(TryGetBoolean(historicalPayload, "duplicateExecutionRisk"), TryGetBoolean(incomingPayload, "duplicateExecutionRisk"));
    }

    private static bool EquivalentFlag(bool? left, bool? right) => (left ?? false) == (right ?? false);

    public static bool ProvenanceCompatible(string historicalPayload, string incomingPayload)
    {
        return CompatibleOptional(historicalPayload, incomingPayload, "providerRequestId") &&
               CompatibleOptional(historicalPayload, incomingPayload, "providerResponseId") &&
               CompatibleOptional(historicalPayload, incomingPayload, "providerReportedModel") &&
               CompatibleFailure(historicalPayload, incomingPayload) &&
               CompatibleUsage(historicalPayload, incomingPayload) &&
               CompatibleDuplicateRisk(historicalPayload, incomingPayload);
    }

    private static bool CompatibleOptional(string historical, string incoming, string name)
    {
        var had = TryGetString(historical, name, out var prior);
        var has = TryGetString(incoming, name, out var next);
        if (!had || string.IsNullOrEmpty(prior))
        {
            return true;
        }

        return has && string.Equals(prior, next, StringComparison.Ordinal);
    }

    private static bool CompatibleFailure(string historical, string incoming)
    {
        var prior = TryGetString(historical, "failureClass", out var rawPrior) ? rawPrior : "None";
        var next = TryGetString(incoming, "failureClass", out var rawNext) ? rawNext : "None";
        if (string.IsNullOrEmpty(prior) || string.Equals(prior, "None", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(prior, next, StringComparison.Ordinal);
    }

    private static bool CompatibleDuplicateRisk(string historical, string incoming)
    {
        var prior = TryGetBoolean(historical, "duplicateExecutionRisk");
        var next = TryGetBoolean(incoming, "duplicateExecutionRisk");
        if (prior is true && next is false)
        {
            return false;
        }

        return true;
    }

    private static bool CompatibleUsage(string historical, string incoming)
    {
        var prior = TryGetRaw(historical, "usage");
        var next = TryGetRaw(incoming, "usage");
        if (string.IsNullOrEmpty(prior) || IsUnknownUsage(prior))
        {
            return true;
        }

        if (string.IsNullOrEmpty(next))
        {
            return false;
        }

        return string.Equals(CanonicalizeJsonObject(prior), CanonicalizeJsonObject(next), StringComparison.Ordinal);
    }

    private static string CanonicalizeJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return CanonicalJson.Write(document.RootElement, redactSecrets: false);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool IsUnknownUsage(string json)
    {
        return json.Contains("\"status\":\"unknown\"", StringComparison.Ordinal) ||
               json.Contains("\"status\":\"Unknown\"", StringComparison.Ordinal);
    }

    private static bool TryGetString(string json, string name, out string? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(name, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? TryGetBoolean(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(name, out var property) ||
                (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
            {
                return null;
            }

            return property.GetBoolean();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetRaw(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var property) ? property.GetRawText() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
