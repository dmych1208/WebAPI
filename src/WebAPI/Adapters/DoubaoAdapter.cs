using System.IO;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    public class DoubaoAdapter : IAdapter
    {
        public string PlatformId => "doubao";
        public string PlatformName => "豆包";
        public string TargetUrl => "https://www.doubao.com/chat/";
        public int DefaultPort => 55556;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "Doubao");

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "doubao-pro", Name = "豆包Pro" },
            new ModelInfo { Id = "doubao-lite", Name = "豆包Lite" },
            new ModelInfo { Id = "doubao-role", Name = "角色扮演" }
        };

        public string GetNetworkInterceptorScript()
        {
            return @"
(() => {
    const originalFetch = window.fetch;

    function isChatResponse(url) {
        if (typeof url !== 'string') return false;
        if (url.includes('volcengine') || url.includes('ark') || url.includes('doubao')) return true;
        if (!url.includes('/api/') && !url.includes('/chat/') && !url.includes('completion') && !url.includes('stream')) return false;
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
                if (response.body) {
                    const clone = response.clone();
                    (async () => {
                        try {
                            const reader = clone.body.getReader();
                            const decoder = new TextDecoder();
                            while (true) {
                                const result = await reader.read();
                                if (result.done) {
                                    if (window.chrome && window.chrome.webview) {
                                        window.chrome.webview.postMessage(JSON.stringify({
                                            type: 'NETWORK_DONE',
                                            url: url
                                        }));
                                    }
                                    break;
                                }
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
                        }
                    })();
                }
            }

            return response;
        } catch (err) {
            return originalFetch.apply(this, args);
        }
    };

    console.log('[DoubaoAdapter] 网络拦截已启用(流式)');
})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    // 1. 查找输入框 - 豆包可能使用 textarea 或 contenteditable
    let input = null;
    const selectors = [
        'textarea#chat-input',
        'textarea[placeholder*=""输入""]',
        'textarea[placeholder*=""说点""]',
        'textarea[placeholder*=""请问""]',
        'textarea[class*=""chat""]',
        'textarea',
        'div[contenteditable=""true""]',
        'div[contenteditable]',
        '[role=""textbox""]',
        'div[class*=""input""]',
        'div[class*=""chat-input""]'
    ];

    for (const sel of selectors) {{
        const el = document.querySelector(sel);
        if (el && el.offsetParent !== null && el.getBoundingClientRect().width > 50) {{
            input = el;
            break;
        }}
    }}

    if (!input) {{
        const allInputs = document.querySelectorAll('textarea, div[contenteditable]');
        for (const el of allInputs) {{
            if (el.offsetParent !== null && el.getBoundingClientRect().width > 50) {{
                input = el;
                break;
            }}
        }}
    }}

    if (!input) {{
        return {{ success: false, error: '找不到输入框' }};
    }}

    // 2. 设置输入值 - 兼容不同输入类型
    input.focus();
    await sleep(100);

    if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {{
        // 使用原生 setter 绕过 React 受控组件
        const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
        if (nativeSetter) {{
            nativeSetter.call(input, '{escapedPrompt}');
        }} else {{
            input.value = '{escapedPrompt}';
        }}
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: '{escapedPrompt}' }}));
        input.dispatchEvent(new Event('change', {{ bubbles: true }}));
    }} else {{
        // contenteditable div
        input.innerHTML = '<span>{escapedPrompt}</span>';
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: '{escapedPrompt}' }}));
        
        // 移动光标到末尾
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(input);
        range.collapse(false);
        selection?.removeAllRanges();
        selection?.addRange(range);
    }}

    await sleep(300);

    // 3. 查找发送按钮 - 豆包可能使用 button 或 div[role=""button""]
    let sendBtn = null;
    const btnSelectors = [
        'button[type=""submit""]',
        'button[aria-label*=""send"" i]',
        'button[aria-label*=""发送"" i]',
        'button[class*=""send"" i]',
        'button[class*=""submit"" i]',
        'div[role=""button""][class*=""send"" i]',
        'div[role=""button""][class*=""submit"" i]',
        'button:has(svg)',
        'button',
        '[role=""button""]'
    ];

    for (const sel of btnSelectors) {{
        const btns = document.querySelectorAll(sel);
        for (const btn of btns) {{
            if (btn.offsetParent !== null && btn.getBoundingClientRect().width > 20) {{
                let targetBtn = btn;
                if (btn.tagName === 'svg') {{
                    targetBtn = btn.closest('button') || btn.closest('[role=""button""]') || btn.parentElement;
                }}
                
                if (targetBtn && targetBtn.offsetParent !== null) {{
                    const txt = (targetBtn.textContent || '').toLowerCase().trim();
                    const ariaLabel = (targetBtn.getAttribute('aria-label') || '').toLowerCase() || '';
                    const isDisabled = targetBtn.disabled || 
                                      targetBtn.getAttribute('aria-disabled') === 'true' ||
                                      targetBtn.getAttribute('disabled') !== null;

                    if (!isDisabled && (
                        txt.includes('send') || 
                        txt.includes('发送') || 
                        ariaLabel.includes('send') || 
                        ariaLabel.includes('发送') ||
                        (targetBtn.querySelector('svg') && txt.length < 10)
                    )) {{
                        sendBtn = targetBtn;
                        break;
                    }}
                }}
            }}
        }}
        if (sendBtn) break;
    }}

    // 4. 发送策略：优先尝试 Enter 键，失败则点击按钮
    if (!sendBtn) {{
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
        return {{ success: true, method: 'enter' }};
    }}

    // 使用 MouseEvent 三件套确保触发点击
    sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
    sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
    sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
    return {{ success: true, method: 'button-click' }};
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

        public string? ExtractContentFromSseData(string dataJson, string eventType)
        {
            if (string.IsNullOrEmpty(dataJson)) return null;

            try
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataJson);

                if (data.TryGetProperty("event_type", out var etProp))
                {
                    int eventTypeCode = etProp.GetInt32();

                    if (eventTypeCode == 2003 && data.TryGetProperty("event_data", out var edProp))
                    {
                        string? eventDataStr = edProp.ValueKind == System.Text.Json.JsonValueKind.String
                            ? edProp.GetString() : null;
                        if (eventDataStr != null)
                        {
                            try
                            {
                                var eventData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(eventDataStr);
                                if (eventData.TryGetProperty("text", out var tProp)) return tProp.GetString();
                                if (eventData.TryGetProperty("content", out var cProp)) return cProp.GetString();
                                if (eventData.TryGetProperty("delta", out var dProp)) return dProp.GetString();
                            }
                            catch { return eventDataStr; }
                        }
                        else if (edProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (edProp.TryGetProperty("text", out var tProp)) return tProp.GetString();
                            if (edProp.TryGetProperty("content", out var cProp)) return cProp.GetString();
                            if (edProp.TryGetProperty("delta", out var dProp)) return dProp.GetString();
                        }
                    }

                    if (eventTypeCode == 2002) return null;

                    if (data.TryGetProperty("event_data", out var edProp2))
                    {
                        string? edStr = edProp2.ValueKind == System.Text.Json.JsonValueKind.String
                            ? edProp2.GetString() : null;
                        if (edStr != null)
                        {
                            try
                            {
                                var eventData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(edStr);
                                if (eventData.TryGetProperty("text", out var tProp)) return tProp.GetString();
                                if (eventData.TryGetProperty("content", out var cProp)) return cProp.GetString();
                                if (eventData.TryGetProperty("message", out var mProp) && mProp.TryGetProperty("content", out var mcProp))
                                    return mcProp.GetString();
                            }
                            catch { }
                        }
                        else if (edProp2.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (edProp2.TryGetProperty("text", out var tProp)) return tProp.GetString();
                            if (edProp2.TryGetProperty("content", out var cProp)) return cProp.GetString();
                        }
                    }

                    return null;
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
                if (data.TryGetProperty("delta", out var deltaProp2)) return deltaProp2.GetString();
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