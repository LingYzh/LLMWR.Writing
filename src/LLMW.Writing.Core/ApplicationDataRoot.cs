namespace LLMW.Writing.Core;

internal static class ApplicationDataRoot
{
    private const string EnvironmentName = "LLMW_APPLICATION_DATA_ROOT";

    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentName);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMW.Writing");
    }
}
