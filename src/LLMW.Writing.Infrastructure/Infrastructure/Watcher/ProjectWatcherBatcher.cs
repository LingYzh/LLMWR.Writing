using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Application.Watcher;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Reconcile;

namespace LLMW.Writing.Infrastructure.Watcher;

/// <summary>
/// OS watcher adapter that normalizes and coalesces hints into Application events. It never scans,
/// reconciles, or writes project state; lost events and polling fallback produce an explicit
/// full-rescan-required batch for the Application to decide how to handle.
/// </summary>
public sealed class ProjectWatcherBatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly ProjectPathResolver paths;
    private readonly string projectId;
    private readonly IProjectWatcherBatchSink sink;
    private readonly FileEventCoalescer coalescer;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan debounce;
    private long sequence;
    private bool gitBatchActive;
    private bool disposed;
    private FileSystemWatcher? watcher;
    private Timer? debounceTimer;
    private Timer? pollingFallbackTimer;

    public ProjectWatcherBatcher(
        string projectId,
        ProjectPathResolver paths,
        IProjectWatcherBatchSink sink,
        TimeSpan? debounce = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        this.projectId = projectId;
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.debounce = debounce ?? DefaultDebounce;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        coalescer = new FileEventCoalescer(this.debounce, this.clock);
    }

    public int PendingEventCount => coalescer.PendingCount;

    public bool NativeWatcherAvailable => watcher is not null;

    public TimeSpan ConfiguredDebounce => debounce;

    public void Start()
    {
        ThrowIfDisposed();
        lock (gate)
        {
            if (watcher is not null || debounceTimer is not null)
            {
                return;
            }

            debounceTimer = new Timer(_ => SafeFlush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                watcher = new FileSystemWatcher(paths.ProjectRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = ProjectFileMonitor.NativeBufferSize,
                    EnableRaisingEvents = false
                };
                watcher.Created += (_, eventArgs) => OnNativeEvent(FileEventKind.Created, eventArgs.FullPath);
                watcher.Changed += (_, eventArgs) => OnNativeEvent(FileEventKind.Modified, eventArgs.FullPath);
                watcher.Deleted += (_, eventArgs) => OnNativeEvent(FileEventKind.Deleted, eventArgs.FullPath);
                watcher.Renamed += (_, eventArgs) => OnNativeEvent(FileEventKind.Renamed, eventArgs.FullPath, eventArgs.OldFullPath);
                watcher.Error += (_, _) => MarkOverflow();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception) when (!disposed)
            {
                watcher?.Dispose();
                watcher = null;
                EnsurePollingFallback();
                Enqueue(FileEventKind.RescanRequired, "project.llmw.json", null, FileEventSource.FullRescan);
            }
        }
    }

    public void BeginGitBatch()
    {
        ThrowIfDisposed();
        lock (gate)
        {
            if (gitBatchActive)
            {
                throw new InvalidOperationException("A Git watcher batch is already active.");
            }

            gitBatchActive = true;
        }
    }

    public void EndGitBatch()
    {
        ThrowIfDisposed();
        lock (gate)
        {
            if (!gitBatchActive)
            {
                throw new InvalidOperationException("No Git watcher batch is active.");
            }

            gitBatchActive = false;
        }

        FlushPending(force: true);
    }

    public void InjectNativeEvent(FileEventKind kind, string relativePath, string? oldRelativePath = null)
    {
        ThrowIfDisposed();
        var normalized = paths.NormalizeRelativePath(relativePath);
        var oldNormalized = oldRelativePath is null ? null : paths.NormalizeRelativePath(oldRelativePath);
        Enqueue(kind, normalized, oldNormalized, FileEventSource.NativeWatcher);
    }

    public void MarkOverflow()
    {
        ThrowIfDisposed();
        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
            EnsurePollingFallback();
        }

        Enqueue(FileEventKind.RescanRequired, "project.llmw.json", null, FileEventSource.FullRescan);
    }

    public bool FlushPending(bool force = false)
    {
        ThrowIfDisposed();
        lock (gate)
        {
            if (gitBatchActive)
            {
                return false;
            }
        }

        var records = coalescer.DrainReady(force);
        if (records.Count == 0)
        {
            return false;
        }

        var requiresFullRescan = records.Any(record => record.Kind == FileEventKind.RescanRequired);
        List<ProjectWatcherEvent> events = [];
        foreach (var record in records)
        {
            if (record.Kind == FileEventKind.RescanRequired)
            {
                events.Add(new ProjectWatcherEvent(ProjectWatcherEventKind.OverflowRecoveryRequired, record));
                continue;
            }

            if (IsDraft(record.RelativePath) || IsProjectConfiguration(record.RelativePath))
            {
                events.Add(new ProjectWatcherEvent(ProjectWatcherEventKind.ProjectFileChanged, record));
            }

            if (!IsRuntimePath(record.RelativePath))
            {
                events.Add(new ProjectWatcherEvent(ProjectWatcherEventKind.GitWorkspaceChanged, record));
            }
        }

        if (events.Count == 0 && !requiresFullRescan)
        {
            return false;
        }

        sink.Publish(new ProjectWatcherBatch(projectId, events, requiresFullRescan, clock()));
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
            debounceTimer?.Dispose();
            debounceTimer = null;
            pollingFallbackTimer?.Dispose();
            pollingFallbackTimer = null;
            disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void OnNativeEvent(FileEventKind kind, string fullPath, string? oldFullPath = null)
    {
        try
        {
            var relative = paths.FromFullPath(fullPath);
            var oldRelative = oldFullPath is null ? null : paths.FromFullPath(oldFullPath);
            Enqueue(kind, relative, oldRelative, FileEventSource.NativeWatcher);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or IOException or System.Security.SecurityException)
        {
            MarkOverflow();
        }
    }

    private void Enqueue(FileEventKind kind, string relativePath, string? oldRelativePath, FileEventSource source)
    {
        var inGitBatch = false;
        lock (gate)
        {
            inGitBatch = gitBatchActive;
        }

        coalescer.Enqueue(new FileEventRecord(
            Interlocked.Increment(ref sequence),
            relativePath,
            oldRelativePath,
            kind,
            null,
            inGitBatch ? FileEventSource.Batch : source,
            clock()));
        if (!inGitBatch)
        {
            debounceTimer?.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void EnsurePollingFallback()
    {
        pollingFallbackTimer ??= new Timer(_ =>
        {
            try
            {
                Enqueue(FileEventKind.RescanRequired, "project.llmw.json", null, FileEventSource.Polling);
                FlushPending(force: true);
            }
            catch (ObjectDisposedException)
            {
            }
        }, null, DefaultPollingInterval, DefaultPollingInterval);
    }

    private void SafeFlush()
    {
        try
        {
            FlushPending();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsDraft(string relativePath) =>
        relativePath.StartsWith("Draft/", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectConfiguration(string relativePath) =>
        StringComparer.OrdinalIgnoreCase.Equals(relativePath, "project.llmw.json");

    private static bool IsRuntimePath(string relativePath) =>
        relativePath.StartsWith(".llmw/", StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
