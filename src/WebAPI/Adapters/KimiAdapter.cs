using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class KimiAdapter : IAdapter
    {
        public string PlatformId => "kimi";
        public string PlatformName => "Kimi";
        public string TargetUrl => "https://www.kimi.com/";
        public int DefaultPort => 56670;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Kimi");

        bool IAdapter.UsesWebResourceCapture => true;
        string? IAdapter.WebResourceRequestedFilter => "https://www.kimi.com/apiv2/kimi.gateway.chat.v1.ChatService/Chat*";
        bool IAdapter.CanSendDirectRequest => true;

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "kimi-k2.5-fast", Name = "K2.5 快速" },
            new ModelInfo { Id = "kimi-k2.5-fast-search", Name = "K2.5 快速+搜索" },
            new ModelInfo { Id = "kimi-k2.5-thinking", Name = "K2.5 思考" },
            new ModelInfo { Id = "kimi-k2.5-thinking-search", Name = "K2.5 思考+搜索" },
            new ModelInfo { Id = "kimi-k2.5-agent", Name = "K2.5 Agent" },
            new ModelInfo { Id = "kimi-k2.5-agent-swarm", Name = "K2.5 Agent 集群" }
        };

        private byte[]? _latestChatRequestBodyBytes;
        private string? _latestChatRequestUrl;
        private string? _latestChatRequestMethod;
        private Dictionary<string, string> _latestChatRequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string? _latestChatRequestBody;
        private string? _latestChatRequestPrompt;

        private string? _pageAuthToken;
        private string? _pageDeviceId;
        private string? _pageLanguage;

        bool IAdapter.IsChatEndpoint(string url)
        {
            string lower = (url ?? "").ToLower();
            return lower.Contains("kimi.com/apiv2/kimi.gateway.chat.v1.chatservice/chat");
        }

        void IAdapter.OnWebResourceRequested(CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string url = e.Request.Uri ?? "";
                string method = e.Request.Method ?? "";
                if (!((IAdapter)this).IsChatEndpoint(url)) return;
                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return;

                string bodyText = "";
                try
                {
                    var content = e.Request.Content;
                    if (content != null)
                    {
                        byte[] rawBytes;
                        if (content.CanSeek) content.Position = 0;
                        using (var ms = new MemoryStream())
                        {
                            content.CopyTo(ms);
                            rawBytes = ms.ToArray();
                        }
                        if (content.CanSeek) content.Position = 0;
                        bodyText = ExtractJsonPayloadFromConnectBody(rawBytes);
                        _latestChatRequestBodyBytes = rawBytes;
                    }
                }
                catch { }

                _latestChatRequestHeaders.Clear();
                try
                {
                    string[] headerNames = new[] {
                        "authorization", "content-type", "accept", "accept-language",
                        "x-traffic-id", "x-client-trace-id", "x-msh-device-id",
                        "x-language", "user-agent"
                    };
                    foreach (var headerName in headerNames)
                    {
                        try
                        {
                            string value = e.Request.Headers.GetHeader(headerName) ?? "";
                            if (!string.IsNullOrWhiteSpace(value))
                                _latestChatRequestHeaders[headerName] = value;
                        }
                        catch { }
                    }
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    _latestChatRequestUrl = url;
                    _latestChatRequestMethod = method;
                    _latestChatRequestBody = bodyText;
                    string? prompt = ExtractPromptFromTemplateBody(bodyText);
                    if (!string.IsNullOrEmpty(prompt))
                        _latestChatRequestPrompt = prompt;
                }
            }
            catch { }
        }

        void IAdapter.OnWebResourceResponseReceived(CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                string url = e.Request.Uri ?? "";
                string method = e.Request.Method ?? "";
                if (!((IAdapter)this).IsChatEndpoint(url)) return;
                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return;
            }
            catch { }
        }

        async Task IAdapter.CapturePageAuthAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null) return;
            try
            {
                string script = @"
(() => {
    function collect(storage) {
        const items = [];
        try {
            for (let i = 0; i < storage.length; i++) {
                const key = storage.key(i);
                items.push({ k: key, v: storage.getItem(key) || '' });
            }
        } catch (e) {}
        return items;
    }
    const items = collect(window.localStorage).concat(collect(window.sessionStorage));
    let token = '', deviceId = '', language = '';
    for (const item of items) {
        const key = (item.k || '').toLowerCase();
        const value = item.v || '';
        if (!token && /^eyJ[A-Za-z0-9_-]+\./.test(value)) token = value;
        if (!token && (key.includes('token') || key.includes('auth')) && /^eyJ/.test(value)) token = value;
        if (!deviceId && (key.includes('device') || key.includes('msh'))) deviceId = value;
        if (!language && key.includes('lang')) language = value;
    }
    return JSON.stringify({ token: token, deviceId: deviceId, language: language });
})();";
                string result = await webView.CoreWebView2.ExecuteScriptAsync(script);
                string json = DecodeScriptResult(result);
                if (string.IsNullOrWhiteSpace(json)) return;
                _pageAuthToken = ExtractValueFromJson(json, "token");
                _pageDeviceId = ExtractValueFromJson(json, "deviceId");
                _pageLanguage = ExtractValueFromJson(json, "language");
            }
            catch { }
        }

        async Task<bool> IAdapter.SendDirectRequestAsync(WebView2 webView, string prompt, string modelId,
            Action<string> onData, Action onDone, Action<string> onLog)
        {
            try
            {
                string normalized = (modelId ?? "kimi-k2.5-fast").Trim().ToLower();
                bool useThink = normalized.Contains("thinking") || normalized.Contains("think");
                bool useSearch = normalized.Contains("search");

                byte[] requestBytes = BuildDirectRequestBodyBytes(prompt, useThink, useSearch);
                if (requestBytes == null || requestBytes.Length == 0)
                {
                    onLog("Direct request skipped: body build failed");
                    return false;
                }

                string requestUrl = !string.IsNullOrWhiteSpace(_latestChatRequestUrl)
                    ? _latestChatRequestUrl
                    : "https://www.kimi.com/apiv2/kimi.gateway.chat.v1.ChatService/Chat";

                onLog($"Direct request (C#): {requestUrl}");

                var request = (HttpWebRequest)WebRequest.Create(requestUrl);
                request.Method = string.IsNullOrWhiteSpace(_latestChatRequestMethod) ? "POST" : _latestChatRequestMethod;
                request.ContentType = GetHeaderValue("content-type", "application/connect+json");
                request.Accept = GetHeaderValue("accept", "*/*");
                request.UserAgent = GetHeaderValue("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.Referer = "https://www.kimi.com/";

                await ApplyCookiesAsync(webView, request, requestUrl);

                ApplyHeaderIfPresent(request, "authorization");
                ApplyHeaderIfPresent(request, "accept-language");
                ApplyHeaderIfPresent(request, "x-traffic-id");
                ApplyHeaderIfPresent(request, "x-client-trace-id");
                ApplyHeaderIfPresent(request, "x-msh-device-id");
                ApplyHeaderIfPresent(request, "x-language");

                using (var reqStream = await request.GetRequestStreamAsync())
                {
                    await reqStream.WriteAsync(requestBytes, 0, requestBytes.Length);
                }

                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var respStream = response.GetResponseStream())
                {
                    if (respStream == null)
                    {
                        onLog("Direct request failed: empty response stream");
                        return false;
                    }

                    string contentType = (response.ContentType ?? "").ToLower();
                    if (contentType.Contains("application/connect+json"))
                    {
                        await ProcessConnectJsonResponseAsync(respStream, onData);
                    }
                    else
                    {
                        using (var reader = new StreamReader(respStream, Encoding.UTF8))
                        {
                            string text = await reader.ReadToEndAsync();
                            if (!string.IsNullOrWhiteSpace(text))
                                onData(text.EndsWith("\n") ? text : text + "\n");
                        }
                    }
                }

                onDone();
                return true;
            }
            catch (Exception ex)
            {
                onLog($"Direct request failed: {ex.Message}, fallback to UI");
                return false;
            }
        }

        public string GetNetworkInterceptorScript()
        {
            return @"
(() => {
    function sendToBridge(text) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(text);
        }
    }

    function log(msg) {
        sendToBridge('[LOG] ' + msg);
    }

    var kimiGetUrlInfo = function(rawUrl) {
        try {
            var parsed = new URL(rawUrl, window.location.origin);
            return { hostname: (parsed.hostname || '').toLowerCase(), pathname: (parsed.pathname || '').toLowerCase() };
        } catch (e) {
            return { hostname: '', pathname: (rawUrl || '').toLowerCase() };
        }
    };

    var kimiShouldCapture = function(url, method) {
        if ((method || 'GET').toUpperCase() !== 'POST') return false;
        var info = kimiGetUrlInfo(url);
        if (info.hostname.indexOf('kimi.moonshot.cn') !== -1 || info.hostname.indexOf('moonshot.cn') !== -1 || info.hostname.indexOf('kimi.com') !== -1) return true;
        if (info.pathname.indexOf('/apiv2/kimi.gateway.chat.v1.chatservice/chat') !== -1) return true;
        return false;
    };

    var kimiConcatBytes = function(left, right) {
        if (!left || left.length === 0) return right;
        var merged = new Uint8Array(left.length + right.length);
        merged.set(left, 0);
        merged.set(right, left.length);
        return merged;
    };

    var kimiParseConnectFrames = function(buffer) {
        var frames = [];
        var offset = 0;
        while (buffer && offset + 5 <= buffer.length) {
            var flags = buffer[offset];
            var length = (buffer[offset + 1] << 24) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 8) | buffer[offset + 4];
            if (length < 0 || offset + 5 + length > buffer.length) break;
            frames.push({ flags: flags, payload: buffer.slice(offset + 5, offset + 5 + length) });
            offset += 5 + length;
        }
        return { frames: frames, remaining: buffer.slice(offset) };
    };

    var kimiReadResponse = async function(response, sourceTag, url) {
        try {
            var contentType = (response.headers.get('content-type') || '').toLowerCase();
            var clone = response.clone();
            var reader = clone.body && clone.body.getReader ? clone.body.getReader() : null;
            if (!reader) {
                var text = await clone.text();
                if (text) {
                    sendToBridge('[NETWORK_DATA]' + text + '\n');
                    sendToBridge('[NETWORK_DONE]');
                }
                return;
            }
            if (contentType.indexOf('application/connect+json') !== -1) {
                var buffer = new Uint8Array(0);
                var decoder = new TextDecoder();
                while (true) {
                    var readResult = await reader.read();
                    if (readResult.done) break;
                    if (!readResult.value || readResult.value.length === 0) continue;
                    buffer = kimiConcatBytes(buffer, readResult.value);
                    var parsed = kimiParseConnectFrames(buffer);
                    buffer = parsed.remaining;
                    parsed.frames.forEach(function(frame) {
                        if ((frame.flags & 0x02) === 0x02) return;
                        var text = decoder.decode(frame.payload);
                        if (text) {
                            sendToBridge('[NETWORK_DATA]' + text + '\n');
                        }
                    });
                }
                sendToBridge('[NETWORK_DONE]');
                return;
            }
            var decoder = new TextDecoder();
            while (true) {
                var readResult = await reader.read();
                if (readResult.done) {
                    sendToBridge('[NETWORK_DONE]');
                    break;
                }
                var text = decoder.decode(readResult.value, { stream: true });
                if (text) {
                    sendToBridge('[NETWORK_DATA]' + text);
                }
            }
        } catch (e) {}
    };

    var originalFetch = window.fetch;
    window.fetch = async function(...args) {
        var url = '';
        var method = 'GET';
        try {
            if (args[0] instanceof Request) { url = args[0].url; method = args[0].method || 'GET'; }
            else { url = args[0].toString(); if (args[1] && args[1].method) method = args[1].method; }
        } catch(e) { url = 'unknown'; }
        if (kimiShouldCapture(url, method)) {
            try {
                var response = await originalFetch.apply(this, args);
                kimiReadResponse(response, 'Fetch', url);
                return response;
            } catch (e) { return originalFetch.apply(this, args); }
        }
        return originalFetch.apply(this, args);
    };

    var OriginalXHR = window.XMLHttpRequest;
    window.XMLHttpRequest = function() {
        var xhr = new OriginalXHR();
        var url = '';
        var method = 'GET';
        var originalOpen = xhr.open;
        xhr.open = function(requestMethod, requestUrl) {
            method = requestMethod || 'GET';
            url = requestUrl || '';
            return originalOpen.apply(this, arguments);
        };
        xhr.addEventListener('progress', function() {
            if (!kimiShouldCapture(url, method)) return;
            try {
                var fullText = '';
                try { fullText = xhr.responseText; } catch(e) { return; }
                if (!fullText) return;
                var lastLen = xhr._lastLength || 0;
                var newChunk = fullText.substring(lastLen);
                if (newChunk.length > 0) {
                    sendToBridge('[NETWORK_DATA]' + newChunk);
                    xhr._lastLength = fullText.length;
                }
            } catch(e) {}
        });
        xhr.addEventListener('load', function() {
            if (kimiShouldCapture(url, method)) {
                try {
                    var fullText = xhr.responseText || '';
                    var lastLen = xhr._lastLength || 0;
                    var newChunk = fullText.substring(lastLen);
                    if (newChunk.length > 0) {
                        sendToBridge('[NETWORK_DATA]' + newChunk);
                        xhr._lastLength = fullText.length;
                    }
                } catch(e) {}
                sendToBridge('[NETWORK_DONE]');
            }
        });
        return xhr;
    };

    const OriginalEventSource = window.EventSource;
    window.EventSource = function(url, config) {
        const es = new OriginalEventSource(url, config);
        const origAddEventListener = es.addEventListener;
        es.addEventListener = function(type, listener, options) {
            const wrappedListener = function(event) {
                sendToBridge('[NETWORK_DATA]' + ('event:' + type + '\ndata:' + (typeof event.data === 'string' ? event.data : JSON.stringify(event.data)) + '\n\n'));
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

    log('KimiAdapter 网络拦截已启用(Fetch+XHR+EventSource+connect+json)');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    function kimiSetContentEditableText(editor, text) {{
        function escapeHtml(value) {{
            return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        }}
        editor.focus();
        editor.innerHTML = '<p><br></p>';
        try {{
            var selection = window.getSelection();
            var range = document.createRange();
            range.selectNodeContents(editor);
            range.collapse(true);
            selection.removeAllRanges();
            selection.addRange(range);
        }} catch (e) {{}}
        var inserted = false;
        try {{ inserted = document.execCommand('insertText', false, text); }} catch (e) {{}}
        if (!inserted || !(editor.innerText || '').trim()) {{
            var lines = text.split(/\r?\n/);
            editor.innerHTML = lines.map(function(line) {{ return '<p>' + (line ? escapeHtml(line) : '<br>') + '</p>'; }}).join('');
        }}
        editor.dispatchEvent(new Event('input', {{ bubbles: true }}));
    }}

    var input = document.querySelector('.chat-input-editor[contenteditable=""true""]')
        || document.querySelector('[contenteditable=""true""][data-lexical-editor=""true""]')
        || document.getElementById('chat-input')
        || document.querySelector('textarea');

    if (!input) {{
        return {{ success: false, error: '找不到输入框' }};
    }}

    var isContentEditable = input.getAttribute('contenteditable') === 'true';
    if (isContentEditable) {{
        kimiSetContentEditableText(input, '{escapedPrompt}');
    }} else {{
        var nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
        nativeSetter.call(input, '{escapedPrompt}');
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.focus();
    }}

    await sleep(600);

    var sendBtn = document.querySelector('.send-button-container:not(.disabled)')
        || document.querySelector('.send-button-container')
        || document.querySelector('div[role=""button""].ds-send-button')
        || document.querySelector('div[role=""button""][aria-disabled=""false""]');

    if (!sendBtn) {{
        var btns = document.querySelectorAll('div[role=""button""]');
        if (btns.length > 0) sendBtn = btns[btns.length - 1];
    }}

    if (sendBtn) {{
        sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
        return {{ success: true, method: 'button-click' }};
    }}

    return {{ success: false, error: '找不到发送按钮' }};
}})();
";
            return script;
        }

        public async Task InjectPromptAsync(WebView2 webView, string prompt)
        {
            if (webView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 未初始化");
            string script = GetDomControlScript(prompt);
            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        public Common.SseParser.SseEvent? ParseSseData(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return null;
            var parser = new SseParser();
            return parser.Parse(rawLine);
        }

        public string GetModeSwitchScript(bool deepThink, bool search)
        {
            string srFlag = search ? "true" : "false";
            string dtFlag = deepThink ? "true" : "false";
            return $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}
    var buttons = document.querySelectorAll('div[role=""button""]');
    for (var i = 0; i < buttons.length; i++) {{
        var btn = buttons[i];
        var text = btn.innerText || '';
        if (text.includes('深度思考')) {{
            var isSelected = btn.classList.contains('ds-toggle-button--selected');
            if ({dtFlag} && !isSelected) {{ btn.click(); await sleep(300); }}
            else if (!{dtFlag} && isSelected) {{ btn.click(); await sleep(300); }}
            break;
        }}
    }}
    if ({srFlag}) {{
        var searchBtn = null;
        for (var j = 0; j < buttons.length; j++) {{
            var b = buttons[j];
            var t = b.innerText || '';
            if (t.includes('联网搜索') || t.includes('搜索')) {{ searchBtn = b; break; }}
        }}
        if (searchBtn) {{
            searchBtn.click();
            await sleep(500);
            var autoOption = document.querySelector('.connect-popover .connect-item, .n-popover__content.connect-popover .connect-item');
            if (autoOption) {{
                autoOption.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
                autoOption.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
                autoOption.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
            }}
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
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataJson);

                string? messageType = null;
                if (data.TryGetProperty("type", out var typeProp))
                    messageType = typeProp.GetString();

                if (string.Equals(messageType, "THINK", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.TryGetProperty("content", out var tcProp)) return tcProp.GetString();
                }
                if (string.Equals(messageType, "RESPONSE", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.TryGetProperty("content", out var rcProp)) return rcProp.GetString();
                }

                if (data.TryGetProperty("p", out var pProp) && data.TryGetProperty("v", out var vProp))
                {
                    string p = pProp.GetString() ?? "";
                    string v = vProp.GetString() ?? "";
                    if (p.Contains("response/fragments/") && p.Contains("/content")) return v;
                    if (p.Contains("/text/content") || p.Contains("text.content")) return v;
                }

                if (data.TryGetProperty("mask", out var maskProp))
                {
                    string mask = maskProp.GetString() ?? "";
                    if (mask == "block.text" || mask == "block.text.content")
                    {
                        if (data.TryGetProperty("text", out var textObj))
                        {
                            if (textObj.TryGetProperty("content", out var tcProp2)) return tcProp2.GetString();
                        }
                    }
                }

                if (data.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var firstChoice = choicesProp.EnumerateArray().FirstOrDefault();
                    if (firstChoice.TryGetProperty("delta", out var deltaProp))
                    {
                        if (deltaProp.TryGetProperty("content", out var dcProp)) return dcProp.GetString();
                    }
                }

                if (data.TryGetProperty("text", out var tProp)) return tProp.GetString();
                if (data.TryGetProperty("content", out var cProp)) return cProp.GetString();
                if (data.TryGetProperty("v", out var vProp2) && vProp2.ValueKind == System.Text.Json.JsonValueKind.String)
                    return vProp2.GetString();
            }
            catch { }
            return null;
        }

        // --- Private helpers for WebResource capture & direct request ---

        private static string ExtractJsonPayloadFromConnectBody(byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length == 0) return "";
            if (rawBytes.Length >= 5)
            {
                int length = (rawBytes[1] << 24) | (rawBytes[2] << 16) | (rawBytes[3] << 8) | rawBytes[4];
                if (length >= 0 && 5 + length <= rawBytes.Length)
                    return Encoding.UTF8.GetString(rawBytes, 5, length);
            }
            return Encoding.UTF8.GetString(rawBytes);
        }

        private byte[] BuildDirectRequestBodyBytes(string prompt, bool useThink, bool useSearch)
        {
            if (_latestChatRequestBodyBytes == null || _latestChatRequestBodyBytes.Length == 0)
                return BuildColdStartDirectRequestBodyBytes(prompt, useThink, useSearch);

            string oldPromptEscaped = EscapeJson(_latestChatRequestPrompt ?? "");
            string newPromptEscaped = EscapeJson(prompt ?? "");
            string body = _latestChatRequestBody ?? "";

            if (!string.IsNullOrWhiteSpace(oldPromptEscaped))
                body = body.Replace(oldPromptEscaped, newPromptEscaped);

            body = Regex.Replace(body, "\"thinking\"\\s*:\\s*(true|false)", "\"thinking\":" + (useThink ? "true" : "false"));

            if (!useSearch)
                body = Regex.Replace(body, ",?\\s*\\{\\s*\"type\"\\s*:\\s*\"TOOL_TYPE_SEARCH\"\\s*,\\s*\"search\"\\s*:\\s*\\{\\s*\\}\\s*\\}", "");
            else if (!body.Contains("\"TOOL_TYPE_SEARCH\""))
                body = body.Replace("\"tools\":[]", "\"tools\":[{\"type\":\"TOOL_TYPE_SEARCH\",\"search\":{}}]");

            byte[] payloadBytes = Encoding.UTF8.GetBytes(body);

            if (_latestChatRequestBodyBytes.Length >= 5)
            {
                byte[] framedBytes = new byte[payloadBytes.Length + 5];
                framedBytes[0] = _latestChatRequestBodyBytes[0];
                framedBytes[1] = (byte)((payloadBytes.Length >> 24) & 0xFF);
                framedBytes[2] = (byte)((payloadBytes.Length >> 16) & 0xFF);
                framedBytes[3] = (byte)((payloadBytes.Length >> 8) & 0xFF);
                framedBytes[4] = (byte)(payloadBytes.Length & 0xFF);
                Buffer.BlockCopy(payloadBytes, 0, framedBytes, 5, payloadBytes.Length);
                return framedBytes;
            }
            return payloadBytes;
        }

        private byte[] BuildColdStartDirectRequestBodyBytes(string prompt, bool useThink, bool useSearch)
        {
            string escapedPrompt = EscapeJson(prompt ?? "");
            string toolsJson = useSearch ? "[{\"type\":\"TOOL_TYPE_SEARCH\",\"search\":{}}]" : "[]";
            string payload = "{" +
                "\"scenario\":\"SCENARIO_K2D5\"," +
                "\"tools\":" + toolsJson + "," +
                "\"message\":{" +
                    "\"role\":\"user\"," +
                    "\"blocks\":[{\"message_id\":\"\",\"text\":{\"content\":\"" + escapedPrompt + "\"}}]," +
                    "\"scenario\":\"SCENARIO_K2D5\"" +
                "}," +
                "\"options\":{\"thinking\":" + (useThink ? "true" : "false") + "}" +
            "}";

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
            byte[] framedBytes = new byte[payloadBytes.Length + 5];
            framedBytes[0] = 0x00;
            framedBytes[1] = (byte)((payloadBytes.Length >> 24) & 0xFF);
            framedBytes[2] = (byte)((payloadBytes.Length >> 16) & 0xFF);
            framedBytes[3] = (byte)((payloadBytes.Length >> 8) & 0xFF);
            framedBytes[4] = (byte)(payloadBytes.Length & 0xFF);
            Buffer.BlockCopy(payloadBytes, 0, framedBytes, 5, payloadBytes.Length);
            return framedBytes;
        }

        private string ExtractPromptFromTemplateBody(string bodyText)
        {
            if (string.IsNullOrWhiteSpace(bodyText)) return "";
            var roleContentRegex = new Regex("\"role\"\\s*:\\s*\"user\"[\\s\\S]*?\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            var match = roleContentRegex.Match(bodyText);
            if (match.Success) return UnescapeJson(match.Groups[1].Value);
            var promptRegex = new Regex("\"prompt\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            match = promptRegex.Match(bodyText);
            if (match.Success) return UnescapeJson(match.Groups[1].Value);
            return "";
        }

        private string GetHeaderValue(string name, string fallback)
        {
            if (_latestChatRequestHeaders.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
            if (string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_pageAuthToken))
                return "Bearer " + _pageAuthToken;
            if (string.Equals(name, "x-msh-device-id", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_pageDeviceId))
                return _pageDeviceId;
            if (string.Equals(name, "x-language", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_pageLanguage))
                return _pageLanguage;
            return fallback;
        }

        private void ApplyHeaderIfPresent(HttpWebRequest request, string name)
        {
            string? value;
            if (!_latestChatRequestHeaders.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
            {
                value = GetHeaderValue(name, "");
                if (string.IsNullOrWhiteSpace(value)) return;
            }
            request.Headers[name] = value;
        }

        private async Task ApplyCookiesAsync(WebView2 webView, HttpWebRequest request, string requestUrl)
        {
            if (webView.CoreWebView2 == null) return;
            try
            {
                var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(requestUrl);
                if (cookies == null || cookies.Count == 0) return;
                request.CookieContainer = new CookieContainer();
                foreach (var cookie in cookies)
                {
                    try
                    {
                        var netCookie = new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain);
                        if (cookie.Expires > DateTime.MinValue) netCookie.Expires = cookie.Expires;
                        netCookie.HttpOnly = cookie.IsHttpOnly;
                        netCookie.Secure = cookie.IsSecure;
                        request.CookieContainer.Add(netCookie);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private async Task ProcessConnectJsonResponseAsync(Stream respStream, Action<string> onData)
        {
            using (var ms = new MemoryStream())
            {
                await respStream.CopyToAsync(ms);
                byte[] data = ms.ToArray();
                int offset = 0;
                while (offset + 5 <= data.Length)
                {
                    byte flags = data[offset];
                    int length = (data[offset + 1] << 24) | (data[offset + 2] << 16) | (data[offset + 3] << 8) | data[offset + 4];
                    if (length < 0 || offset + 5 + length > data.Length) break;
                    string frameText = Encoding.UTF8.GetString(data, offset + 5, length);
                    if ((flags & 0x02) == 0x02)
                    {
                        offset += 5 + length;
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(frameText))
                        onData(frameText.EndsWith("\n") ? frameText : frameText + "\n");
                    offset += 5 + length;
                }
            }
        }

        // --- JSON helpers ---

        private string DecodeScriptResult(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return "";
            string text = raw;
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                text = text.Substring(1, text.Length - 2);
            return UnescapeJson(text);
        }

        private static string ExtractValueFromJson(string json, string key)
        {
            var regex = new Regex($"\"{key}\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            var match = regex.Match(json);
            if (match.Success) return UnescapeJson(match.Groups[1].Value);
            return null!;
        }

        private static string EscapeJson(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string UnescapeJson(string str)
        {
            return str.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static string EscapeForJs(string str)
        {
            return str.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}