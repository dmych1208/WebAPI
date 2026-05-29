using System.IO;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class YuanbaoAdapter : IAdapter
    {
        public string PlatformId => "yuanbao";
        public string PlatformName => "元宝增强";
        public string TargetUrl => "https://yuanbao.tencent.com/chat/";
        public int DefaultPort => 56669;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Yuanbao");

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "yuanbao-pro", Name = "元宝Pro" },
            new ModelInfo { Id = "yuanbao-turbo", Name = "元宝Turbo" },
            new ModelInfo { Id = "yuanbao-deepseek", Name = "元宝DeepSeek" }
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

    const originalFetch = window.fetch;

    function isChatResponse(url) {
        if (typeof url !== 'string') return false;
        var u = url.toLowerCase();
        if (u.indexOf('yuanbao.tencent.com') !== -1 || u.indexOf('hunyuan.cloud.tencent.com') !== -1) {
            var isStatic = url.match(/\.(js|css|png|jpg|jpeg|gif|svg|woff|woff2|ttf|ico)$/);
            if (isStatic) return false;
            return true;
        }
        return false;
    }

    window.fetch = async function(...args) {
        const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
        try {
            const response = await originalFetch.apply(this, args);

            if (isChatResponse(url) && response.body) {
                const clone = response.clone();
                (async () => {
                    try {
                        const reader = clone.body.getReader();
                        const decoder = new TextDecoder();
                        while (true) {
                            var result = await reader.read();
                            if (result.done) {
                                sendToBridge('[NETWORK_DONE]');
                                break;
                            }
                            var chunk = decoder.decode(result.value, { stream: true });
                            sendToBridge('[NETWORK_DATA]' + chunk);
                        }
                    } catch (e) {}
                })();
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
                    sendToBridge('[NETWORK_DATA]' + newChunk);
                    xhr._lastLength = fullText.length;
                }
            } catch(e) {}
        });

        xhr.addEventListener('load', function() {
            if (isChatResponse(url)) {
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

    log('YuanbaoAdapter 网络拦截已启用(Fetch+XHR+EventSource)');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    let input = null;
    const selectors = [
        'div[contenteditable=""true""]',
        'textarea[placeholder*=""输入"" i]',
        'textarea[placeholder*=""message"" i]',
        'textarea[placeholder*=""ask"" i]',
        'textarea[class*=""input""]',
        'textarea',
        '[role=""textbox""]',
        'div[class*=""chat-input""]',
        'div[class*=""input-box""] div[contenteditable]'
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
        if (nativeSetter) {{
            nativeSetter.call(input, '{escapedPrompt}');
        }} else {{
            input.value = '{escapedPrompt}';
        }}
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: '{escapedPrompt}', inputType: 'insertText' }}));
        input.dispatchEvent(new Event('change', {{ bubbles: true }}));
    }}

    await sleep(500);

    const enterKeydown = new KeyboardEvent('keydown', {{
        key: 'Enter',
        code: 'Enter',
        keyCode: 13,
        which: 13,
        bubbles: true,
        cancelable: true
    }});
    const enterKeyup = new KeyboardEvent('keyup', {{
        key: 'Enter',
        code: 'Enter',
        keyCode: 13,
        which: 13,
        bubbles: true,
        cancelable: true
    }});
    input.dispatchEvent(enterKeydown);
    await sleep(50);
    input.dispatchEvent(enterKeyup);

    await sleep(500);

    let sendBtn = null;
    const buttons = document.querySelectorAll('button, [role=""button""], div[class*=""send""]');
    for (const btn of buttons) {{
        if (!btn.offsetParent || btn.getBoundingClientRect().width < 20) continue;
        const ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        const text = (btn.textContent || '').toLowerCase();
        const cls = (btn.className || '').toLowerCase();
        if (ariaLabel.includes('send') || ariaLabel.includes('发送') || 
            text.includes('send') || text.includes('发送') ||
            cls.includes('send') || cls.includes('submit')) {{
            sendBtn = btn;
            break;
        }}
        if (btn.querySelector('svg') && !ariaLabel && !text && cls.length < 20) {{
            sendBtn = btn;
            break;
        }}
    }}

    if (sendBtn) {{
        if (sendBtn.disabled) sendBtn.disabled = false;
        if (sendBtn.hasAttribute('aria-disabled')) sendBtn.removeAttribute('aria-disabled');
        sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
    }}

    return {{ success: true, method: sendBtn ? 'button-click' : 'enter' }};
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

    var modelBtns = document.querySelectorAll('button, [role=""tab""], div[class*=""model""], div[class*=""switch""], [data-testid]');
    var thinkBtn = null;
    for (var i = 0; i < modelBtns.length; i++) {{
        var btn = modelBtns[i];
        var text = (btn.textContent || '').toLowerCase();
        var ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        if (text.includes('deepseek') || text.includes('r1') || text.includes('深度思考') || text.includes('推理') ||
            ariaLabel.includes('deepseek') || ariaLabel.includes('深度思考')) {{
            thinkBtn = btn;
            break;
        }}
    }}

    if (thinkBtn) {{
        var isActive = thinkBtn.classList.contains('active') || 
                      thinkBtn.classList.contains('selected') ||
                      thinkBtn.classList.contains('checked') ||
                      thinkBtn.getAttribute('aria-checked') === 'true' ||
                      thinkBtn.getAttribute('aria-selected') === 'true';
        if ({dtFlag} !== isActive) {{
            thinkBtn.click();
            await sleep(300);
        }}
    }}

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
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataJson);

                if (data.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var firstChoice = choicesProp.EnumerateArray().FirstOrDefault();
                    if (firstChoice.TryGetProperty("delta", out var deltaProp))
                    {
                        if (deltaProp.TryGetProperty("content", out var dcProp))
                            return dcProp.GetString();
                        if (deltaProp.TryGetProperty("reasoning_content", out var rcProp))
                            return rcProp.GetString();
                    }
                    if (firstChoice.TryGetProperty("message", out var msgProp))
                    {
                        if (msgProp.TryGetProperty("content", out var mcProp))
                            return mcProp.GetString();
                    }
                }

                if (data.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "text" || type == "content")
                    {
                        if (data.TryGetProperty("text", out var ttProp)) return ttProp.GetString();
                        if (data.TryGetProperty("content", out var tcProp)) return tcProp.GetString();
                        if (data.TryGetProperty("msg", out var tmProp)) return tmProp.GetString();
                    }
                    if (type == "think" || type == "thinking" || type == "search_result" || type == "search")
                        return null;
                }

                if (data.TryGetProperty("result", out var resultProp))
                {
                    if (resultProp.TryGetProperty("text", out var rtProp)) return rtProp.GetString();
                    if (resultProp.TryGetProperty("content", out var rcProp)) return rcProp.GetString();
                }

                if (data.TryGetProperty("text", out var tProp)) return tProp.GetString();
                if (data.TryGetProperty("content", out var cProp)) return cProp.GetString();
                if (data.TryGetProperty("delta", out var dProp) && dProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    return dProp.GetString();
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