using System.IO;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class QwenAdapter : IAdapter
    {
        public string PlatformId => "qwen";
        public string PlatformName => "通义千问";
        public string TargetUrl => "https://tongyi.aliyun.com/qianwen/";
        public int DefaultPort => 56666;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Qwen");

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "qwen-turbo", Name = "千问加速" },
            new ModelInfo { Id = "qwen-plus", Name = "千问增强" },
            new ModelInfo { Id = "qwen-max", Name = "千问旗舰" },
            new ModelInfo { Id = "qwen-max-long", Name = "千问长文本" }
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
        if (u.indexOf('qianwen.aliyun.com') !== -1 || u.indexOf('tongyi.aliyun.com') !== -1) {
            var isStatic = url.match(/\.(js|css|png|jpg|jpeg|gif|svg|woff|woff2|ttf|ico)$/);
            if (isStatic) return false;
            return true;
        }
        if (u.indexOf('/api/') !== -1 || u.indexOf('/chat/') !== -1 || u.indexOf('/qianwen/') !== -1 || u.indexOf('/stream/') !== -1 || u.indexOf('/v1/') !== -1) {
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

    function hideAds() {
        var existing = document.getElementById('qwen-ad-block-style');
        if (!existing) {
            var style = document.createElement('style');
            style.id = 'qwen-ad-block-style';
            style.textContent = '.bg-pc-sidebar, [class*=''bg-pc-sidebar''] { display: none !important; }';
            document.head.appendChild(style);
        }
        document.querySelectorAll('.bg-pc-sidebar, [class*=''bg-pc-sidebar'']').forEach(function(el) {
            el.style.display = 'none';
        });
    }
    hideAds();

    const observer = new MutationObserver(() => {
        hideAds();
    });

    setTimeout(() => {
        observer.observe(document.body, { childList: true, subtree: true });
    }, 1000);

    log('QwenAdapter 网络拦截已启用(Fetch+XHR+EventSource)');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    var attempts = 0;
    var maxAttempts = 30;

    function tryFindInput() {{
        attempts++;

        var input = document.querySelector('div[contenteditable=""true""]') ||
                    document.querySelector('textarea[placeholder*=""输入""]') ||
                    document.querySelector('textarea[placeholder*=""说点什么""]') ||
                    document.querySelector('textarea[placeholder*=""message""]') ||
                    document.querySelector('textarea') ||
                    document.querySelector('div[contenteditable]') ||
                    document.querySelector('.chat-input textarea') ||
                    document.querySelector('.input-box textarea');

        if (!input) {{
            if (attempts < maxAttempts) {{
                setTimeout(tryFindInput, 500);
                return;
            }}
            return;
        }}

        input.focus();

        var rect = input.getBoundingClientRect();
        var mx = rect.left + rect.width / 2;
        var my = rect.top + rect.height / 2;

        input.dispatchEvent(new MouseEvent('mousemove', {{ clientX: mx, clientY: my, bubbles: true }}));
        input.dispatchEvent(new MouseEvent('mousedown', {{ clientX: mx, clientY: my, bubbles: true }}));
        input.dispatchEvent(new MouseEvent('mouseup', {{ clientX: mx, clientY: my, bubbles: true }}));
        input.dispatchEvent(new MouseEvent('click', {{ clientX: mx, clientY: my, bubbles: true, cancelable: true }}));

        if (input.getAttribute('contenteditable') === 'true') {{
            input.innerHTML = '';
        }} else {{
            input.value = '';
        }}

        input.dispatchEvent(new InputEvent('beforeinput', {{
            bubbles: true, cancelable: true, inputType: 'deleteContentBackward'
        }}));

        input.dispatchEvent(new CompositionEvent('compositionstart', {{
            data: '', bubbles: true, cancelable: true
        }}));

        var idx = 0;
        var text = '{escapedPrompt}';

        function nextChar() {{
            if (idx >= text.length) {{
                input.dispatchEvent(new CompositionEvent('compositionend', {{
                    data: text, bubbles: true, cancelable: true
                }}));
                input.dispatchEvent(new Event('change', {{ bubbles: true, cancelable: true }}));
                input.dispatchEvent(new FocusEvent('focus', {{ bubbles: true }}));

                setTimeout(function() {{
                    input.focus();

                    var enterKD = new KeyboardEvent('keydown', {{
                        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
                        bubbles: true, cancelable: true, isComposing: false
                    }});
                    var enterKP = new KeyboardEvent('keypress', {{
                        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
                        bubbles: true, cancelable: true, isComposing: false
                    }});
                    var enterKU = new KeyboardEvent('keyup', {{
                        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
                        bubbles: true, cancelable: true, isComposing: false
                    }});

                    input.dispatchEvent(enterKD);
                    input.dispatchEvent(enterKP);
                    input.dispatchEvent(enterKU);

                    setTimeout(function() {{
                        var buttons = document.querySelectorAll('button');
                        var sendBtn = null;

                        for (var b = 0; b < buttons.length; b++) {{
                            var btn = buttons[b];
                            var txt2 = (btn.textContent || '').toLowerCase();
                            if (txt2.indexOf('发送') !== -1 || txt2.indexOf('send') !== -1) {{
                                sendBtn = btn;
                                break;
                            }}
                            var hasSvg = btn.querySelector('svg');
                            var cls = (btn.className || '').toLowerCase();
                            if (!sendBtn && (hasSvg || cls.indexOf('send') !== -1 || cls.indexOf('submit') !== -1)) {{
                                sendBtn = btn;
                            }}
                        }}

                        if (sendBtn) {{
                            if (sendBtn.disabled) sendBtn.disabled = false;
                            var br = sendBtn.getBoundingClientRect();
                            sendBtn.dispatchEvent(new MouseEvent('mousedown', {{
                                clientX: br.left + br.width/2, clientY: br.top + br.height/2, bubbles: true
                            }}));
                            sendBtn.dispatchEvent(new MouseEvent('mouseup', {{
                                clientX: br.left + br.width/2, clientY: br.top + br.height/2, bubbles: true
                            }}));
                            sendBtn.dispatchEvent(new MouseEvent('click', {{
                                clientX: br.left + br.width/2, clientY: br.top + br.height/2, bubbles: true, cancelable: true
                            }}));
                            input.dispatchEvent(enterKD);
                            input.dispatchEvent(enterKP);
                            input.dispatchEvent(enterKU);
                        }}
                    }}, 1000);
                }}, 1000);
                return;
            }}

            var ch = text[idx];
            input.dispatchEvent(new CompositionEvent('compositionupdate', {{
                data: ch, bubbles: true, cancelable: true
            }}));

            if (input.getAttribute('contenteditable') === 'true') {{
                input.innerHTML += ch;
            }} else {{
                input.value += ch;
            }}

            input.dispatchEvent(new InputEvent('input', {{
                bubbles: true, cancelable: true, inputType: 'insertText', data: ch
            }}));

            idx++;
            var delay = Math.random() * 50 + 20;
            setTimeout(nextChar, delay);
        }}

        nextChar();
    }}

    tryFindInput();
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

        private string _lastExtractedContent = "";

        public string? ExtractContentFromSseData(string dataJson, string eventType)
        {
            if (string.IsNullOrEmpty(dataJson)) return null;

            try
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataJson);

                if (data.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (dataProp.TryGetProperty("messages", out var msgsProp) && msgsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var msgs = msgsProp.EnumerateArray();
                        foreach (var msg in msgs.Reverse())
                        {
                            if (msg.TryGetProperty("content", out var cProp) && cProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string content = cProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(content) && content != _lastExtractedContent)
                                {
                                    _lastExtractedContent = content;
                                    return content;
                                }
                            }
                        }
                    }

                    if (dataProp.TryGetProperty("text", out var tProp)) return tProp.GetString();
                    if (dataProp.TryGetProperty("content", out var cProp2)) return cProp2.GetString();
                    if (dataProp.TryGetProperty("delta", out var dProp)) return dProp.GetString();
                }

                if (data.TryGetProperty("communication", out var commProp) && commProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (commProp.TryGetProperty("text", out var tProp)) return tProp.GetString();
                    if (commProp.TryGetProperty("content", out var cProp)) return cProp.GetString();
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

                if (data.TryGetProperty("text", out var textProp)) return textProp.GetString();
                if (data.TryGetProperty("content", out var contentProp)) return contentProp.GetString();
                if (data.TryGetProperty("delta", out var deltaProp2) && deltaProp2.ValueKind == System.Text.Json.JsonValueKind.String)
                    return deltaProp2.GetString();
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