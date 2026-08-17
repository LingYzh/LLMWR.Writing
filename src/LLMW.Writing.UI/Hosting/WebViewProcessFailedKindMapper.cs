using Microsoft.Web.WebView2.Core;
using LLMW.Writing.UI.WebView;

namespace LLMW.Writing.UI.Hosting;

internal static class WebViewProcessFailedKindMapper
{
    public static WebViewProcessFailedKind Map(CoreWebView2ProcessFailedKind kind)
    {
        return kind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewProcessFailedKind.BrowserProcessExited,
            CoreWebView2ProcessFailedKind.RenderProcessExited => WebViewProcessFailedKind.RenderProcessExited,
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => WebViewProcessFailedKind.RenderProcessUnresponsive,
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited => WebViewProcessFailedKind.FrameRenderProcessExited,
            CoreWebView2ProcessFailedKind.UtilityProcessExited => WebViewProcessFailedKind.UtilityProcessExited,
            CoreWebView2ProcessFailedKind.SandboxHelperProcessExited => WebViewProcessFailedKind.SandboxHelperProcessExited,
            CoreWebView2ProcessFailedKind.GpuProcessExited => WebViewProcessFailedKind.GpuProcessExited,
            CoreWebView2ProcessFailedKind.PpapiPluginProcessExited => WebViewProcessFailedKind.PpapiPluginProcessExited,
            CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited => WebViewProcessFailedKind.PpapiBrokerProcessExited,
            CoreWebView2ProcessFailedKind.UnknownProcessExited => WebViewProcessFailedKind.UnknownProcessExited,
            _ => WebViewProcessFailedKind.UnknownProcessExited
        };
    }
}
