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
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan pollingInterval;
    private FileSystemWatcher? nativeWatcher;
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
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        coalescer = new FileEventCoalescer(debounce, this.clock);
    }

    public bool NativeWatcherAvailable { get; private set; }

    public long HeavyReconcilePassCount { get; private set; }

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

            TryStartNativeWatcher();
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

        coalescer.DrainReady(force: true);
        var report = RunScan(
            eventReliabilityUnknown ? FileEventSource.FullRescan : FileEventSource.Batch,
            batchSize,
            cancellationToken);
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
        var normalized = engine.PathResolver.NormalizeRelativePath(relativePath, rejectReparsePoints: false);
        var oldNormalized = oldRelativePath is null
            ? null
            : engine.PathResolver.NormalizeRelativePath(oldRelativePath, rejectReparsePoints: false);
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
            cancellationToken);
        eventReliabilityUnknown = false;
        return ReconcileResults.Success<ReconcileScanReport?>(report);
    }

    public void Stop()
    {
        lock (sync)
        {
            nativeWatcher?.Dispose();
            nativeWatcher = null;
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

    private void TryStartNativeWatcher()
    {
        try
        {
            var watcher = new FileSystemWatcher(engine.PathResolver.ProjectRoot)
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
            watcher.Error += (_, _) => MarkNativeWatcherUnreliable();
            watcher.EnableRaisingEvents = true;
            nativeWatcher = watcher;
            NativeWatcherAvailable = true;
        }
        catch (Exception) when (!disposed)
        {
            nativeWatcher?.Dispose();
            nativeWatcher = null;
            NativeWatcherAvailable = false;
            eventReliabilityUnknown = true;
        }
    }

    private void OnNativePath(FileEventKind kind, string fullPath, string? oldFullPath = null)
    {
        try
        {
            var relative = engine.PathResolver.FromFullPath(fullPath, rejectReparsePoints: false);
            var oldRelative = oldFullPath is null
                ? null
                : engine.PathResolver.FromFullPath(oldFullPath, rejectReparsePoints: false);
            Enqueue(kind, relative, oldRelative, null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            eventReliabilityUnknown = true;
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
            debounceTimer?.Change(FileEventCoalescer.DefaultDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private ReconcileScanReport RunScan(
        FileEventSource source,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var report = engine.Scan(source, batchSize, cancellationToken);
        LastReport = report;
        HeavyReconcilePassCount++;
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
