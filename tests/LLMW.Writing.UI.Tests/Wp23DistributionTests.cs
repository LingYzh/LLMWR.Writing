using LLMW.Writing.UI;

namespace LLMW.Writing.UI.Tests;

internal static class Wp23DistributionTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP23", "app");
        var local = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP23", "local");
        var installed = DistributionLayout.ResolveApplicationDataRoot(false, root, local);
        var portable = DistributionLayout.ResolveApplicationDataRoot(true, root, local);

        Program.AssertEqual(Path.GetFullPath(Path.Combine(local, "LLMW.Writing")), installed,
            "Installed application data must remain under LocalAppData.");
        Program.AssertEqual(Path.GetFullPath(Path.Combine(root, "data")), portable,
            "Portable application data must be beside the executable.");
        Program.AssertTrue(!portable.StartsWith(local, StringComparison.OrdinalIgnoreCase),
            "Portable application data must not silently fall back to LocalAppData.");

        Console.WriteLine("WP23 distribution tests passed (3).");
        return 3;
    }
}
