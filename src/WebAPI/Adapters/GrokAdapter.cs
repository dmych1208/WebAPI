using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class GrokAdapter : IAdapter
    {
        private readonly SseParser _sseParser = new();

        public string PlatformId => "grok";
        public string PlatformName => "Grok";
        public string TargetUrl => "https://grok.com/";
        public int DefaultPort => 56668;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Grok");

        bool IAdapter.UsesWebResourceCapture => true;
        string? IAdapter.WebResourceRequestedFilter => "https://grok.com/rest/*";
        bool IAdapter.CanSendDirectRequest => true;

        bool IAdapter.IsChatEndpoint(string url)
        {
            string lower = (url ?? "").ToLower();
            return (lower.Contains("grok.com/rest/") || lower.Contains("x.ai/")) &&
                   (lower.Contains("chat") || lower.Contains("conversation") || lower.Contains("response") || lower.Contains("message"));
        }

        void IAdapter.OnWebResourceRequested(CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string url = (e.Request.Uri ?? "").ToLower();
                if (!url.Contains("grok.com/rest/")) return;
                if (!string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return;

                _latestApiUrl = e.Request.Uri;

                try
                {
                    string authHeader = e.Request.Headers.GetHeader("Authorization") ?? "";
                    if (!string.IsNullOrWhiteSpace(authHeader))
                        _latestAuthHeader = authHeader;
                }
                catch { }

                try
                {
                    string ct = e.Request.Headers.GetHeader("Content-Type") ?? "";
                    if (!string.IsNullOrWhiteSpace(ct))
                        _latestContentType = ct;
                }
                catch { }

                try
                {
                    var content = e.Request.Content;
                    if (content != null)
                    {
                        if (content.CanSeek) content.Position = 0;
                        using (var ms = new MemoryStream())
                        {
                            content.CopyTo(ms);
                            _latestRequestBody = Encoding.UTF8.GetString(ms.ToArray());
                        }
                        if (content.CanSeek) content.Position = 0;
                    }
                }
                catch { }
            }
            catch { }
        }

        void IAdapter.OnWebResourceResponseReceived(CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
        }

        private string? _latestApiUrl;
        private string? _latestAuthHeader;
        private string? _latestContentType;
        private string? _latestRequestBody;
        private string? _cachedAuthToken;
        private DateTime _authTokenCachedTime = DateTime.MinValue;
        private readonly object _authLock = new();

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "grok-auto", Name = "Grok Auto" },
            new ModelInfo { Id = "grok-fast", Name = "Grok Fast" },
            new ModelInfo { Id = "grok-expert", Name = "Grok Expert" },
            new ModelInfo { Id = "grok-heavy", Name = "Grok Heavy" }
        };

        async Task IAdapter.CapturePageAuthAsync(WebView2 webView)
        {
            try
            {
                if (webView.CoreWebView2 == null) return;

                string script = @"
(() => {
    let token = '';
    try {
        let items = [];
        for (let i = 0; i < localStorage.length; i++) {
            let k = localStorage.key(i);
            items.push({k: k, v: localStorage.getItem(k)});
        }
        for (let i = 0; i < sessionStorage.length; i++) {
            let k = sessionStorage.key(i);
            items.push({k: k, v: sessionStorage.getItem(k)});
        }
        for (let item of items) {
            let key = (item.k || '').toLowerCase();
            if (!token && (key.includes('token') || key.includes('auth') || key.includes('jwt')))
                token = item.v || '';
        }
    } catch(e) {}
    if (!token) {
        try {
            let metas = document.querySelectorAll('meta[name=""sso-token""], meta[name=""bearer-token""]');
            for (let m of metas) { token = m.getAttribute('content') || ''; if (token) break; }
        } catch(e) {}
    }
    return JSON.stringify({token: token});
})();";
                string result = await webView.CoreWebView2.ExecuteScriptAsync(script);
                string json = DecodeScriptResult(result);
                if (string.IsNullOrWhiteSpace(json)) return;

                string? token = ExtractJsonValue(json, "token");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    lock (_authLock)
                    {
                        _cachedAuthToken = token;
                        _authTokenCachedTime = DateTime.UtcNow;
                    }
                }
            }
            catch { }
        }

        async Task<bool> IAdapter.SendDirectRequestAsync(WebView2 webView, string prompt, string modelId,
            Action<string> onData, Action onDone, Action<string> onLog)
        {
            try
            {
                if (webView.CoreWebView2 == null)
                {
                    onLog("WebView2 未初始化，降级到网页模拟");
                    return false;
                }

                var cookies = await ExportGrokCookiesAsync(webView);

                string? apiUrl = _latestApiUrl;
                if (string.IsNullOrWhiteSpace(apiUrl))
                {
                    onLog("后台直连：尚未捕获到 Grok API 端点，降级到网页模拟");
                    return false;
                }

                string? authHeader = _latestAuthHeader;
                if (string.IsNullOrWhiteSpace(authHeader))
                {
                    lock (_authLock)
                    {
                        if (!string.IsNullOrWhiteSpace(_cachedAuthToken) &&
                            (DateTime.UtcNow - _authTokenCachedTime).TotalHours < 2)
                            authHeader = "Bearer " + _cachedAuthToken;
                    }
                }
                if (string.IsNullOrWhiteSpace(authHeader))
                {
                    onLog("后台直连：未获取到认证令牌，降级到网页模拟");
                    return false;
                }

                string? bodyTemplate = _latestRequestBody;
                if (string.IsNullOrWhiteSpace(bodyTemplate))
                {
                    onLog("后台直连：尚未捕获到请求模板，降级到网页模拟");
                    return false;
                }

                string escapedPrompt = EscapeJson(prompt ?? "");
                string requestBody = bodyTemplate;
                var promptRegex = new Regex("\"message\"\\s*:\\s*\"[^\"]*\"");
                requestBody = promptRegex.Replace(requestBody, "\"message\":\"" + escapedPrompt + "\"", 1);
                var promptRegex2 = new Regex("\"prompt\"\\s*:\\s*\"[^\"]*\"");
                requestBody = promptRegex2.Replace(requestBody, "\"prompt\":\"" + escapedPrompt + "\"", 1);

                string contentType = _latestContentType ?? "application/json";

                onLog($"后台直连：请求 Grok API");

                string rawResponse = await SendCookieBackedRequestAsync(
                    "POST", apiUrl, cookies, requestBody, contentType,
                    req =>
                    {
                        req.Headers["Authorization"] = authHeader!;
                        req.Referer = "https://grok.com/";
                        req.Headers["Origin"] = "https://grok.com";
                    });

                onLog("后台直连：已收到 Grok 回复，解析中...");

                string? parsedText = ExtractResponseText(rawResponse);
                if (!string.IsNullOrWhiteSpace(parsedText))
                {
                    onData(parsedText);
                }
                else
                {
                    onData(rawResponse);
                }
                onDone();
                return true;
            }
            catch (Exception ex)
            {
                onLog($"后台直连失败: {ex.Message}，降级到网页模拟");
                return false;
            }
        }

        private static async Task<Dictionary<string, string>> ExportGrokCookiesAsync(WebView2 webView)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (webView.CoreWebView2 == null) return dict;

            try
            {
                var urls = new[] { "https://grok.com/", "https://x.ai/" };
                foreach (var url in urls)
                {
                    var cookiesList = await webView.CoreWebView2.CookieManager.GetCookiesAsync(url);
                    foreach (var cookie in cookiesList)
                    {
                        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name)) continue;
                        dict[cookie.Name] = cookie.Value ?? "";
                    }
                }
            }
            catch { }

            return dict;
        }

        private static async Task<string> SendCookieBackedRequestAsync(
            string method, string url, Dictionary<string, string> cookies,
            string? body, string? contentType, Action<HttpWebRequest>? configure)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 120000;
            req.ReadWriteTimeout = 120000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.CookieContainer = new CookieContainer();
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            req.Accept = "*/*";
            if (!string.IsNullOrWhiteSpace(contentType))
                req.ContentType = contentType;

            foreach (var kv in cookies)
            {
                try
                {
                    string domain = url.Contains("x.ai") ? ".x.ai" : ".grok.com";
                    req.CookieContainer.Add(new Cookie(kv.Key, kv.Value ?? "", "/", domain));
                }
                catch { }
            }

            configure?.Invoke(req);

            if (!string.IsNullOrEmpty(body))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                using (var stream = await req.GetRequestStreamAsync())
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }

            using (var resp = (HttpWebResponse)await req.GetResponseAsync())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private static string? ExtractResponseText(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse)) return null;
            try
            {
                using var doc = JsonDocument.Parse(rawResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("message", out var msgProp)) return msgProp.GetString();
                if (root.TryGetProperty("text", out var textProp)) return textProp.GetString();
                if (root.TryGetProperty("response", out var respProp))
                {
                    if (respProp.ValueKind == JsonValueKind.String) return respProp.GetString();
                    if (respProp.TryGetProperty("message", out var rMsgProp)) return rMsgProp.GetString();
                    if (respProp.TryGetProperty("text", out var rTextProp)) return rTextProp.GetString();
                }
                if (root.TryGetProperty("content", out var contentProp)) return contentProp.GetString();
                if (root.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == JsonValueKind.Array)
                {
                    var first = choicesProp.EnumerateArray().FirstOrDefault();
                    if (first.TryGetProperty("message", out var cmProp))
                    {
                        if (cmProp.TryGetProperty("content", out var ccProp)) return ccProp.GetString();
                    }
                    if (first.TryGetProperty("text", out var ctProp)) return ctProp.GetString();
                }
                return rawResponse;
            }
            catch
            {
                return rawResponse;
            }
        }

        private static string DecodeScriptResult(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return "";
            string text = raw;
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                text = text.Substring(1, text.Length - 2);
            return text.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static string? ExtractJsonValue(string json, string key)
        {
            var regex = new Regex($"\"{key}\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            var match = regex.Match(json);
            if (match.Success) return match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            return null;
        }

        private static string EscapeJson(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        public string GetNetworkInterceptorScript()
        {
            return @"
(() => {
    function sendToBridge(prefix, text) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(prefix + text); }
    function log(msg) { sendToBridge('[LOG] ', msg); }

    const originalFetch = window.fetch;

    function isChatResponse(url) {
        if (typeof url !== 'string') return false;
        var isGrok = url.indexOf('grok.com') !== -1 || url.indexOf('x.ai') !== -1;
        if (!isGrok) return false;
        var isStatic = url.match(/\.(js|css|png|jpg|jpeg|gif|svg|woff|woff2|ttf|ico|webp|mp4|mp3|wav|ogg|pdf|zip|gz)(\?.*)?$/);
        if (isStatic) return false;
        return true;
    }

    window.fetch = async function(...args) {
        const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
        try {
            const response = await originalFetch.apply(this, args);
            if (isChatResponse(url)) {
                if (response.body) {
                    const clone = response.clone();
                    (async () => {
                        try {
                            const reader = clone.body.getReader();
                            const decoder = new TextDecoder();
                            while (true) {
                                var result = await reader.read();
                                if (result.done) {
                                    sendToBridge('[NETWORK_DONE]', '');
                                    break;
                                }
                                var chunk = decoder.decode(result.value, { stream: true });
                                sendToBridge('[NETWORK_DATA]', chunk);
                            }
                        } catch (e) {}
                    })();
                }
            }
            return response;
        } catch (err) {
            return originalFetch.apply(this, args);
        }
    };

    const OriginalXHR = window.XMLHttpRequest;
    window.XMLHttpRequest = function() {
        const xhr = new OriginalXHR();
        let url = '';
        const originalOpen = xhr.open;
        xhr.open = function(method, requestUrl) {
            url = requestUrl || '';
            return originalOpen.apply(this, arguments);
        };
        xhr.addEventListener('progress', function() {
            if (!isChatResponse(url)) return;
            try {
                let fullText = '';
                try { fullText = xhr.responseText; } catch(e) { return; }
                if (!fullText) return;
                const lastLen = xhr._lastLength || 0;
                const newChunk = fullText.substring(lastLen);
                if (newChunk.length > 0) {
                    sendToBridge('[NETWORK_DATA]', newChunk);
                    xhr._lastLength = fullText.length;
                }
            } catch(e) {}
        });
        xhr.addEventListener('load', function() {
            if (isChatResponse(url)) sendToBridge('[NETWORK_DONE]', '');
        });
        return xhr;
    };

    const OriginalEventSource = window.EventSource;
    window.EventSource = function(url, config) {
        const es = new OriginalEventSource(url, config);
        const origAddEventListener = es.addEventListener;
        es.addEventListener = function(type, listener, options) {
            const wrappedListener = function(event) {
                sendToBridge('[NETWORK_DATA]', 'event:' + type + '\ndata:' + (typeof event.data === 'string' ? event.data : JSON.stringify(event.data)) + '\n\n');
                if (listener) listener.call(this, event);
            };
            return origAddEventListener.call(this, type, wrappedListener, options);
        };
        let _onmessage = null, _onopen = null, _onerror = null;
        Object.defineProperty(es, 'onmessage', { get: function() { return _onmessage; }, set: function(fn) { _onmessage = fn; if (fn) es.addEventListener('message', fn); } });
        Object.defineProperty(es, 'onopen', { get: function() { return _onopen; }, set: function(fn) { _onopen = fn; if (fn) es.addEventListener('open', fn); } });
        Object.defineProperty(es, 'onerror', { get: function() { return _onerror; }, set: function(fn) { _onerror = fn; if (fn) es.addEventListener('error', fn); } });
        return es;
    };
    Object.defineProperty(window.EventSource, 'prototype', { value: OriginalEventSource.prototype });
    window.EventSource.CONNECTING = OriginalEventSource.CONNECTING;
    window.EventSource.OPEN = OriginalEventSource.OPEN;
    window.EventSource.CLOSED = OriginalEventSource.CLOSED;

    log('GrokAdapter 网络拦截已启用(Fetch+XHR+EventSource)');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            return $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    let input = null;
    const selectors = [
        'textarea[placeholder*=""Ask"" i]',
        'textarea[placeholder*=""Message"" i]',
        'textarea[placeholder*=""问题"" i]',
        'textarea',
        'div[contenteditable=""true""]',
        '[role=""textbox""]'
    ];

    for (const sel of selectors) {{
        const el = document.querySelector(sel);
        if (el && el.offsetParent !== null && el.getBoundingClientRect().width > 50) {{
            input = el;
            break;
        }}
    }}

    if (!input) {{
        return {{ success: false, error: '找不到输入框' }};
    }}

    input.focus();
    await sleep(200);

    if (input.getAttribute('contenteditable') === 'true') {{
        input.innerHTML = '';
        input.textContent = '{escapedPrompt}';
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: '{escapedPrompt}', inputType: 'insertText' }}));
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(input);
        range.collapse(false);
        selection?.removeAllRanges();
        selection?.addRange(range);
    }} else {{
        const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
        if (nativeSetter) {{ nativeSetter.call(input, '{escapedPrompt}'); }}
        else {{ input.value = '{escapedPrompt}'; }}
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new Event('change', {{ bubbles: true }}));
    }}

    await sleep(500);

    input.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }}));
    input.dispatchEvent(new KeyboardEvent('keyup', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }}));

    await sleep(500);

    let sendBtn = null;
    const allButtons = document.querySelectorAll('button, [role=""button""], svg[class*=""send""], svg[aria-label*=""send"" i]');
    for (const btn of allButtons) {{
        if (!btn.offsetParent || btn.getBoundingClientRect().width < 20) continue;
        const ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        if (ariaLabel.includes('send') || ariaLabel.includes('发送') || ariaLabel.includes('submit')) {{
            sendBtn = btn;
            break;
        }}
        const closestBtn = btn.closest('button');
        if (closestBtn && closestBtn.offsetParent) {{
            sendBtn = closestBtn;
            break;
        }}
    }}

    if (sendBtn) {{
        sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
    }}

    return {{ success: true, method: sendBtn ? 'button-click' : 'enter' }};
}})();
";
        }

        public async Task InjectPromptAsync(WebView2 webView, string prompt)
        {
            if (webView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 未初始化");
            string script = GetDomControlScript(prompt);
            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        public SseParser.SseEvent? ParseSseData(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return null;
            return _sseParser.Parse(rawLine);
        }

        public string GetModeSwitchScript(bool deepThink, bool search)
        {
            string dtFlag = deepThink ? "true" : "false";
            return $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}
    for (var i = 0; i < 3; i++) {{
        var buttons = document.querySelectorAll('button, [role=""switch""]');
        for (var j = 0; j < buttons.length; j++) {{
            var btn = buttons[j];
            var txt = (btn.textContent || '').toLowerCase();
            if (txt.includes('deep') || txt.includes('think') || txt.includes('reasoning')) {{
                var isActive = btn.classList.contains('active') || btn.classList.contains('selected') || btn.getAttribute('aria-checked') === 'true';
                var deepThinkFlag = {dtFlag};
                if (deepThinkFlag !== isActive) {{ btn.click(); await sleep(300); }}
                break;
            }}
        }}
        break;
    }}
}})();
";
        }

        public string? ExtractContentFromSseData(string dataJson, string eventType)
        {
            if (string.IsNullOrEmpty(dataJson)) return null;
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
                if (data.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == JsonValueKind.Array)
                {
                    var firstChoice = choicesProp.EnumerateArray().FirstOrDefault();
                    if (firstChoice.TryGetProperty("delta", out var deltaProp))
                    {
                        if (deltaProp.TryGetProperty("content", out var dcProp)) return dcProp.GetString();
                    }
                }
                if (data.TryGetProperty("token", out var tokenProp)) return tokenProp.GetString();
                if (data.TryGetProperty("text", out var tProp)) return tProp.GetString();
                if (data.TryGetProperty("content", out var cProp)) return cProp.GetString();
                if (data.TryGetProperty("delta", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                    return dProp.GetString();
            }
            catch { }
            return null;
        }

        private static string EscapeForJs(string str)
        {
            return str.Replace("\\", "\\\\")
                      .Replace("'", "\\'")
                      .Replace("\"", "\\\"")
                      .Replace("\n", "\\n")
                      .Replace("\r", "\\r")
                      .Replace("\t", "\\t");
        }
    }
}