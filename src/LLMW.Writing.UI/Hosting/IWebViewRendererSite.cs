using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMW.Writing.UI.Hosting;

internal interface IWebViewRendererSite
{
    WebView2 Renderer { get; }
    XamlRoot XamlRoot { get; }
    DispatcherQueue DispatcherQueue { get; }
    void ShowNativeStatus(string code, string message);
    void ShowNativeError(string code, string message);
    WebView2 RecreateRenderer();
}
