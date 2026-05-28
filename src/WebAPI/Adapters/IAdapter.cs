using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;

namespace WebAPI.Adapters
{
    /// <summary>AI平台适配器接口</summary>
    public interface IAdapter
    {
        string PlatformId { get; }
        string PlatformName { get; }
        string TargetUrl { get; }
        int DefaultPort { get; }
        string UserDataFolder { get; }
        List<ModelInfo> AvailableModels { get; }

        /// <summary>获取网络拦截JS脚本（注入到WebView2）</summary>
        string GetNetworkInterceptorScript();

        /// <summary>获取DOM操作JS脚本（设置输入值、触发发送）</summary>
        string GetDomControlScript(string prompt);

        /// <summary>将prompt注入到WebView2并触发发送</summary>
        Task InjectPromptAsync(WebView2 webView, string prompt);

        /// <summary>解析SSE数据，返回有效内容或null</summary>
        Common.SseParser.SseEvent? ParseSseData(string rawLine);
    }
}
