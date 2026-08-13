namespace LLMW.Writing.UI;

internal static class Program
{
    private static void Main()
    {
        // The trusted native UI process is the future bootstrapper. It keeps Core and Runtime
        // credentials separate and does not put either secret in command-line arguments.
        // The WinUI startup composition root will own ProcessBootstrapper after WinUI integration.
    }
}
