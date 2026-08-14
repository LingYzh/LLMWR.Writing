namespace LLMW.Writing.Application.Registry;

public enum RegistryQueryError
{
    RegistryEntryNotFound,
    RegistryUnavailable,
    RegistryStale,
    RegistryNotTrusted,
    SearchIndexDirty,
    SearchIndexUnavailable,
    SearchQueryInvalid
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

public sealed record SearchNarrativeQuery(string Text, int Limit = 20);

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

    public SearchNarrativeService(INarrativeSearchStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public RegistryQueryResult<IReadOnlyList<NarrativeSearchHit>> Search(
        SearchNarrativeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Length > MaximumQueryLength ||
            query.Limit is < 1 or > MaximumResultLimit)
        {
            return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                RegistryQueryError.SearchQueryInvalid);
        }

        return store.Search(query with { Text = query.Text.Normalize() }, cancellationToken);
    }
}
