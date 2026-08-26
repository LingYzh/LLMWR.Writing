namespace LLMW.Writing.UI;

internal static class DistributionLayout
{
    internal const string PortableMarkerName = "portable.marker";

    public static bool IsPortable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerName));

    public static string ApplicationDataRoot => ResolveApplicationDataRoot(
        IsPortable,
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ResolveApplicationDataRoot(bool portable, string applicationBase, string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return portable
            ? Path.GetFullPath(Path.Combine(applicationBase, "data"))
            : Path.GetFullPath(Path.Combine(localAppData, "LLMW.Writing"));
    }
}
