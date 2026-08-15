using System.Collections.Concurrent;
using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Infrastructure.Sandbox;

namespace LLMW.Writing.Core;

internal sealed class CoreRunWorkerSupervisor : IRunWorkerSupervisor
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, LiveEntry> workers = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedIds = new(StringComparer.Ordinal);
    private readonly ITrustedSandboxBroker broker;
    private readonly ISandboxHost sandboxHost;
    private readonly ITrustedIpcBindingRegistry bindings;
    private readonly IpcEventRing eventRing;
    private readonly string workspaceInstanceId;
    private readonly string workerExecutablePath;
    private readonly CallerPrincipal launchPrincipal;
    private readonly ProjectScope projectScope;
    private readonly RunSessionService sessions;
    private int sequence;

    public CoreRunWorkerSupervisor(
        ITrustedSandboxBroker broker,
        ISandboxHost sandboxHost,
        ITrustedIpcBindingRegistry bindings,
        IpcEventRing eventRing,
        string workspaceInstanceId,
        string workerExecutablePath,
        CallerPrincipal launchPrincipal,
        ProjectScope projectScope,
        RunSessionService sessions)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.sandboxHost = sandboxHost ?? throw new ArgumentNullException(nameof(sandboxHost));
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        this.eventRing = eventRing ?? throw new ArgumentNullException(nameof(eventRing));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        this.workspaceInstanceId = workspaceInstanceId;
        this.workerExecutablePath = workerExecutablePath;
        this.launchPrincipal = launchPrincipal ?? throw new ArgumentNullException(nameof(launchPrincipal));
        this.projectScope = projectScope ?? throw new ArgumentNullException(nameof(projectScope));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public WorkerLaunchResult Launch(WorkerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            sequence++;
            var workerId = "worker-" + Guid.NewGuid().ToString("N");
            var bindingId = sequence.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            var channelId = "channel-" + Guid.NewGuid().ToString("N");
            if (!usedIds.Add(workerId) || !usedIds.Add(bindingId))
            {
                throw new InvalidOperationException("Worker identity reuse is forbidden.");
            }

            bindings.Register(new TrustedIpcLaunchRecord(
                AuthenticatedClientKind.Worker,
                workerId,
                channelId,
                projectScope,
                bindingId,
                request.RunId));

            var bootstrap = IpcBootstrapToken.Create();
            var pipeName = IpcPipeNames.Worker(workspaceInstanceId, bindingId);
            var overlay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LLMW_WORKER_BOOTSTRAP_TOKEN"] = bootstrap,
                ["LLMW_WORKSPACE_INSTANCE_ID"] = workspaceInstanceId,
                ["LLMW_WORKER_INSTANCE_ID"] = workerId,
                ["LLMW_RUN_ID"] = request.RunId,
                ["LLMW_LAUNCH_BINDING_ID"] = bindingId,
                ["LLMW_WORKER_PIPE_NAME"] = pipeName
            };

            CancellationTokenSource? lifetime = null;
            NamedPipeServerStream? pipe = null;
            try
            {
                if (!OperatingSystem.IsWindows() ||
                    broker.Availability is not SandboxAvailability.Available ||
                    string.IsNullOrWhiteSpace(sandboxHost.Identity?.AppContainerSid))
                {
                    bindings.Unregister(bindingId);
                    throw new InvalidOperationException("Sandboxed Worker launch is unavailable.");
                }

                pipe = WorkerIpcPipeFactory.Create(pipeName, sandboxHost.Identity.AppContainerSid);
                lifetime = new CancellationTokenSource();
                var options = new IpcServerOptions
                {
                    WorkspaceInstanceId = workspaceInstanceId,
                    ExpectedClientKind = IpcClientKind.Worker,
                    Bootstrap = new IpcBootstrapAuthenticator(bootstrap),
                    EventRing = eventRing,
                    Bindings = bindings,
                    LaunchBindingId = bindingId,
                    RunSessions = sessions
                };
                var capturedPipe = pipe;
                var capturedLifetime = lifetime;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await capturedPipe.WaitForConnectionAsync(capturedLifetime.Token).ConfigureAwait(false);
                        await IpcServerSession.ServeAsync(capturedPipe, options, capturedLifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }, capturedLifetime.Token);

                var launched = broker.LaunchRunWorker(new SandboxWorkerLaunchRequest(
                    launchPrincipal,
                    request.RunId,
                    workerId,
                    workerExecutablePath,
                    [],
                    overlay,
                    Timeout.InfiniteTimeSpan));
                if (!launched.Succeeded || launched.Process is null)
                {
                    lifetime.Cancel();
                    bindings.Unregister(bindingId);
                    pipe.Dispose();
                    throw new InvalidOperationException(launched.DenyReason ?? "Worker launch failed closed.");
                }

                workers[workerId] = new LiveEntry(workerId, bindingId, channelId, request.RunId, launched.Process, lifetime, pipe);
                return new WorkerLaunchResult(workerId, bindingId, channelId);
            }
            catch
            {
                lifetime?.Cancel();
                pipe?.Dispose();
                throw;
            }
        }
    }

    public bool Release(string workerInstanceId)
    {
        if (!workers.TryRemove(workerInstanceId, out var entry))
        {
            return false;
        }

        sessions.RevokeByChannelWorker(new AuthenticatedChannelContext(
            entry.ChannelInstanceId,
            AuthenticatedClientKind.Worker,
            entry.WorkerInstanceId,
            projectScope,
            entry.RunId));
        entry.Process.Terminate();
        entry.Lifetime.Cancel();
        entry.Process.Dispose();
        entry.Pipe.Dispose();
        entry.Lifetime.Dispose();
        bindings.Unregister(entry.LaunchBindingId);
        return true;
    }

    public bool IsAlive(string workerInstanceId) =>
        workers.TryGetValue(workerInstanceId, out var entry) && entry.Process.IsAlive;

    public IReadOnlyList<LiveWorkerObservation> Snapshot() =>
        workers.Values
            .Select(item => new LiveWorkerObservation(item.WorkerInstanceId, item.LaunchBindingId, item.RunId, item.Process.IsAlive))
            .ToArray();

    private sealed record LiveEntry(
        string WorkerInstanceId,
        string LaunchBindingId,
        string ChannelInstanceId,
        string RunId,
        ISandboxedWorkerProcess Process,
        CancellationTokenSource Lifetime,
        NamedPipeServerStream Pipe);
}
