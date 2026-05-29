using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;

namespace WebAPI.Adapters
{
    public interface IAdapter
    {
        string PlatformId { get; }
        string PlatformName { get; }
        string TargetUrl { get; }
        int DefaultPort { get; }
        string UserDataFolder { get; }
        List<ModelInfo> AvailableModels { get; }

        string GetNetworkInterceptorScript();

        string GetDomControlScript(string prompt);

        Task InjectPromptAsync(WebView2 webView, string prompt);

        Common.SseParser.SseEvent? ParseSseData(string rawLine);

        string GetModeSwitchScript(bool deepThink, bool search);

        string? ExtractContentFromSseData(string dataJson, string eventType);

        bool UsesWebResourceCapture => false;

        string? WebResourceRequestedFilter => null;

        bool IsChatEndpoint(string url) => false;

        void OnWebResourceRequested(CoreWebView2WebResourceRequestedEventArgs e) { }

        void OnWebResourceResponseReceived(CoreWebView2WebResourceResponseReceivedEventArgs e) { }

        bool CanSendDirectRequest => false;

        Task<bool> SendDirectRequestAsync(WebView2 webView, string prompt, string modelId,
            Action<string> onData, Action onDone, Action<string> onLog) => Task.FromResult(false);

        Task CapturePageAuthAsync(WebView2 webView) => Task.CompletedTask;
    }
}