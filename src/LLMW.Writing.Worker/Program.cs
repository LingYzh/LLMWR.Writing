namespace LLMW.Writing.Worker;

internal static class Program
{
    private static async Task Main()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMW_UI_BOOTSTRAP_TOKEN")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMW_RUNTIME_BOOTSTRAP_TOKEN")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMW_CORE_BOOTSTRAP_TOKEN")))
        {
            throw new InvalidOperationException("A Worker must never inherit a Core bootstrap credential.");
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        // WP01 establishes only lifecycle isolation. Runtime-owned per-Run creation and OS sandboxing begin later.
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
    }
}
