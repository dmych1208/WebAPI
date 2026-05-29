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
    }
}
