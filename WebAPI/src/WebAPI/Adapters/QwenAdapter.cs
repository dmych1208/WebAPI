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
    const originalFetch = window.fetch;
    const originalXhrOpen = XMLHttpRequest.prototype.open;
    const originalXhrSend = XMLHttpRequest.prototype.send;

    function shouldIntercept(url) {
        if (typeof url !== 'string') return false;
        return url.includes('/api/') || 
               url.includes('/chat/') || 
               url.includes('qianwen') ||
               url.includes('/completion') ||
               url.includes('/message') ||
               url.includes('/stream');
    }

    function postToHost(obj) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify(obj));
        }
    }

    window.fetch = async function(...args) {
        const url = args[0];
        try {
            const response = await originalFetch.apply(this, args);
            
            if (shouldIntercept(url)) {
                const contentType = response.headers.get('content-type') || '';
                if (contentType.includes('text/event-stream') || contentType.includes('stream')) {
                    const reader = response.clone().body.getReader();
                    const decoder = new TextDecoder();
                    (async () => {
                        try {
                            while (true) {
                                const { done, value } = await reader.read();
                                if (done) {
                                    postToHost({ type: 'NETWORK_DONE', url: url });
                                    break;
                                }
                                const chunk = decoder.decode(value, { stream: true });
                                postToHost({ type: 'NETWORK_DATA', url: url, data: chunk });
                            }
                        } catch (e) {
                            postToHost({ type: 'NETWORK_DONE', url: url });
                        }
                    })();
                } else {
                    const cloned = response.clone();
                    cloned.text().then(text => {
                        postToHost({ type: 'NETWORK_DATA', url: url, data: text });
                        postToHost({ type: 'NETWORK_DONE', url: url });
                    }).catch(() => {
                        postToHost({ type: 'NETWORK_DONE', url: url });
                    });
                }
            }
            
            return response;
        } catch (err) {
            return originalFetch.apply(this, args);
        }
    };

    XMLHttpRequest.prototype.open = function(method, url, ...rest) {
        this._url = url;
        return originalXhrOpen.apply(this, [method, url, ...rest]);
    };

    XMLHttpRequest.prototype.send = function(...args) {
        this.addEventListener('readystatechange', function() {
            if (this.readyState === 4 && this._url && shouldIntercept(this._url)) {
                postToHost({ type: 'NETWORK_DATA', url: this._url, data: this.responseText });
                postToHost({ type: 'NETWORK_DONE', url: this._url });
            }
        });
        return originalXhrSend.apply(this, args);
    };

    console.log('[QwenAdapter] 网络拦截已启用（流式）');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    function nativeInputValueSetter(el, value) {{
        const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value')?.set ||
            Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
        if (setter) {{
            setter.call(el, value);
        }} else {{
            el.value = value;
        }}
    }}

    function dispatchInputEvents(el) {{
        el.dispatchEvent(new Event('input', {{ bubbles: true, cancelable: true }}));
        el.dispatchEvent(new InputEvent('input', {{ bubbles: true, cancelable: true, inputType: 'insertText' }}));
        el.dispatchEvent(new Event('change', {{ bubbles: true, cancelable: true }}));
    }}

    let input = null;

    const chatArea = document.querySelector('.chat-input-container') ||
                     document.querySelector('[class*=""chat-input""]') ||
                     document.querySelector('[class*=""chatInput""]') ||
                     document.querySelector('.input-area') ||
                     document.querySelector('[class*=""inputArea""]') ||
                     document.querySelector('[class*=""input-area""]') ||
                     document.querySelector('.chat-container') ||
                     document.querySelector('[class*=""dialog""]') ||
                     document.querySelector('main') ||
                     document.body;

    const textareas = chatArea.querySelectorAll('textarea');
    for (const ta of textareas) {{
        const rect = ta.getBoundingClientRect();
        const placeholder = (ta.placeholder || '').toLowerCase();
        const cls = (ta.className || '').toLowerCase();
        if (rect.width > 100 && rect.height > 20 &&
            !placeholder.includes('搜索') && !placeholder.includes('查找') &&
            !cls.includes('search') && !cls.includes('filter')) {{
            input = ta;
            break;
        }}
    }}

    if (!input) {{
        const contentEditables = chatArea.querySelectorAll('[contenteditable=""true""]');
        for (const ce of contentEditables) {{
            const rect = ce.getBoundingClientRect();
            const cls = (ce.className || '').toLowerCase();
            if (rect.width > 100 && rect.height > 20 &&
                !cls.includes('search') && !cls.includes('filter') &&
                !cls.includes('history') && !cls.includes('sidebar')) {{
                input = ce;
                break;
            }}
        }}
    }}

    if (!input) {{
        const allTa = document.querySelectorAll('textarea');
        for (const ta of allTa) {{
            const rect = ta.getBoundingClientRect();
            const placeholder = (ta.placeholder || '').toLowerCase();
            if (rect.width > 100 && rect.height > 20 &&
                !placeholder.includes('搜索') && !placeholder.includes('查找')) {{
                input = ta;
                break;
            }}
        }}
    }}

    if (!input) {{
        return {{ success: false, error: '找不到输入框' }};
    }}

    input.focus();
    await sleep(100);

    if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {{
        input.value = '';
        dispatchInputEvents(input);
        await sleep(50);

        nativeInputValueSetter(input, '{escapedPrompt}');
        dispatchInputEvents(input);

        if (input.setSelectionRange) {{
            input.setSelectionRange(input.value.length, input.value.length);
        }}
    }} else {{
        input.innerHTML = '';
        const textNode = document.createTextNode('{escapedPrompt}');
        input.appendChild(textNode);

        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(input);
        range.collapse(false);
        selection?.removeAllRanges();
        selection?.addRange(range);

        dispatchInputEvents(input);
    }}

    await sleep(600);

    let sendBtn = null;

    const inputContainer = input.closest('[class*=""input""]') ||
                           input.closest('[class*=""chat""]') ||
                           input.parentElement?.parentElement ||
                           document.body;

    const btnsInContainer = inputContainer.querySelectorAll('button, [role=""button""]');
    for (const btn of btnsInContainer) {{
        if (btn.offsetParent === null || btn.getBoundingClientRect().width === 0) continue;
        const txt = (btn.textContent || '').toLowerCase().trim();
        const ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        const cls = (btn.className || '').toLowerCase();
        const title = (btn.getAttribute('title') || '').toLowerCase();
        const isDisabled = btn.disabled || btn.getAttribute('aria-disabled') === 'true';

        if (isDisabled) continue;

        if (txt.includes('发送') || txt.includes('send') || txt.includes('submit') ||
            ariaLabel.includes('发送') || ariaLabel.includes('send') ||
            title.includes('发送') || title.includes('send') ||
            cls.includes('send') || cls.includes('submit')) {{
            sendBtn = btn;
            break;
        }}
    }}

    if (!sendBtn) {{
        const allBtns = document.querySelectorAll('button, [role=""button""]');
        for (const btn of allBtns) {{
            if (btn.offsetParent === null || btn.getBoundingClientRect().width === 0) continue;
            const ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
            const title = (btn.getAttribute('title') || '').toLowerCase();
            const cls = (btn.className || '').toLowerCase();
            const isDisabled = btn.disabled || btn.getAttribute('aria-disabled') === 'true';

            if (isDisabled) continue;

            if (ariaLabel.includes('发送') || ariaLabel.includes('send') ||
                title.includes('发送') || title.includes('send') ||
                cls.includes('send-btn') || cls.includes('submit-btn')) {{
                sendBtn = btn;
                break;
            }}
        }}
    }}

    if (sendBtn) {{
        sendBtn.focus();
        sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
        sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
        return {{ success: true, method: 'button-click' }};
    }}

    input.focus();
    input.dispatchEvent(new KeyboardEvent('keydown', {{
        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
        shiftKey: false, bubbles: true, cancelable: true
    }}));
    await sleep(50);
    input.dispatchEvent(new KeyboardEvent('keypress', {{
        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
        shiftKey: false, bubbles: true, cancelable: true
    }}));
    await sleep(50);
    input.dispatchEvent(new KeyboardEvent('keyup', {{
        key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
        shiftKey: false, bubbles: true, cancelable: true
    }}));
    return {{ success: true, method: 'enter' }};
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