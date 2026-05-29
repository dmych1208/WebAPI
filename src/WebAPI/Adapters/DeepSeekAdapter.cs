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
    console.log('[DeepSeekAdapter] 网络拦截脚本开始加载...');
    const originalFetch = window.fetch;

    function isChatResponse(url) {
        if (typeof url !== 'string') return false;
        if (!url.includes('/api/') && !url.includes('/chat/')) return false;
        if (url.includes('rephrase') || url.includes('rewrite') || url.includes('search_query') || url.includes('query_rewrite')) return false;
        if (url.includes('suggest') || url.includes('recommend') || url.includes('feedback') || url.includes('log')) return false;
        if (url.includes('config') || url.includes('setting') || url.includes('abtest') || url.includes('feature')) return false;
        return true;
    }

    function sendToBridge(type, url, data) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: type, url: url, data: data }));
        }
    }

    window.fetch = async function(...args) {
        const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
        console.log('[DeepSeekAdapter] fetch:', url);
        try {
            const response = await originalFetch.apply(this, args);

            if (isChatResponse(url) && response.body) {
                const clone = response.clone();
                const reader = clone.body.getReader();
                const decoder = new TextDecoder();
                (async () => {
                    try {
                        console.log('[DeepSeekAdapter] 开始读取响应流...');
                        while (true) {
                            var result = await reader.read();
                            if (result.done) {
                                console.log('[DeepSeekAdapter] 响应流读取完成');
                                sendToBridge('NETWORK_DONE', url, '');
                                break;
                            }
                            var chunk = decoder.decode(result.value, { stream: true });
                            sendToBridge('NETWORK_DATA', url, chunk);
                        }
                    } catch (e) {
                        console.error('[DeepSeekAdapter] 读取流异常:', e);
                    }
                })();
            }

            return response;
        } catch (err) {
            console.error('[DeepSeekAdapter] fetch异常:', err);
            return originalFetch.apply(this, args);
        }
    };

    console.log('[DeepSeekAdapter] fetch拦截已设置');

    const OriginalEventSource = window.EventSource;
    window.EventSource = function(url, config) {
        console.log('[DeepSeekAdapter] 创建EventSource:', url);
        const es = new OriginalEventSource(url, config);
        const origAddEventListener = es.addEventListener;
        es.addEventListener = function(type, listener, options) {
            const wrappedListener = function(event) {
                const msgData = 'event:' + type + '\ndata:' + (typeof event.data === 'string' ? event.data : JSON.stringify(event.data)) + '\n\n';
                sendToBridge('NETWORK_DATA', url, msgData);
                if (listener) listener.call(this, event);
            };
            return origAddEventListener.call(this, type, wrappedListener, options);
        };
        let _onmessage = null, _onopen = null, _onerror = null;
        Object.defineProperty(es, 'onmessage', {
            get: function() { return _onmessage; },
            set: function(fn) { _onmessage = fn; if (fn) es.addEventListener('message', fn); }
        });
        Object.defineProperty(es, 'onopen', {
            get: function() { return _onopen; },
            set: function(fn) { _onopen = fn; if (fn) es.addEventListener('open', fn); }
        });
        Object.defineProperty(es, 'onerror', {
            get: function() { return _onerror; },
            set: function(fn) { _onerror = fn; if (fn) es.addEventListener('error', fn); }
        });
        return es;
    };
    Object.defineProperty(window.EventSource, 'prototype', { value: OriginalEventSource.prototype });
    window.EventSource.CONNECTING = OriginalEventSource.CONNECTING;
    window.EventSource.OPEN = OriginalEventSource.OPEN;
    window.EventSource.CLOSED = OriginalEventSource.CLOSED;

    console.log('[DeepSeekAdapter] 网络拦截已完全启用');
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

    // 1. 查找输入框 - DeepSeek 使用 textarea#chat-input
    let input = document.getElementById('chat-input') || document.querySelector('textarea');
    
    if (!input) {{
        const allInputs = document.querySelectorAll('textarea');
        for (const el of allInputs) {{
            if (el.offsetParent !== null) {{
                input = el;
                break;
            }}
        }}
    }}

    if (!input) {{
        return {{ success: false, error: '找不到输入框' }};
    }}

    // 2. 绕过 React 受控组件 - 使用原生 setter
    const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
    nativeSetter.call(input, '{escapedPrompt}');
    input.dispatchEvent(new Event('input', {{ bubbles: true }}));
    input.focus();

    await sleep(300);

    // 3. 尝试 Enter 键发送
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
    input.dispatchEvent(enterKeyup);

    await sleep(300);

    // 4. 如果输入框还有内容（Enter 没成功），尝试点击发送按钮
    if (input.value && input.value.length > 0) {{
        // DeepSeek 的发送按钮是 div[role='button']
        let sendBtn = document.querySelector('div[role=""button""].ds-send-button') ||
                      document.querySelector('div[role=""button""][aria-disabled=""false""]');
        
        if (!sendBtn) {{
            const btns = document.querySelectorAll('div[role=""button""]');
            if (btns.length > 0) sendBtn = btns[btns.length - 1];
        }}

        if (sendBtn) {{
            sendBtn.dispatchEvent(new MouseEvent('mousedown', {{ view: window, bubbles: true, cancelable: true }}));
            sendBtn.dispatchEvent(new MouseEvent('click', {{ view: window, bubbles: true, cancelable: true }}));
            sendBtn.dispatchEvent(new MouseEvent('mouseup', {{ view: window, bubbles: true, cancelable: true }}));
            return {{ success: true, method: 'div-button-click' }};
        }}
        return {{ success: false, error: '输入框仍有内容但找不到发送按钮' }};
    }}

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

        public string GetModeSwitchScript(bool deepThink, bool search)
        {
            string dtFlag = deepThink ? "true" : "false";
            string srFlag = search ? "true" : "false";
            return $@"
(async () => {{
    function sleep(ms) {{ return new Promise(r => setTimeout(r, ms)); }}

    var modelBtns = document.querySelectorAll('button, [role=""tab""], div[class*=""model""], div[class*=""tab""], [data-testid]');
    var deepThinkBtn = null;
    var chatBtn = null;
    for (var i = 0; i < modelBtns.length; i++) {{
        var btn = modelBtns[i];
        var text = (btn.textContent || '').toLowerCase();
        var ariaLabel = (btn.getAttribute('aria-label') || '').toLowerCase();
        if (text.includes('deepthink') || text.includes('r1') || text.includes('深度思考') ||
            ariaLabel.includes('deepthink') || ariaLabel.includes('r1')) {{
            deepThinkBtn = btn;
        }}
        if ((text.includes('chat') && !text.includes('search')) || 
            (text === '对话') || (text.includes('标准') && !text.includes('深度'))) {{
            chatBtn = btn;
        }}
    }}

    if ({dtFlag} && deepThinkBtn) {{
        var dtActive = deepThinkBtn.classList.contains('active') || 
                      deepThinkBtn.classList.contains('selected') ||
                      deepThinkBtn.getAttribute('aria-checked') === 'true' ||
                      deepThinkBtn.getAttribute('aria-selected') === 'true';
        if (!dtActive) {{
            deepThinkBtn.click();
            await sleep(500);
        }}
    }} else if (!{dtFlag} && chatBtn) {{
        var chatActive = chatBtn.classList.contains('active') || 
                        chatBtn.classList.contains('selected') ||
                        chatBtn.getAttribute('aria-checked') === 'true' ||
                        chatBtn.getAttribute('aria-selected') === 'true';
        if (!chatActive) {{
            chatBtn.click();
            await sleep(500);
        }}
    }} else if (!{dtFlag} && deepThinkBtn) {{
        var dtActive2 = deepThinkBtn.classList.contains('active') || 
                       deepThinkBtn.classList.contains('selected') ||
                       deepThinkBtn.getAttribute('aria-checked') === 'true' ||
                       deepThinkBtn.getAttribute('aria-selected') === 'true';
        if (dtActive2) {{
            deepThinkBtn.click();
            await sleep(500);
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
            if (dataJson == "FINISHED") return null;

            try
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataJson);

                // 检查是否是结束信号
                if (data.TryGetProperty("finish_reason", out var frProp))
                {
                    string? fr = frProp.ValueKind == System.Text.Json.JsonValueKind.String ? frProp.GetString() : null;
                    if (fr == "stop" || fr == "length" || fr == "content_filter")
                        return null; // 返回 null 表示这是结束信号，由调用方处理
                }

                if (data.TryGetProperty("p", out var pProp) && data.TryGetProperty("v", out var vProp))
                {
                    string p = pProp.GetString() ?? "";
                    string? v = vProp.ValueKind == System.Text.Json.JsonValueKind.String
                        ? vProp.GetString() : null;

                    if (p.Contains("reasoning") || p.Contains("thinking"))
                        return v;

                    if (p.Contains("status") || p.Contains("accumulated_token_usage"))
                        return null;

                    if (p.Contains("content") || p.Contains("choices") || string.IsNullOrEmpty(p))
                        return v;
                }

                if (data.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "thinking")
                    {
                        if (data.TryGetProperty("content", out var cProp))
                            return cProp.GetString();
                        if (data.TryGetProperty("v", out var vProp2) && vProp2.ValueKind == System.Text.Json.JsonValueKind.String)
                            return vProp2.GetString();
                        return null;
                    }
                    if (type == "text")
                    {
                        if (data.TryGetProperty("content", out var cProp))
                            return cProp.GetString();
                        if (data.TryGetProperty("v", out var vProp2) && vProp2.ValueKind == System.Text.Json.JsonValueKind.String)
                            return vProp2.GetString();
                        return null;
                    }
                    if (type == "search_result" || type == "search")
                        return null;
                }

                // 标准 OpenAI 格式: choices[0].delta.content
                if (data.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var firstChoice = choicesProp.EnumerateArray().FirstOrDefault();
                    if (firstChoice.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    {
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
                }

                // 直接 content 字段
                if (data.TryGetProperty("content", out var directContent))
                {
                    return directContent.GetString();
                }

                // 直接 text 字段
                if (data.TryGetProperty("text", out var directText))
                {
                    return directText.GetString();
                }

                var fragments = data.TryGetProperty("v", out var vFrag) && vFrag.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? vFrag.TryGetProperty("response", out var respProp) && respProp.ValueKind == System.Text.Json.JsonValueKind.Object
                        ? respProp.TryGetProperty("fragments", out var fragProp) ? fragProp : default
                        : default
                    : default;

                if (fragments.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var frag in fragments.EnumerateArray())
                    {
                        string? fragType = frag.TryGetProperty("type", out var ftProp) ? ftProp.GetString() : null;
                        if (fragType == "THINKING" || fragType == "reasoning") continue;
                        if (frag.TryGetProperty("content", out var fcProp))
                            sb.Append(fcProp.GetString() ?? "");
                    }
                    return sb.Length > 0 ? sb.ToString() : null;
                }
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
