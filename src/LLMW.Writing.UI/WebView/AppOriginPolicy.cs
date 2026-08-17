using System.Diagnostics.CodeAnalysis;

namespace LLMW.Writing.UI.WebView;

internal static class AppOrigin
{
    public const string Scheme = "https";
    public const string Host = "app.llmw.invalid";
    public const int Port = 443;
    public const string Origin = "https://app.llmw.invalid";
    public const string IndexHtmlAbsoluteUri = "https://app.llmw.invalid/index.html";
    public const string IndexPath = "/index.html";
    public const string RootPath = "/";
    public const string BridgeScriptPath = "/bridge.js";
    public const string AppCssPath = "/app.css";
    public const string EditorBundlePath = "/editor.bundle.js";
}

internal static class AppOriginPolicy
{
    private static readonly HashSet<string> DocumentPaths = new(StringComparer.Ordinal)
    {
        AppOrigin.RootPath,
        AppOrigin.IndexPath
    };

    private static readonly HashSet<string> ResourcePaths = new(StringComparer.Ordinal)
    {
        AppOrigin.RootPath,
        AppOrigin.IndexPath,
        AppOrigin.BridgeScriptPath,
        AppOrigin.AppCssPath,
        AppOrigin.EditorBundlePath
    };

    public static bool TryParseAbsolute(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (ContainsControlCharacters(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed is null)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsExactApplicationOrigin(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, AppOrigin.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (!HostEqualsApplication(uri))
        {
            return false;
        }

        var port = uri.IsDefaultPort ? AppOrigin.Port : uri.Port;
        return port == AppOrigin.Port;
    }

    public static bool IsApplicationDocument(string? value)
        => TryParseAbsolute(value, out var uri) && IsApplicationDocument(uri);

    public static bool IsApplicationDocument(Uri uri)
        => IsExactApplicationOrigin(uri)
           && HasEmptyQueryAndFragment(uri)
           && IsExactAllowedPath(uri, DocumentPaths);

    public static bool IsApplicationResource(string? value)
        => TryParseAbsolute(value, out var uri) && IsApplicationResource(uri);

    public static bool IsApplicationResource(Uri uri)
        => IsExactApplicationOrigin(uri)
           && HasEmptyQueryAndFragment(uri)
           && IsExactAllowedPath(uri, ResourcePaths);

    public static bool IsTrustedMessageSource(string? source, string? currentDocument)
        => IsApplicationDocument(source) && IsApplicationDocument(currentDocument);

    private static bool HostEqualsApplication(Uri uri)
    {
        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = uri.Host;
        }

        return string.Equals(host, AppOrigin.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasEmptyQueryAndFragment(Uri uri)
        => string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsExactAllowedPath(Uri uri, HashSet<string> allowed)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return allowed.Contains(path);
    }

    internal static bool ContainsControlCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}
