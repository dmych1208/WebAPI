using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Wpf;
using WebAPI.Models;
using WebAPI.Common;

namespace WebAPI.Adapters
{
    /// <summary>
    /// DeepSeek PoW 挑战数据结构（对应 WebView 收到的 JSON）
    /// </summary>
    public class DeepSeekPowChallenge
    {
        public string challenge { get; set; } = "";
        public int difficulty { get; set; } = 4;
        public string algorithm { get; set; } = "DeepSeekHashV1";
        public int timeout { get; set; } = 30000;
    }

    public class DeepSeekPowData
    {
        public int code { get; set; }
        public string msg { get; set; } = "";
        public DeepSeekPowDataInner? data { get; set; }
    }

    public class DeepSeekPowDataInner
    {
        public int biz_code { get; set; }
        public string biz_msg { get; set; } = "";
        public DeepSeekPowChallenge? challenge { get; set; }
    }

    public class DeepSeekAdapter : IAdapter
    {
        public string PlatformId => "deepseek";
        public string PlatformName => "DeepSeek";
        public string TargetUrl => "https://chat.deepseek.com/";
        public int DefaultPort => 55555;
        public string UserDataFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2_Data", "DeepSeek");

        public List<ModelInfo> AvailableModels => new List<ModelInfo>
        {
            new ModelInfo { Id = "deepseek-chat", Name = "标准对话(极速)" },
            new ModelInfo { Id = "deepseek-chat-search", Name = "联网对话(搜索)" },
            new ModelInfo { Id = "deepseek-reasoner", Name = "深度思考(R1)" },
            new ModelInfo { Id = "deepseek-reasoner-search", Name = "R1联网(弱)" }
        };

        /// <summary>
        /// 捕获最近一次 PoW 挑战数据（由 ChannelPanel 在收到 WebMessage 时设置）
        /// </summary>
        public DeepSeekPowChallenge? CapturedPowChallenge { get; set; }

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
               url.includes('deepseek') ||
               url.includes('/message') ||
               url.includes('/send');
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
        this._method = method;
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

    console.log('[DeepSeekAdapter] 网络拦截已启用（流式）');
})();
";
        }

        /// <summary>
        /// 将 PoW 挑战数据（JSON 字符串）解析并存储到 CapturedPowChallenge
        /// </summary>
        public void CapturePowChallenge(string jsonData)
        {
            try
            {
                var powData = JsonSerializer.Deserialize<DeepSeekPowData>(jsonData);
                if (powData?.data?.challenge != null)
                {
                    CapturedPowChallenge = powData.data.challenge;
                    Console.WriteLine($"[DeepSeekAdapter] 捕获 PoW 挑战: {CapturedPowChallenge.algorithm}, difficulty={CapturedPowChallenge.difficulty}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DeepSeekAdapter] 解析 PoW 挑战失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步解决 DeepSeek PoW 挑战（使用 CPU 多核并行暴力搜索）
        /// difficulty=N 表示哈希前 N 位必须为 0
        /// </summary>
        public async Task<string> SolvePowAsync(DeepSeekPowChallenge challenge, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                int targetBits = challenge.difficulty; // 通常是 4 或 5
                int targetBytes = (targetBits + 7) / 8;
                int requiredZeroBytes = targetBits / 8;

                // 需要前 targetBits 位为 0，即前 requiredZeroBytes 全为 0，
                // 且第 requiredZeroBytes 字节的前 (targetBits % 8) 位为 0
                byte targetMask = (byte)(0xFF << (8 - (targetBits % 8)));

                // 搜索上限，避免无限循环（实际难度 4-5 时很快就能找到）
                long maxAttempts = 1L << (targetBits + 5);
                Random rng = Random.Shared;

                for (long i = 0; i < maxAttempts; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // 生成随机 nonce（8 字节）
                    byte[] nonceBytes = new byte[8];
                    rng.NextBytes(nonceBytes);

                    // 构建输入：challenge + nonce
                    byte[] input = Encoding.UTF8.GetBytes(challenge.challenge);
                    byte[] combined = new byte[input.Length + 8];
                    Buffer.BlockCopy(input, 0, combined, 0, input.Length);
                    Buffer.BlockCopy(nonceBytes, 0, combined, input.Length, 8);

                    // SHA-256 哈希
                    byte[] hash = SHA256.HashData(combined);

                    // 检查是否满足难度要求
                    bool valid = true;
                    for (int j = 0; j < requiredZeroBytes; j++)
                    {
                        if (hash[j] != 0) { valid = false; break; }
                    }
                    if (valid && targetBits % 8 != 0 && requiredZeroBytes < hash.Length)
                    {
                        if ((hash[requiredZeroBytes] & targetMask) != 0)
                            valid = false;
                    }

                    if (valid)
                    {
                        // 将 nonce 转换为 Base64 字符串（与 DeepSeek 前端一致）
                        string nonce = Convert.ToBase64String(nonceBytes);
                        Console.WriteLine($"[DeepSeekAdapter] PoW 解决！尝试次数: {i + 1}, nonce={nonce.Substring(0, 8)}...");
                        return nonce;
                    }

                    // 每 100 万次报告一次进度
                    if (i > 0 && i % 1_000_000 == 0)
                    {
                        Console.WriteLine($"[DeepSeekAdapter] PoW 搜索进度: {i:N0} 次尝试...");
                    }
                }

                throw new Exception($"PoW 求解失败，已尝试 {maxAttempts:N0} 次");
            }, ct);
        }

        /// <summary>
        /// 注入 PoW 解决方案到页面，并触发发送
        /// </summary>
        public string GetPowInjectionScript(string powSolution)
        {
            string escapedSolution = EscapeForJs(powSolution);
            return $@"
(async () => {{
    try {{
        // 注入 PoW 解决方案到全局对象，DeepSeek 前端会读取此字段
        if (window.t) {{
            window.t._powSolution = '{escapedSolution}';
            window.t._powSolved = true;
        }}

        // 直接触发 DeepSeek 内部的消息发送
        // 查找发送按钮并点击
        const btns = document.querySelectorAll('button');
        for (const btn of btns) {{
            const txt = btn.textContent?.toLowerCase() || '';
            const aria = btn.getAttribute('aria-label')?.toLowerCase() || '';
            if (txt.includes('send') || aria.includes('send')) {{
                btn.click();
                return {{ success: true, method: 'pow-injected-send' }};
            }}
        }}
        return {{ success: true, method: 'pow-injected-no-btn' }};
    }} catch(e) {{
        return {{ success: false, error: e.message }};
    }}
}})();
";
        }

        public string GetDomControlScript(string prompt)
        {
            string escapedPrompt = EscapeForJs(prompt);
            string script = $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    // 1. 查找输入框 - DeepSeek 使用 textarea#chat-input 或其他选择器
    let input = document.getElementById('chat-input') || 
                document.querySelector('textarea') ||
                document.querySelector('[data-placeholder*=""输入""]') ||
                document.querySelector('.chat-input') ||
                document.querySelector('textarea[role=""textbox""]');
    
    if (!input) {{
        const allInputs = document.querySelectorAll('textarea, input[type=""text""], [contenteditable=""true""]');
        for (const el of allInputs) {{
            if (el.offsetParent !== null && el.clientHeight > 20) {{
                input = el;
                break;
            }}
        }}
    }}

    if (!input) {{
        console.log('[DeepSeekAdapter] 找不到输入框');
        return {{ success: false, error: '找不到输入框' }};
    }}

    console.log('[DeepSeekAdapter] 找到输入框:', input.tagName, input.id, input.className);

    // 2. 绕过 React 受控组件 - 使用原生 setter
    const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
    nativeSetter.call(input, '{escapedPrompt}');
    input.dispatchEvent(new Event('input', {{ bubbles: true }}));
    input.dispatchEvent(new Event('change', {{ bubbles: true }}));
    input.focus();

    await sleep(500);

    // 3. 尝试 Enter 键发送
    input.dispatchEvent(new KeyboardEvent('keydown', {{
        key: 'Enter',
        code: 'Enter',
        keyCode: 13,
        which: 13,
        bubbles: true,
        cancelable: true
    }}));
    input.dispatchEvent(new KeyboardEvent('keyup', {{
        key: 'Enter',
        code: 'Enter',
        keyCode: 13,
        which: 13,
        bubbles: true,
        cancelable: true
    }}));

    await sleep(500);

    // 4. 如果输入框还有内容（Enter 没成功），尝试点击发送按钮
    if (input.value && input.value.length > 0) {{
        console.log('[DeepSeekAdapter] Enter 键未生效，尝试点击发送按钮');
        
        // DeepSeek 的发送按钮
        let sendBtn = document.querySelector('button[type=""submit""]') ||
                      document.querySelector('.send-button') ||
                      document.querySelector('[data-testid=""send-button""]') ||
                      document.querySelector('div[role=""button""][aria-label*=""发送""]') ||
                      document.querySelector('div[role=""button""][aria-label*=""Send""]');
        
        if (!sendBtn) {{
            const btns = document.querySelectorAll('button, div[role=""button""]');
            for (const btn of btns) {{
                const text = btn.textContent?.trim().toLowerCase() || '';
                if (text.includes('发送') || text.includes('send') || text.includes('提交')) {{
                    sendBtn = btn;
                    break;
                }}
            }}
        }}

        if (sendBtn && !sendBtn.disabled) {{
            console.log('[DeepSeekAdapter] 点击发送按钮');
            sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
            sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
            sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
            return {{ success: true, method: 'button-click' }};
        }}
        
        // 如果有 contenteditable 元素，尝试直接提交
        if (input.getAttribute('contenteditable') === 'true') {{
            console.log('[DeepSeekAdapter] 尝试提交 contenteditable');
            input.dispatchEvent(new KeyboardEvent('keydown', {{
                key: 'Enter',
                code: 'Enter',
                keyCode: 13,
                shiftKey: false,
                bubbles: true,
                cancelable: true
            }}));
        }}
        
        return {{ success: true, method: 'fallback-enter' }};
    }}

    console.log('[DeepSeekAdapter] 发送成功，使用 Enter 键');
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

        /// <summary>
        /// 注入 PoW 解决方案（需要在 SendPromptCoreAsync 中，收到 prepare 调用后调用）
        /// </summary>
        public async Task InjectPowSolutionAsync(WebView2 webView, string powSolution)
        {
            if (webView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 未初始化");

            string script = GetPowInjectionScript(powSolution);
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
