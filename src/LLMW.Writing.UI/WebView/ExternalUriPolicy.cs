using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace LLMW.Writing.UI.WebView;

internal sealed class ValidatedExternalUri
{
    public ValidatedExternalUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        AbsoluteUri = uri.AbsoluteUri;
        Scheme = uri.Scheme;
        Host = string.IsNullOrEmpty(uri.IdnHost) ? uri.Host : uri.IdnHost;
        DisplayHost = $"{Scheme}://{Host}";
    }

    public string AbsoluteUri { get; }
    public string Scheme { get; }
    public string Host { get; }
    public string DisplayHost { get; }

    public override string ToString() => AbsoluteUri;
}

internal static class ExternalUriPolicy
{
    public const int MaximumUriChars = 2048;

    public static bool TryValidate(
        string? raw,
        [NotNullWhen(true)] out ValidatedExternalUri? validated,
        [NotNullWhen(false)] out string? errorCode)
    {
        validated = null;
        errorCode = BridgeErrorCodes.ExternalUrlDenied;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumUriChars)
        {
            return false;
        }

        if (AppOriginPolicy.ContainsControlCharacters(raw)
            || raw.Contains('\\', StringComparison.Ordinal)
            || raw.Contains(' ', StringComparison.Ordinal)
            || raw.Contains('\t', StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri is null)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return false;
        }

        if (uri.IsFile || uri.IsUnc)
        {
            return false;
        }

        validated = new ValidatedExternalUri(uri);
        errorCode = null;
        return true;
    }
}

internal interface IExternalBrowserLauncher
{
    void Open(ValidatedExternalUri uri);
}

internal interface IExternalLinkConsent
{
    Task<bool> ConfirmAsync(ValidatedExternalUri uri);
}

internal sealed class AlwaysAllowExternalLinkConsent : IExternalLinkConsent
{
    public Task<bool> ConfirmAsync(ValidatedExternalUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Task.FromResult(true);
    }
}

internal sealed class ShellExecuteExternalBrowserLauncher : IExternalBrowserLauncher
{
    public void Open(ValidatedExternalUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }
}

internal static class ExternalLinkFlow
{
    public static async Task<(bool AllowedByPolicy, string? ErrorCode, bool Accepted)> OpenAsync(
        string? raw,
        IExternalLinkConsent consent,
        IExternalBrowserLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(launcher);
        if (!ExternalUriPolicy.TryValidate(raw, out var validated, out var errorCode))
        {
            return (false, errorCode, false);
        }

        if (!await consent.ConfirmAsync(validated).ConfigureAwait(true))
        {
            return (true, null, false);
        }

        launcher.Open(validated);
        return (true, null, true);
    }
}
