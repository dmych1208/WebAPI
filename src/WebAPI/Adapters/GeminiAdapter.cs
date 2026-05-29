using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class GeminiAdapter : IAdapter
    {
        private readonly SseParser _sseParser = new();

        public string PlatformId => "gemini";
        public string PlatformName => "Gemini";
        public string TargetUrl => "https://gemini.google.com/";
        public int DefaultPort => 56667;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Gemini");

        bool IAdapter.UsesWebResourceCapture => true;
        string? IAdapter.WebResourceRequestedFilter => "https://lh3.googleusercontent.com/*";
        bool IAdapter.CanSendDirectRequest => true;

        bool IAdapter.IsChatEndpoint(string url)
        {
            string lower = (url ?? "").ToLower();
            return lower.Contains("gemini.google.com") && (
                lower.Contains("streamgenerate") ||
                lower.Contains("bardchatui") ||
                lower.Contains("bardfrontend"));
        }

        void IAdapter.OnWebResourceRequested(CoreWebView2WebResourceRequestedEventArgs e)
        {
        }

        void IAdapter.OnWebResourceResponseReceived(CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
        }

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "gemini-web-api", Name = "Gemini Web API" },
            new ModelInfo { Id = "gemini-thinking", Name = "Gemini Thinking" },
            new ModelInfo { Id = "gemini-3.1-flash", Name = "Gemini 3.1 Flash" },
            new ModelInfo { Id = "gemini-3.1-pro", Name = "Gemini 3.1 Pro" },
            new ModelInfo { Id = "gemini-3.1-image", Name = "Gemini 3.1 Image" },
            new ModelInfo { Id = "gemini-3.1-flash-image", Name = "Gemini 3.1 Flash Image" },
            new ModelInfo { Id = "gemini-3.1-pro-image", Name = "Gemini 3.1 Pro Image" }
        };

        private string? _cachedAccessToken;
        private DateTime _accessTokenCachedTime = DateTime.MinValue;
        private readonly object _authLock = new();

        async Task IAdapter.CapturePageAuthAsync(WebView2 webView)
        {
            try { _cachedAccessToken = null; } catch { }
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

                var cookies = await ExportGeminiCookiesAsync(webView);
                onLog($"后台直连：已读取 Gemini 登录状态 ({cookies.Count} cookies)");

                string accessToken = await GetGeminiAccessTokenAsync(webView, cookies);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    onLog("后台直连：未获取到 access token，降级到网页模拟");
                    return false;
                }

                string modelParam = (modelId ?? "gemini-web-api").ToLower();
                string geminiModel = modelParam.Contains("thinking") ? "gemini-2.5-flash-thinking" :
                                  modelParam.Contains("3.1-flash") && modelParam.Contains("image") ? "gemini-3.1-flash-preview-vision" :
                                  modelParam.Contains("3.1-pro") && modelParam.Contains("image") ? "gemini-3.1-pro-vision" :
                                  modelParam.Contains("3.1-flash") ? "gemini-3.1-flash" :
                                  modelParam.Contains("3.1-pro") ? "gemini-3.1-pro" :
                                  modelParam.Contains("3.1-image") ? "gemini-3.1-flash" :
                                  "gemini-2.5-flash";

                bool isImageModel = modelParam.Contains("image");

                onLog($"后台直连：请求 Gemini model={geminiModel}");

                string apiUrl = "https://gemini.google.com/_/BardChatUi/data/assistant.lamda.BardFrontendService/StreamGenerate";

                object[] item0;
                if (isImageModel)
                {
                    var imagePart = new Dictionary<string, object>
                    {
                        ["imagePrompt"] = prompt,
                        ["model"] = geminiModel
                    };
                    item0 = new object[] { prompt, 0, null, new ArrayList(), imagePart };
                }
                else
                {
                    item0 = new object[] { prompt };
                }

                var innerList = new ArrayList { item0, null, null };
                string innerJson = SerializeJsonArray(innerList.ToArray());
                string outerJson = SerializeJsonArray(new object[] { null!, innerJson });
                string formBody = "at=" + Uri.EscapeDataString(accessToken) + "&f.req=" + Uri.EscapeDataString(outerJson);

                string rawResponse = await SendCookieBackedRequestAsync(
                    "POST", apiUrl, cookies, formBody,
                    "application/x-www-form-urlencoded;charset=utf-8",
                    req =>
                    {
                        req.Headers["Origin"] = "https://gemini.google.com";
                        req.Headers["X-Same-Domain"] = "1";
                        req.Referer = "https://gemini.google.com/";
                    });

                onLog("后台直连：已收到 Gemini 回复，正在解析...");

                string parsedText = ParseGeminiBackendResponse(rawResponse);
                if (!string.IsNullOrWhiteSpace(parsedText))
                {
                    onData(parsedText);
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

        private static async Task<Dictionary<string, string>> ExportGeminiCookiesAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
                throw new Exception("WebView 未就绪，无法读取 Cookie。");

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var urls = new[] {
                    "https://gemini.google.com/",
                    "https://accounts.google.com/",
                    "https://www.google.com/"
                };

                foreach (var url in urls)
                {
                    var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(url);
                    foreach (var cookie in cookies)
                    {
                        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name)) continue;
                        dict[cookie.Name] = cookie.Value ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("读取 WebView Cookie 失败: " + ex.Message);
            }

            if (!dict.ContainsKey("__Secure-1PSID"))
                throw new Exception("当前 Gemini 登录态缺少 __Secure-1PSID，请先在 WebView 中登录 Google 账号。");

            return dict;
        }

        private async Task<string> GetGeminiAccessTokenAsync(WebView2 webView, Dictionary<string, string> cookies)
        {
            lock (_authLock)
            {
                if (!string.IsNullOrWhiteSpace(_cachedAccessToken) &&
                    (DateTime.UtcNow - _accessTokenCachedTime).TotalMinutes < 10)
                    return _cachedAccessToken;
            }

            Exception? lastError = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    try
                    {
                        await SendCookieBackedRequestAsync("GET", "https://www.google.com", cookies, null, "text/html", null);
                    }
                    catch { }

                    string html = await SendCookieBackedRequestAsync(
                        "GET", "https://gemini.google.com/app", cookies, null, "text/html", null);

                    var match = Regex.Match(html ?? "", "\"SNlM0e\":\"([^\"]+)\"");
                    if (!match.Success)
                        match = Regex.Match(html ?? "", @"SNlM0e\\?""\s*:\s*\\?""([^""\\]+)");
                    if (!match.Success)
                    {
                        string pageHtml = await TryGetCurrentWebViewHtmlAsync(webView);
                        if (!string.IsNullOrWhiteSpace(pageHtml))
                        {
                            match = Regex.Match(pageHtml, "\"SNlM0e\":\"([^\"]+)\"");
                            if (!match.Success)
                                match = Regex.Match(pageHtml, @"SNlM0e\\?""\s*:\s*\\?""([^""\\]+)");
                        }
                    }
                    if (!match.Success)
                        throw new Exception("未能从 Gemini Init 页面提取 SNlM0e。");

                    string token = match.Groups[1].Value;
                    lock (_authLock)
                    {
                        _cachedAccessToken = token;
                        _accessTokenCachedTime = DateTime.UtcNow;
                    }
                    return token;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await Task.Delay(500 * (attempt + 1));
                }
            }

            lock (_authLock)
            {
                if (!string.IsNullOrWhiteSpace(_cachedAccessToken) &&
                    (DateTime.UtcNow - _accessTokenCachedTime).TotalHours < 2)
                    return _cachedAccessToken;
            }

            throw lastError ?? new Exception("未能从 Gemini Init 页面提取 SNlM0e。");
        }

        private static async Task<string> TryGetCurrentWebViewHtmlAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null) return "";
            try
            {
                string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement ? document.documentElement.outerHTML : ''");
                if (string.IsNullOrWhiteSpace(raw)) return "";
                string s = raw.Trim();
                if (s == "null") return "";
                if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                    s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\u003C", "<")
                     .Replace("\\u003E", ">")
                     .Replace("\\u0026", "&")
                     .Replace("\\u0027", "'")
                     .Replace("\\\"", "\"")
                     .Replace("\\\\", "\\")
                     .Replace("\\n", "\n")
                     .Replace("\\r", "\r")
                     .Replace("\\t", "\t");
                return s;
            }
            catch
            {
                return "";
            }
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
                try { req.CookieContainer.Add(new Cookie(kv.Key, kv.Value ?? "", "/", ".google.com")); }
                catch { }
            }

            configure?.Invoke(req);

            try
            {
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
            catch { throw; }
        }

        private static string ParseGeminiBackendResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new Exception("Gemini 返回为空。");

            List<string> imageUrls = Regex.Matches(
                text,
                @"https://lh3\.googleusercontent\.com/(?:gg-dl|rd-gg-dl|rd-gg)/[^\s""\\]+",
                RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? bestText = null;

            string[] lines = text.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("[")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Array) continue;

                    foreach (var item in root.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Array) continue;
                        var arr = item.EnumerateArray().ToArray();
                        if (arr.Length <= 2) continue;

                        string? innerJson = arr[2].GetString();
                        if (string.IsNullOrWhiteSpace(innerJson)) continue;

                        using var innerDoc = JsonDocument.Parse(innerJson);
                        var innerRoot = innerDoc.RootElement;
                        if (innerRoot.ValueKind != JsonValueKind.Array) continue;
                        var innerArr = innerRoot.EnumerateArray().ToArray();
                        if (innerArr.Length <= 4) continue;
                        if (innerArr[4].ValueKind != JsonValueKind.Array) continue;

                        var candidates = innerArr[4].EnumerateArray();
                        string best = "";
                        foreach (var cand in candidates)
                        {
                            if (cand.ValueKind != JsonValueKind.Array) continue;
                            var candArr = cand.EnumerateArray().ToArray();
                            if (candArr.Length <= 1) continue;
                            if (candArr[1].ValueKind != JsonValueKind.Array) continue;

                            string candText = JoinCandidateTextParts(candArr[1].EnumerateArray());
                            candText = Regex.Replace(candText,
                                @"https?://(?:www\.)?googleusercontent\.com/image_generation_content/\S+", "").Trim();
                            candText = StripBackendCitationMarkers(candText);
                            if (candText.Length > best.Length)
                                best = candText;
                        }
                        if (best.Length > 0)
                        {
                            bestText = best;
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(bestText)) break;
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(bestText))
            {
                if (imageUrls.Count > 0)
                    return string.Join("\n", imageUrls);
                throw new Exception("未定位到 Gemini 主响应体。");
            }

            if (imageUrls.Count > 0)
                bestText += "\n\n" + string.Join("\n", imageUrls);

            return bestText;
        }

        private static string JoinCandidateTextParts(JsonElement.ArrayEnumerator parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                string piece = part.GetString() ?? part.ToString();
                if (string.IsNullOrEmpty(piece)) continue;
                sb.Append(piece);
            }
            return sb.ToString();
        }

        private static string StripBackendCitationMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            string s = text;
            s = s.Replace("[cite_start]", "");
            s = Regex.Replace(s, @"[ \t]*\[cite:\s*\d+\][ \t]*", "");
            s = Regex.Replace(s, @"\r?\n[ \t]+\r?\n", "\n\n");
            return s.Trim();
        }

        private static string SerializeJsonArray(object[] arr)
        {
            var opts = new JsonSerializerOptions { WriteIndented = false };
            return JsonSerializer.Serialize(arr, opts);
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
        var isGeminiDomain = url.indexOf('gemini.google.com') !== -1 ||
                             url.indexOf('generativelanguage.googleapis.com') !== -1 ||
                             url.indexOf('alkalimakersuite-pa.clients6.google.com') !== -1;
        if (!isGeminiDomain) return false;
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

    log('GeminiAdapter 网络拦截已启用(Fetch+XHR+EventSource)');
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
        'div[contenteditable=""true""][aria-label*=""message"" i]',
        'div[contenteditable=""true""][aria-label*=""输入"" i]',
        'div[contenteditable=""true""]',
        'textarea[placeholder*=""message"" i]',
        'textarea[placeholder*=""输入"" i]',
        'textarea',
        '[role=""textbox""]',
        'div[class*=""input-area""] div[contenteditable]'
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

    const enterKeydown = new KeyboardEvent('keydown', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }});
    const enterKeyup = new KeyboardEvent('keyup', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }});
    input.dispatchEvent(enterKeydown);
    await sleep(50);
    input.dispatchEvent(enterKeyup);

    await sleep(500);

    let sendBtn = null;
    const buttons = document.querySelectorAll('button, [role=""button""]');
    for (const btn of buttons) {{
        if (!btn.offsetParent || btn.getBoundingClientRect().width < 20) continue;
        const ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        const text = (btn.textContent || '').toLowerCase();
        if (ariaLabel.includes('send') || ariaLabel.includes('发送') || text.includes('send') || text.includes('发送')) {{
            sendBtn = btn;
            break;
        }}
        if (btn.querySelector('svg') && !ariaLabel && !text) {{
            sendBtn = btn;
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
            string srFlag = search ? "true" : "false";
            return $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}
    var searchToggles = document.querySelectorAll('button, [role=""switch""], div[class*=""search""], [data-testid]');
    for (var j = 0; j < searchToggles.length; j++) {{
        var toggle = searchToggles[j];
        var txt = (toggle.textContent || '').toLowerCase();
        var aLabel = (toggle.getAttribute('aria-label') || '').toLowerCase();
        if (txt.includes('搜索') || txt.includes('联网') || txt.includes('search') ||
            aLabel.includes('搜索') || aLabel.includes('search')) {{
            var isActive = toggle.classList.contains('active') || 
                          toggle.classList.contains('selected') ||
                          toggle.classList.contains('checked') ||
                          toggle.getAttribute('aria-checked') === 'true';
            if ({srFlag} !== isActive) {{
                toggle.click();
                await sleep(300);
            }}
            break;
        }}
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
                        if (deltaProp.TryGetProperty("content", out var dcProp))
                            return dcProp.GetString();
                    }
                }
                if (data.TryGetProperty("candidates", out var candidatesProp) && candidatesProp.ValueKind == JsonValueKind.Array)
                {
                    var firstCandidate = candidatesProp.EnumerateArray().FirstOrDefault();
                    if (firstCandidate.TryGetProperty("content", out var contentProp))
                    {
                        if (contentProp.TryGetProperty("parts", out var partsProp) && partsProp.ValueKind == JsonValueKind.Array)
                        {
                            var firstPart = partsProp.EnumerateArray().FirstOrDefault();
                            if (firstPart.TryGetProperty("text", out var textProp))
                                return textProp.GetString();
                        }
                    }
                }
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