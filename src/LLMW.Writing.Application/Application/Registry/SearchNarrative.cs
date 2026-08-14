using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Registry;

public enum RegistryQueryError
{
    RegistryEntryNotFound,
    RegistryUnavailable,
    RegistryStale,
    RegistryNotTrusted,
    SearchIndexDirty,
    SearchIndexUnavailable,
    SearchQueryInvalid,
    InvalidPrincipal,
    CapabilityDenied,
    ApprovalRequired
}

public sealed record RegistryQueryFailure(RegistryQueryError Code, string? Detail = null);

public sealed record RegistryQueryResult<T>(T? Value, RegistryQueryFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class RegistryQueryResults
{
    public static RegistryQueryResult<T> Success<T>(T value) => new(value, null);

    public static RegistryQueryResult<T> Fail<T>(RegistryQueryError code, string? detail = null) =>
        new(default, new RegistryQueryFailure(code, detail));
}

public sealed record SearchNarrativeQuery(
    string Text,
    int Limit = 20,
    CallerPrincipal? Principal = null);

public sealed record NarrativeSearchHit(
    string ObjectId,
    string ArtifactDigest,
    string SectionKey,
    string? Title,
    string Body,
    string CurrentStatus,
    double Rank);

public interface INarrativeSearchStore
{
    RegistryQueryResult<IReadOnlyList<NarrativeSearchHit>> Search(
        SearchNarrativeQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class SearchNarrativeService
{
    private const int MaximumQueryLength = 512;
    private const int MaximumResultLimit = 100;
    private readonly INarrativeSearchStore store;
    private readonly IAuthorizationService authorizationService;

    public SearchNarrativeService(INarrativeSearchStore store, IAuthorizationService? authorizationService = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.authorizationService = authorizationService ?? DenyAllAuthorizationService.Instance;
    }

    public RegistryQueryResult<IReadOnlyList<NarrativeSearchHit>> Search(
        SearchNarrativeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var decision = authorizationService.Authorize(
            query.Principal,
            new AuthorizationRequest(Capability.RegistryQuery));
        if (decision.Decision != CapabilityDecisionKind.Allowed)
        {
            var code = query.Principal is null
                ? RegistryQueryError.InvalidPrincipal
                : decision.Decision == CapabilityDecisionKind.RequiresApproval
                    ? RegistryQueryError.ApprovalRequired
                    : RegistryQueryError.CapabilityDenied;
            return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                code,
                string.Join(',', decision.Reasons));
        }

        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Length > MaximumQueryLength ||
            query.Limit is < 1 or > MaximumResultLimit)
        {
            return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                RegistryQueryError.SearchQueryInvalid);
        }

        return store.Search(query with { Text = query.Text.Normalize() }, cancellationToken);
    }
}
