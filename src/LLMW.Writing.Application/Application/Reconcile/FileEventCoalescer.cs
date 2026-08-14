namespace LLMW.Writing.Application.Reconcile;

public sealed class FileEventCoalescer
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private readonly object sync = new();
    private readonly TimeSpan debounce;
    private readonly Func<DateTimeOffset> clock;
    private readonly List<FileEventRecord> pending = [];

    public FileEventCoalescer(
        TimeSpan? debounce = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.debounce = debounce ?? DefaultDebounce;
        if (this.debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int PendingCount
    {
        get
        {
            lock (sync)
            {
                return pending.Count;
            }
        }
    }

    public void Enqueue(FileEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (sync)
        {
            pending.Add(record);
        }
    }

    public IReadOnlyList<FileEventRecord> DrainReady(bool force = false)
    {
        List<FileEventRecord> ready;
        lock (sync)
        {
            var cutoff = clock() - debounce;
            ready = pending
                .Where(item => force || item.ObservedAt <= cutoff || item.Kind == FileEventKind.RescanRequired)
                .OrderBy(item => item.Sequence)
                .ToList();
            if (ready.Count == 0)
            {
                return [];
            }

            var sequences = ready.Select(item => item.Sequence).ToHashSet();
            pending.RemoveAll(item => sequences.Contains(item.Sequence));
        }

        return Coalesce(ready);
    }

    public static IReadOnlyList<FileEventRecord> Coalesce(IEnumerable<FileEventRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ordered = records.OrderBy(item => item.Sequence).ToArray();
        List<FileEventRecord> result = [];
        foreach (var rescan in ordered.Where(item => item.Kind == FileEventKind.RescanRequired))
        {
            result.Add(rescan);
        }

        foreach (var group in ordered
                     .Where(item => item.Kind != FileEventKind.RescanRequired)
                     .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Min(item => item.Sequence)))
        {
            var events = group.OrderBy(item => item.Sequence).ToArray();
            var first = events[0];
            var last = events[^1];
            var sawDelete = events.Any(item => item.Kind == FileEventKind.Deleted);
            var sawCreateAfterDelete = events
                .Select((item, index) => (item, index))
                .Any(pair => pair.item.Kind == FileEventKind.Created &&
                             events.Take(pair.index).Any(item => item.Kind == FileEventKind.Deleted));
            var rename = events.LastOrDefault(item => item.Kind == FileEventKind.Renamed);
            var kind = rename is not null && events.Length == 1
                ? FileEventKind.Renamed
                : sawDelete && sawCreateAfterDelete
                    ? FileEventKind.Modified
                    : last.Kind;
            result.Add(new FileEventRecord(
                first.Sequence,
                last.RelativePath,
                kind == FileEventKind.Renamed ? rename?.OldRelativePath : null,
                kind,
                last.ObservedDigest,
                events.Any(item => item.Source == FileEventSource.Batch) ? FileEventSource.Batch : last.Source,
                last.ObservedAt,
                last.SelfWriteOperationToken ?? events.Select(item => item.SelfWriteOperationToken).LastOrDefault(value => value is not null)));
        }

        return result.OrderBy(item => item.Sequence).ToArray();
    }
}
