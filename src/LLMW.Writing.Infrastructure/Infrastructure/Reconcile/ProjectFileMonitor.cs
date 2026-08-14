using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Infrastructure.FileSystem;

namespace LLMW.Writing.Infrastructure.Reconcile;

public sealed class ProjectFileMonitor : IDisposable
{
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(5);
    public const int NativeBufferSize = 64 * 1024;

    private readonly object sync = new();
    private readonly ProjectReconcileEngine engine;
    private readonly ISelfWriteTracker selfWriteTracker;
    private readonly FileEventCoalescer coalescer;
    private readonly NativeWatchSurfacePolicy watchSurfacePolicy;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan pollingInterval;
    private readonly TimeSpan configuredDebounce;
    private readonly Dictionary<string, FileSystemWatcher> nativeWatchers =
        new(StringComparer.OrdinalIgnoreCase);
    private Timer? pollingTimer;
    private Timer? debounceTimer;
    private long sequence;
    private bool disposed;
    private bool batchActive;
    private string? batchKind;
    private bool eventReliabilityUnknown;

    public ProjectFileMonitor(
        ProjectReconcileEngine engine,
        ISelfWriteTracker selfWriteTracker,
        TimeSpan? pollingInterval = null,
        TimeSpan? debounce = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.selfWriteTracker = selfWriteTracker ?? throw new ArgumentNullException(nameof(selfWriteTracker));
        this.pollingInterval = pollingInterval ?? DefaultPollingInterval;
        configuredDebounce = debounce ?? FileEventCoalescer.DefaultDebounce;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        coalescer = new FileEventCoalescer(configuredDebounce, this.clock);
        watchSurfacePolicy = new NativeWatchSurfacePolicy(engine.PathResolver);
    }

    public bool NativeWatcherAvailable { get; private set; }

    public long HeavyReconcilePassCount { get; private set; }

    public TimeSpan ConfiguredDebounce => configuredDebounce;

    public TimeSpan? LastScheduledDebounce { get; private set; }

    public IReadOnlyList<string> NativeWatchRoots
    {
        get
        {
            lock (sync)
            {
                return nativeWatchers.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public ReconcileScanReport? LastReport { get; private set; }

    public int PendingEventCount => coalescer.PendingCount;

    public void Start()
    {
        ThrowIfDisposed();
        lock (sync)
        {
            if (pollingTimer is not null)
            {
                return;
            }

            RefreshNativeWatchers();
            pollingTimer = new Timer(
                _ => SafePoll(),
                null,
                pollingInterval,
                pollingInterval);
            debounceTimer = new Timer(
                _ => SafeFlush(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
    }

    public ReconcileScanReport StartupScan(
        int batchSize = ProjectReconcileEngine.DefaultScanBatchSize,
        CancellationToken cancellationToken = default) =>
        RunScan(FileEventSource.StartupScan, batchSize, cancellationToken);

    public ReconcileScanReport PollOnce(
        int batchSize = ProjectReconcileEngine.DefaultScanBatchSize,
        CancellationToken cancellationToken = default) =>
        RunScan(FileEventSource.Polling, batchSize, cancellationToken);

    public ReconcileResult<bool> BeginExternalBatch(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ThrowIfDisposed();
        lock (sync)
        {
            if (batchActive)
            {
                return ReconcileResults.Fail<bool>(ReconcileError.BatchAlreadyActive, batchKind);
            }

            batchActive = true;
            batchKind = kind;
            return ReconcileResults.Success(true);
        }
    }

    public ReconcileResult<ReconcileScanReport> EndExternalBatch(
        string kind,
        int batchSize = ProjectReconcileEngine.DefaultScanBatchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ThrowIfDisposed();
        lock (sync)
        {
            if (!batchActive)
            {
                return ReconcileResults.Fail<ReconcileScanReport>(ReconcileError.NoActiveBatch);
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(batchKind, kind))
            {
                return ReconcileResults.Fail<ReconcileScanReport>(
                    ReconcileError.NoActiveBatch,
                    $"Active batch is '{batchKind}', not '{kind}'.");
            }

            batchActive = false;
            batchKind = null;
        }

        var events = coalescer.DrainReady(force: true);
        var report = RunScan(
            eventReliabilityUnknown ? FileEventSource.FullRescan : FileEventSource.Batch,
            batchSize,
            cancellationToken,
            eventReliabilityUnknown ? null : events);
        eventReliabilityUnknown = false;
        return ReconcileResults.Success(report);
    }

    public void InjectNativeEvent(
        FileEventKind kind,
        string relativePath,
        string? oldRelativePath = null,
        string? observedDigest = null)
    {
        ThrowIfDisposed();
        var normalized = engine.PathResolver.NormalizeRelativePath(relativePath);
        if (!watchSurfacePolicy.IsRelevantRelativePath(normalized))
        {
            return;
        }

        var oldNormalized = oldRelativePath is null
            ? null
            : engine.PathResolver.NormalizeRelativePath(oldRelativePath);
        Enqueue(kind, normalized, oldNormalized, observedDigest);
    }

    public void MarkNativeWatcherUnreliable()
    {
        eventReliabilityUnknown = true;
        Enqueue(FileEventKind.RescanRequired, "Narrative", null, null);
    }

    public ReconcileResult<ReconcileScanReport?> FlushPending(
        bool force = false,
        int batchSize = ProjectReconcileEngine.DefaultScanBatchSize,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (sync)
        {
            if (batchActive)
            {
                return ReconcileResults.Success<ReconcileScanReport?>(null);
            }
        }

        var events = coalescer.DrainReady(force);
        if (events.Count == 0)
        {
            return ReconcileResults.Success<ReconcileScanReport?>(null);
        }

        var full = eventReliabilityUnknown || events.Any(item => item.Kind == FileEventKind.RescanRequired);
        var report = RunScan(
            full ? FileEventSource.FullRescan : FileEventSource.NativeWatcher,
            batchSize,
            cancellationToken,
            full ? null : events);
        eventReliabilityUnknown = false;
        return ReconcileResults.Success<ReconcileScanReport?>(report);
    }

    public void Stop()
    {
        lock (sync)
        {
            foreach (var watcher in nativeWatchers.Values)
            {
                watcher.Dispose();
            }

            nativeWatchers.Clear();
            pollingTimer?.Dispose();
            pollingTimer = null;
            debounceTimer?.Dispose();
            debounceTimer = null;
            NativeWatcherAvailable = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void RefreshNativeWatchers()
    {
        lock (sync)
        {
            var desiredRoots = watchSurfacePolicy.ExistingWatchRoots()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleRoot in nativeWatchers.Keys
                         .Where(root => !desiredRoots.Contains(root))
                         .ToArray())
            {
                nativeWatchers[staleRoot].Dispose();
                nativeWatchers.Remove(staleRoot);
            }

            foreach (var root in desiredRoots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (nativeWatchers.ContainsKey(root))
                {
                    continue;
                }

                try
                {
                    var watcher = CreateWatcher(root);
                    nativeWatchers.Add(root, watcher);
                }
                catch (Exception) when (!disposed)
                {
                    eventReliabilityUnknown = true;
                }
            }

            NativeWatcherAvailable = desiredRoots.Count > 0 && nativeWatchers.Count == desiredRoots.Count;
        }
    }

    private FileSystemWatcher CreateWatcher(string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = NativeBufferSize,
            EnableRaisingEvents = false
        };
        watcher.Created += (_, args) => OnNativePath(FileEventKind.Created, args.FullPath);
        watcher.Changed += (_, args) => OnNativePath(FileEventKind.Modified, args.FullPath);
        watcher.Deleted += (_, args) => OnNativePath(FileEventKind.Deleted, args.FullPath);
        watcher.Renamed += (_, args) => OnNativePath(FileEventKind.Renamed, args.FullPath, args.OldFullPath);
        watcher.Error += (_, _) => OnNativeWatcherError(root);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnNativeWatcherError(string root)
    {
        lock (sync)
        {
            if (nativeWatchers.Remove(root, out var watcher))
            {
                watcher.Dispose();
            }

            NativeWatcherAvailable = false;
        }

        MarkNativeWatcherUnreliable();
    }

    private void OnNativePath(FileEventKind kind, string fullPath, string? oldFullPath = null)
    {
        try
        {
            var relative = engine.PathResolver.FromFullPath(fullPath);
            if (!watchSurfacePolicy.IsRelevantRelativePath(relative))
            {
                return;
            }

            var oldRelative = oldFullPath is null
                ? null
                : engine.PathResolver.FromFullPath(oldFullPath);
            Enqueue(kind, relative, oldRelative, null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or
                                          IOException or System.Security.SecurityException)
        {
            MarkNativeWatcherUnreliable();
        }
    }

    private void Enqueue(
        FileEventKind kind,
        string relativePath,
        string? oldRelativePath,
        string? observedDigest)
    {
        bool inBatch;
        lock (sync)
        {
            inBatch = batchActive;
        }

        coalescer.Enqueue(new FileEventRecord(
            Interlocked.Increment(ref sequence),
            relativePath,
            oldRelativePath,
            kind,
            observedDigest,
            inBatch ? FileEventSource.Batch : FileEventSource.NativeWatcher,
            clock(),
            selfWriteTracker.TryGetActiveToken(relativePath)));
        if (!inBatch)
        {
            var timer = debounceTimer;
            if (timer is not null)
            {
                LastScheduledDebounce = configuredDebounce;
                timer.Change(LastScheduledDebounce.Value, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private ReconcileScanReport RunScan(
        FileEventSource source,
        int batchSize,
        CancellationToken cancellationToken,
        IReadOnlyList<FileEventRecord>? eventRecords = null)
    {
        var report = engine.Scan(source, batchSize, eventRecords, cancellationToken);
        LastReport = report;
        HeavyReconcilePassCount++;
        if (pollingTimer is not null)
        {
            RefreshNativeWatchers();
        }

        return report;
    }

    private void SafePoll()
    {
        try
        {
            PollOnce();
        }
        catch (Exception)
        {
            eventReliabilityUnknown = true;
        }
    }

    private void SafeFlush()
    {
        try
        {
            FlushPending();
        }
        catch (Exception)
        {
            eventReliabilityUnknown = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
