using LLMW.Writing.Application.Reconcile;

namespace LLMW.Writing.Application.Watcher;

public enum ProjectWatcherEventKind
{
    ProjectFileChanged,
    GitWorkspaceChanged,
    OverflowRecoveryRequired
}

/// <summary>
/// A normalized path notification, not a statement of current disk truth. Consumers decide whether
/// to reconcile; the watcher never writes Project state or invokes a business mutation itself.
/// </summary>
public sealed record ProjectWatcherEvent(
    ProjectWatcherEventKind Kind,
    FileEventRecord FileEvent);

public sealed record ProjectWatcherBatch(
    string ProjectId,
    IReadOnlyList<ProjectWatcherEvent> Events,
    bool RequiresFullRescan,
    DateTimeOffset PublishedAt);

public interface IProjectWatcherBatchSink
{
    void Publish(ProjectWatcherBatch batch);
}

/// <summary>
/// Explicit Application hand-off point. Registrations are read/notification handlers only; they
/// decide whether later reconcile work is appropriate for the batch.
/// </summary>
public sealed class ProjectWatcherBatchDispatcher : IProjectWatcherBatchSink
{
    private readonly object gate = new();
    private readonly List<Action<ProjectWatcherBatch>> subscribers = [];

    public IDisposable Subscribe(Action<ProjectWatcherBatch> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        lock (gate)
        {
            subscribers.Add(subscriber);
        }

        return new Subscription(this, subscriber);
    }

    public void Publish(ProjectWatcherBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Action<ProjectWatcherBatch>[] handlers;
        lock (gate)
        {
            handlers = subscribers.ToArray();
        }

        foreach (var handler in handlers)
        {
            handler(batch);
        }
    }

    private void Unsubscribe(Action<ProjectWatcherBatch> subscriber)
    {
        lock (gate)
        {
            subscribers.Remove(subscriber);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private ProjectWatcherBatchDispatcher? owner;
        private readonly Action<ProjectWatcherBatch> subscriber;

        public Subscription(ProjectWatcherBatchDispatcher owner, Action<ProjectWatcherBatch> subscriber)
        {
            this.owner = owner;
            this.subscriber = subscriber;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Unsubscribe(subscriber);
        }
    }
}
