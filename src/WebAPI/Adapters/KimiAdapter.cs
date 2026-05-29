using System.IO;
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

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "kimi-k2.5-fast", Name = "K2.5 快速" },
            new ModelInfo { Id = "kimi-k2.5-fast-search", Name = "K2.5 快速+搜索" },
            new ModelInfo { Id = "kimi-k2.5-thinking", Name = "K2.5 思考" },
            new ModelInfo { Id = "kimi-k2.5-thinking-search", Name = "K2.5 思考+搜索" },
            new ModelInfo { Id = "kimi-k2.5-agent", Name = "K2.5 Agent" },
            new ModelInfo { Id = "kimi-k2.5-agent-swarm", Name = "K2.5 Agent 集群" }
        };

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
            if (string.IsNullOrWhiteSpace(rawLine))
                return null;

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
                    if (data.TryGetProperty("content", out var tcProp))
                        return tcProp.GetString();
                }
                if (string.Equals(messageType, "RESPONSE", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.TryGetProperty("content", out var rcProp))
                        return rcProp.GetString();
                }

                if (data.TryGetProperty("p", out var pProp) && data.TryGetProperty("v", out var vProp))
                {
                    string p = pProp.GetString() ?? "";
                    string v = vProp.GetString() ?? "";
                    if (p.Contains("response/fragments/") && p.Contains("/content"))
                        return v;
                    if (p.Contains("/text/content") || p.Contains("text.content"))
                        return v;
                }

                if (data.TryGetProperty("mask", out var maskProp))
                {
                    string mask = maskProp.GetString() ?? "";
                    if (mask == "block.text" || mask == "block.text.content")
                    {
                        if (data.TryGetProperty("text", out var textObj))
                        {
                            if (textObj.TryGetProperty("content", out var tcProp2))
                                return tcProp2.GetString();
                        }
                    }
                }

                if (data.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var firstChoice = choicesProp.EnumerateArray().FirstOrDefault();
                    if (firstChoice.TryGetProperty("delta", out var deltaProp))
                    {
                        if (deltaProp.TryGetProperty("content", out var dcProp))
                            return dcProp.GetString();
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

        private string EscapeForJs(string str)
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