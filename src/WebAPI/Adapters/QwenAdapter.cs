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
        public string TargetUrl => "https://www.qianwen.com/?source=tongyigw";
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

    window.fetch = async function(...args) {
        const url = args[0];
        try {
            const response = await originalFetch.apply(this, args);
            
            if (typeof url === 'string' && (url.includes('/api/') || url.includes('/chat/'))) {
                const cloned = response.clone();
                const text = await cloned.text();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'NETWORK_DATA',
                        url: url,
                        data: text
                    }));
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
            if (this.readyState === 4 && this._url && (this._url.includes('/api/') || this._url.includes('/chat/'))) {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'NETWORK_DATA',
                        url: this._url,
                        data: this.responseText
                    }));
                }
            }
        });
        return originalXhrSend.apply(this, args);
    };

    const observer = new MutationObserver((mutations) => {
        for (const mut of mutations) {
            for (const node of mut.addedNodes) {
                if (node.nodeType === 1 && node.textContent && node.textContent.length > 10) {
                }
            }
        }
    });

    setTimeout(() => {
        observer.observe(document.body, { childList: true, subtree: true });
    }, 1000);

    console.log('[QwenAdapter] 网络拦截已启用');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    function nativeInputValueSetter(Object, value) {{
        const nativeInputValueSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value')?.set ||
            Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
        if (nativeInputValueSetter) {{
            nativeInputValueSetter.call(Object, value);
        }} else {{
            Object.value = value;
        }}
    }}

    function dispatchInputEvent(el, value) {{
        const inputEvt = new InputEvent('input', {{
            bubbles: true,
            cancelable: true,
            data: value,
            inputType: 'insertText'
        }});
        el.dispatchEvent(new Event('input', {{ bubbles: true, cancelable: true }}));
        el.dispatchEvent(inputEvt);
        el.dispatchEvent(new Event('change', {{ bubbles: true, cancelable: true }}));
    }}

    let input = null;
    const selectors = [
        'textarea',
        '[contenteditable=""true""]',
        '[contenteditable]',
        '[role=""textbox""]',
        'div[contenteditable]',
        'textarea[placeholder*=""输入""]',
        'textarea[placeholder*=""请输入""]',
        'textarea[placeholder*=""问""]',
        'textarea[class*=""chat""]',
        'div[contenteditable=""true""]'
    ];

    for (const sel of selectors) {{
        const el = document.querySelector(sel);
        if (el && el.offsetParent !== null && el.getBoundingClientRect().width > 0) {{
            input = el;
            break;
        }}
    }}

    if (!input) {{
        const allInputs = document.querySelectorAll('textarea, div[contenteditable]');
        for (const el of allInputs) {{
            if (el.offsetParent !== null && el.getBoundingClientRect().width > 0) {{
                input = el;
                break;
            }}
        }}
    }}

    if (!input) {{
        return {{ success: false, error: '找不到输入框' }};
    }}

    input.focus();

    if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {{
        // 清空现有内容
        input.value = '';
        dispatchInputEvent(input, '');

        // 使用原生 setter 设置值（React 兼容）
        nativeInputValueSetter(input, '{escapedPrompt}');
        dispatchInputEvent(input, '{escapedPrompt}');

        // 移动光标到末尾
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

        dispatchInputEvent(input, '{escapedPrompt}');
    }}

    await sleep(500);

    let sendBtn = null;
    const btnSelectors = [
        'button[type=""submit""]',
        'button[aria-label*=""send"" i]',
        'button[aria-label*=""发送"" i]',
        'button:has(svg)',
        'button[class*=""chat"" i]',
        '[role=""button""]',
        'button'
    ];

    for (const sel of btnSelectors) {{
        const btns = document.querySelectorAll(sel);
        for (const btn of btns) {{
            if (btn.offsetParent !== null && btn.getBoundingClientRect().width > 0) {{
                let targetBtn = btn;
                if (btn.tagName === 'svg') {{
                    targetBtn = btn.closest('button') || btn.closest('[role=""button""]') || btn.parentElement;
                }}

                if (targetBtn && targetBtn.offsetParent !== null && targetBtn.getBoundingClientRect().width > 0) {{
                    const txt = (targetBtn.textContent || '').toLowerCase().trim();
                    const ariaLabel = (targetBtn.getAttribute('aria-label') || '').toLowerCase();
                    const isDisabled = targetBtn.disabled || targetBtn.getAttribute('aria-disabled') === 'true';

                    if (!isDisabled && (txt.includes('send') || txt.includes('发送') || txt.includes('submit') ||
                        ariaLabel.includes('send') || ariaLabel.includes('发送') ||
                        (targetBtn.querySelector('svg') && (txt.length < 5 || ariaLabel.length > 0)))) {{
                        sendBtn = targetBtn;
                        break;
                    }}
                }}
            }}
        }}
        if (sendBtn) break;
    }}

    if (!sendBtn) {{
        // 尝试按 Enter 键
        input.focus();
        const enterKeydown = new KeyboardEvent('keydown', {{
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
            bubbles: true, cancelable: true
        }});
        const enterKeyup = new KeyboardEvent('keyup', {{
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13,
            bubbles: true, cancelable: true
        }});
        input.dispatchEvent(enterKeydown);
        await sleep(50);
        input.dispatchEvent(enterKeyup);
        return {{ success: true, method: 'enter' }};
    }}

    sendBtn.focus();
    sendBtn.click();
    return {{ success: true, method: 'button' }};
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