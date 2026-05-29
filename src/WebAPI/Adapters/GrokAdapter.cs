using System.IO;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class GrokAdapter : IAdapter
    {
        public string PlatformId => "grok";
        public string PlatformName => "Grok";
        public string TargetUrl => "https://grok.com/";
        public int DefaultPort => 56668;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Grok");

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "grok-4", Name = "Grok 4" },
            new ModelInfo { Id = "grok-4-thinking", Name = "Grok 4 Thinking" },
            new ModelInfo { Id = "grok-3", Name = "Grok 3" }
        };

        public string GetNetworkInterceptorScript()
        {
            return @"
(() => {
    const originalFetch = window.fetch;

    function isChatResponse(url) {
        if (typeof url !== 'string') return false;
        if (url.includes('grok.com') || url.includes('x.ai') || url.includes('x.com/grok')) return true;
        if (!url.includes('/api/') && !url.includes('/chat/') && !url.includes('/stream/') && !url.includes('/v1/')) return false;
        var isStatic = url.match(/\.(js|css|png|jpg|jpeg|gif|svg|woff|woff2|ttf|ico)$/);
        if (isStatic) return false;
        if (url.includes('rephrase') || url.includes('rewrite') || url.includes('search_query') || url.includes('query_rewrite')) return false;
        if (url.includes('suggest') || url.includes('recommend') || url.includes('feedback') || url.includes('log')) return false;
        if (url.includes('config') || url.includes('setting') || url.includes('abtest') || url.includes('feature')) return false;
        return true;
    }

    window.fetch = async function(...args) {
        const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
        try {
            const response = await originalFetch.apply(this, args);

            if (isChatResponse(url)) {
                const clone = response.clone();
                (async () => {
                    const reader = clone.body.getReader();
                    const decoder = new TextDecoder();
                    try {
                        while (true) {
                            const result = await reader.read();
                            if (result.done) break;
                            const chunk = decoder.decode(result.value, { stream: true });
                            if (window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({
                                    type: 'NETWORK_DATA',
                                    url: url,
                                    data: chunk
                                }));
                            }
                        }
                    } catch (e) {
                    } finally {
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(JSON.stringify({
                                type: 'NETWORK_DONE',
                                url: url
                            }));
                        }
                    }
                })();
            }

            return response;
        } catch (err) {
            return originalFetch.apply(this, args);
        }
    };

    const OriginalEventSource = window.EventSource;
    window.EventSource = function(url, config) {
        const es = new OriginalEventSource(url, config);
        const origAddEventListener = es.addEventListener;
        es.addEventListener = function(type, listener, options) {
            const wrappedListener = function(event) {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'NETWORK_DATA',
                        url: url,
                        data: 'event:' + type + '\ndata:' + (typeof event.data === 'string' ? event.data : JSON.stringify(event.data)) + '\n\n'
                    }));
                }
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

    console.log('[GrokAdapter] 网络拦截已启用(Fetch+EventSource)');
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
        'textarea[placeholder*=""message"" i]',
        'textarea[placeholder*=""ask"" i]',
        'textarea[placeholder*=""输入"" i]',
        'textarea[class*=""input""]',
        'textarea',
        'div[contenteditable=""true""]',
        'div[contenteditable]',
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

    if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {{
        const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
        if (nativeSetter) {{
            nativeSetter.call(input, '{escapedPrompt}');
        }} else {{
            input.value = '{escapedPrompt}';
        }}
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: '{escapedPrompt}', inputType: 'insertText' }}));
        input.dispatchEvent(new Event('change', {{ bubbles: true }}));
    }} else {{
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
    const buttons = document.querySelectorAll('button, [role=""button""]');
    for (const btn of buttons) {{
        if (!btn.offsetParent || btn.getBoundingClientRect().width < 20) continue;
        const ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        if (ariaLabel.includes('send') || ariaLabel.includes('发送')) {{
            sendBtn = btn;
            break;
        }}
        if (btn.querySelector('svg') && !ariaLabel) {{
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

    var modelBtns = document.querySelectorAll('button, [role=""tab""], div[class*=""model""], [data-testid]');
    var thinkBtn = null;
    for (var i = 0; i < modelBtns.length; i++) {{
        var btn = modelBtns[i];
        var text = (btn.textContent || '').toLowerCase();
        var ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        if (text.includes('think') || text.includes('reasoning') || text.includes('深度思考') ||
            ariaLabel.includes('think') || ariaLabel.includes('reasoning')) {{
            thinkBtn = btn;
            break;
        }}
    }}

    if (thinkBtn) {{
        var isActive = thinkBtn.classList.contains('active') || 
                      thinkBtn.classList.contains('selected') ||
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
        if (txt.includes('search') || txt.includes('搜索') || txt.includes('联网') ||
            aLabel.includes('search') || aLabel.includes('搜索')) {{
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

                if (data.TryGetProperty("response", out var respProp))
                {
                    if (respProp.TryGetProperty("text", out var rtProp))
                        return rtProp.GetString();
                    if (respProp.TryGetProperty("content", out var rcProp))
                        return rcProp.GetString();
                }

                if (data.TryGetProperty("token", out var tokenProp))
                    return tokenProp.GetString();

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