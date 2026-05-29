using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using WebAPI.Common;
using WebAPI.Models;
using WebAPI.Adapters;

namespace WebAPI.Controls
{
    public partial class ChannelPanel : System.Windows.Controls.UserControl
    {
        private readonly ChannelConfig _config;
        private ChannelHttpServer? _httpServer;
        private IAdapter? _adapter;
        private bool _isRunning = true;
        private bool _isInitialized = false;
        public new bool IsInitialized => _isInitialized;

        public void MarkInitialized() => _isInitialized = true;

        // 响应收集（非流式 fallback 用）
        private TaskCompletionSource<string>? _responseTcs;
        private StringBuilder _responseBuffer = new();

        // 流式响应：chunk 队列 + 信号量
        private ConcurrentQueue<(string text, bool isDone)> _streamChunks = new();
        private SemaphoreSlim _streamSignal = new(0);
        private bool _isStreaming = false;

        private bool _deepThinkEnabled = false;
        private bool _searchEnabled = false;

        private bool _controllerAvailable = false;
        private readonly List<string> _citations = new();
        private TaskCompletionSource<bool>? _pageLoadTcs;
        private bool _pageUsable = false;
        public bool PageUsable => _pageUsable;
        private static readonly string AntiDetectionScript = @"
Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
delete navigator.__proto__.webdriver;
Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
Object.defineProperty(navigator, 'languages', { get: () => ['zh-CN', 'zh', 'en'] });
window.dispatchEvent(new Event('load'));
window.dispatchEvent(new Event('DOMContentLoaded'));
";

        // 请求处理锁
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private CancellationTokenSource? _requestCts;

        // DeepSeek PoW
        private TaskCompletionSource<DeepSeekPowChallenge>? _powChallengeTcs;

        public Action<string, LogLevel>? OnLogMessage;
        public Action? OnProxySettingsChanged;
        public Action? OnRequestReceived;

        public string StatusText => _isRunning ? $"{_config.Name} 服务就绪" : $"{_config.Name} 已停止";
        public int Port => _config.Port;
        public bool IsRunning => _isRunning;
        public string ChannelName => _config.Name;

        public ChannelPanel(ChannelConfig config)
        {
            InitializeComponent();
            _config = config;
            InitUI();
        }

        private void InitUI()
        {
            PanelTitle.Text = $"{_config.Name} Relay";
            PanelStatus.Text = "⏳ 正在初始化...";

            TxtBaseUrl.Text = $"http://127.0.0.1:{_config.Port}";
            TxtApiKey.Text = "sk-any";

            ModelList.ItemsSource = _config.Models;

            if (_config.Id is "gemini" or "grok")
                ProxyPanel.Visibility = Visibility.Visible;
        }

        private void ChannelPanel_Loaded(object sender, RoutedEventArgs e)
        {
            // Loaded 事件不再自动初始化，由 MainWindow 串行初始化控制
        }

        public async Task InitializeAsync()
        {
            try
            {
                _streamChunks = new ConcurrentQueue<(string text, bool isDone)>();
                _streamSignal = new SemaphoreSlim(0);
                _isStreaming = false;
                _responseBuffer.Clear();

                Log("正在初始化 HTTP 服务...", LogLevel.Info);

                _httpServer = new ChannelHttpServer(_config);
                _httpServer.OnLog += (msg) => Log(msg, LogLevel.Info);
                _httpServer.OnRequest += () => OnRequestReceived?.Invoke();

                _httpServer.ProcessPromptFunc = SendPromptAsync;
                _httpServer.ProcessPromptStreamFunc = SendPromptStreamAsync;

                await _httpServer.StartAsync();

                Log("正在初始化 WebView2...", LogLevel.Info);

                _adapter = CreateAdapter();

                _pageLoadTcs = new TaskCompletionSource<bool>();

                await InitializeWebView2Async();

                bool pageOk = false;
                try
                {
                    pageOk = await _pageLoadTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (TimeoutException)
                {
                    Log("页面加载超时（15秒）", LogLevel.Warn);
                }

                if (pageOk)
                {
                    PanelStatus.Text = "✅ 正在运行 (V2)";
                    PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
                    Log("初始化完成（流式响应已启用）", LogLevel.Info);
                }
                else
                {
                    PanelStatus.Text = "⚠️ 页面加载失败";
                    PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentRed");
                    Log("页面加载失败，渠道已启动但网页不可用", LogLevel.Warn);
                }
                _isRunning = true;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (WebView.CoreWebView2 != null)
                        {
                            Log("自动刷新页面...", LogLevel.Info);
                            WebView.CoreWebView2.Reload();
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                Log($"初始化失败: {ex.Message}", LogLevel.Error);
                _isRunning = false;
                PanelStatus.Text = "❌ 初始化失败";
                PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentRed");
            }
        }

        private IAdapter CreateAdapter()
        {
            return _config.Id switch
            {
                "deepseek" => new DeepSeekAdapter(),
                "qwen" => new QwenAdapter(),
                "doubao" => new DoubaoAdapter(),
                "gemini" => new GeminiAdapter(),
                "grok" => new GrokAdapter(),
                "yuanbao" => new YuanbaoAdapter(),
                "kimi" => new KimiAdapter(),
                _ => new DeepSeekAdapter()
            };
        }

        private async Task InitializeWebView2Async()
        {
            if (_adapter == null) return;

            ShowLoadingSpinner();

            Directory.CreateDirectory(_adapter.UserDataFolder);

            try
            {
                if (WebView.CoreWebView2 != null)
                {
                    _controllerAvailable = true;
                    WebView.CoreWebView2.Settings.UserAgent =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.78 Safari/537.36";
                    await WebView.CoreWebView2.ExecuteScriptAsync(AntiDetectionScript);
                    WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                    WebView.NavigationCompleted += WebView_NavigationCompleted;
                    try
                    {
                        await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_adapter.GetNetworkInterceptorScript());
                    }
                    catch { }
                    Log($"正在导航到 {_adapter.TargetUrl}...", LogLevel.Info);
                    WebView.CoreWebView2.Navigate(_adapter.TargetUrl);
                    WebView.Visibility = Visibility.Visible;
                    HideLoadingSpinner();
                    return;
                }

                var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                var opts = new CoreWebView2EnvironmentOptions();
                string? proxyArgs = GetProxyArgs();
                if (proxyArgs != null)
                {
                    opts.AdditionalBrowserArguments = proxyArgs;
                    Log($"代理已启用: {proxyArgs}", LogLevel.Info);
                }
                var envTask = CoreWebView2Environment.CreateAsync(userDataFolder: _adapter.UserDataFolder, options: opts);
                var env = await envTask.WaitAsync(initCts.Token);
                
                var ensureTask = WebView.EnsureCoreWebView2Async(env);
                await ensureTask.WaitAsync(initCts.Token);

                _controllerAvailable = true;
                WebView.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.78 Safari/537.36";
                await WebView.CoreWebView2.ExecuteScriptAsync(AntiDetectionScript);

                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_adapter.GetNetworkInterceptorScript());

                WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                WebView.NavigationCompleted += WebView_NavigationCompleted;

                Log($"正在导航到 {_adapter.TargetUrl}...", LogLevel.Info);
                WebView.Source = new Uri(_adapter.TargetUrl);
                WebView.Visibility = Visibility.Visible;
                HideLoadingSpinner();
            }
            catch (TimeoutException)
            {
                HideLoadingSpinner();
                Log("WebView2 初始化超时（30秒）", LogLevel.Error);
                PlaceholderText.Text = "❌ WebView2 初始化超时\n\n请检查系统资源或重启应用";
            }
            catch (OperationCanceledException)
            {
                HideLoadingSpinner();
                Log("WebView2 初始化超时（30秒）", LogLevel.Error);
                PlaceholderText.Text = "❌ WebView2 初始化超时\n\n请检查系统资源或重启应用";
            }
            catch (WebView2RuntimeNotFoundException)
            {
                HideLoadingSpinner();
                Log("WebView2 Runtime 未安装", LogLevel.Error);
                PlaceholderText.Text = "❌ WebView2 Runtime 未安装\n\n请访问以下链接下载安装：\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/";
                throw;
            }
            catch (Exception ex)
            {
                HideLoadingSpinner();
                Log($"WebView2 初始化失败: {ex.Message}", LogLevel.Error);
                PlaceholderText.Text = $"❌ 初始化失败: {ex.Message}";
            }
        }

        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            HideLoadingSpinner();

            bool pageUsable = e.IsSuccess;

            if (e.IsSuccess && WebView.CoreWebView2 != null)
            {
                try
                {
                    string checkResult = await WebView.CoreWebView2.ExecuteScriptAsync(
                        "(function(){var t=document.title||'';" +
                        "if(t.includes('ERR_')||t.includes('无法访问')||t.includes('connect')||" +
                        "t.includes('Problem loading')||t.includes('proxy')||t.includes('timeout')||" +
                        "t.includes('No internet')||t.includes('This site can'))return'error';return'ok';})()");
                    checkResult = checkResult.Trim('"');

                    if (checkResult == "error")
                    {
                        pageUsable = false;
                        Log($"页面错误: title='{WebView.CoreWebView2.DocumentTitle}'", LogLevel.Warn);
                    }
                }
                catch { }

                if (pageUsable)
                {
                    Log("页面加载完成", LogLevel.Info);
                    try { await WebView.CoreWebView2.ExecuteScriptAsync(AntiDetectionScript); } catch { }
                    try { await WebView.CoreWebView2.ExecuteScriptAsync(_adapter!.GetDomControlScript("")); } catch { }
                }
            }
            else
            {
                Log($"页面加载失败: {e.WebErrorStatus}", LogLevel.Error);
            }

            if (_pageLoadTcs != null && !_pageLoadTcs.Task.IsCompleted)
            {
                _pageLoadTcs.TrySetResult(pageUsable);
            }

            _pageUsable = pageUsable;
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();

                if (message.Length > 300)
                    Log($"收到 WebMessage: {message.Substring(0, 300)}...", LogLevel.Debug);
                else
                    Log($"收到 WebMessage: {message}", LogLevel.Debug);

                // === 网络拦截数据（流式和非流式都处理）===
                // 优先处理 NETWORK_DATA，因为 PoW 也可能在其中
                if (message.Contains("NETWORK_DATA"))
                {
                    ProcessNetworkData(message);
                    return;
                }

                // === NETWORK_DONE：非流式时结束响应 ===
                if (message.Contains("NETWORK_DONE"))
                {
                    if (!_isStreaming && _responseTcs != null && _responseBuffer.Length > 0)
                    {
                        _responseTcs.TrySetResult(_responseBuffer.ToString());
                    }
                    return;
                }

                // === 非流式 fallback：从 WebMessage 提取内容 ===
                if (!_isStreaming && _responseTcs != null)
                {
                    var content = ExtractContentFromMessage(message);
                    if (!string.IsNullOrEmpty(content))
                    {
                        _responseBuffer.Append(content);
                        Log($"累计收到内容: {_responseBuffer.Length} 字符", LogLevel.Debug);
                    }

                    if (message.Contains("\"stop\"") || message.Contains("\"done\"") ||
                        message.Contains("NETWORK_DONE"))
                    {
                        _responseTcs.TrySetResult(_responseBuffer.ToString());
                    }
                    else if ((DateTime.Now - _streamStartTime).TotalSeconds > 5 && _responseBuffer.Length > 0)
                    {
                        // 超时但有内容，返回已有内容
                        _responseTcs.TrySetResult(_responseBuffer.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"处理 WebMessage 出错: {ex.Message}", LogLevel.Error);
            }
        }

        private DateTime _streamStartTime;

        /// <summary>
        /// 处理网络拦截数据，提取有效内容并推入流式队列
        /// </summary>
        private void ProcessNetworkData(string message)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                if (!json.TryGetProperty("data", out var dataProp)) return;

                string dataStr = dataProp.GetString() ?? "";
                if (string.IsNullOrEmpty(dataStr)) return;

                // === DeepSeek PoW 挑战捕获 ===
                if (_powChallengeTcs != null && !_powChallengeTcs.Task.IsCompleted)
                {
                    if (dataStr.Contains("create_pow_challenge") || dataStr.Contains("pow") || dataStr.Contains("challenge"))
                    {
                        Log($"[PoW检测] 疑似PoW数据: {dataStr.Substring(0, Math.Min(300, dataStr.Length))}", LogLevel.Info);
                        
                        var adapter = _adapter as DeepSeekAdapter;
                        if (adapter != null)
                        {
                            try
                            {
                                adapter.CapturePowChallenge(dataStr);

                                if (adapter.CapturedPowChallenge != null)
                                {
                                    Log($"PoW 挑战已捕获: difficulty={adapter.CapturedPowChallenge.difficulty}", LogLevel.Info);
                                    _powChallengeTcs.TrySetResult(adapter.CapturedPowChallenge);
                                }
                                else
                                {
                                    Log($"PoW 解析失败，CapturedPowChallenge 为 null", LogLevel.Warn);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"解析 PoW 挑战失败: {ex.Message}", LogLevel.Error);
                            }
                        }
                    }
                }

                if (dataStr.Contains("AI question rephraser") ||
                    dataStr.Contains("rephrase the follow-up") ||
                    dataStr.Contains("query_rewrite") ||
                    dataStr.Contains("search_query"))
                {
                    Log("过滤非聊天响应(搜索重写器)", LogLevel.Debug);
                    return;
                }

                CaptureCitations(dataStr);

                if (dataStr.Contains("data:") || dataStr.Contains("event:"))
                {
                    ProcessSseData(dataStr);
                }
                else
                {
                    string? content = null;

                    if (_adapter != null)
                    {
                        content = _adapter.ExtractContentFromSseData(dataStr, "");
                    }

                    if (content == null)
                        content = SseParser.ExtractContent(dataStr);

                    if (!string.IsNullOrEmpty(content))
                    {
                        if (content.Contains("AI question rephraser") ||
                            content.Contains("rephrase the follow-up"))
                        {
                            Log("过滤非聊天内容(搜索重写器)", LogLevel.Debug);
                            return;
                        }

                        if (_isStreaming)
                            EnqueueChunk(content, false);
                        else
                        {
                            _responseBuffer.Append(content);
                            Log($"累计收到内容: {_responseBuffer.Length} 字符", LogLevel.Debug);
                        }
                    }
                    else
                    {
                        // 记录提取失败，帮助调试
                        if (_isStreaming && dataStr.Length > 10)
                        {
                            Log($"内容提取为空: {dataStr.Substring(0, Math.Min(100, dataStr.Length))}", LogLevel.Debug);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"解析网络数据出错: {ex.Message}", LogLevel.Error);
            }
        }

        private void CaptureCitations(string dataStr)
        {
            try
            {
                if (!dataStr.Contains("\"url\"") && !dataStr.Contains("\"title\"")) return;

                var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(dataStr);

                void ExtractFromElement(System.Text.Json.JsonElement el, string prefix)
                {
                    if (el.TryGetProperty("search_results", out var sr) && sr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in sr.EnumerateArray())
                        {
                            string? title = null;
                            string? url = null;
                            if (item.TryGetProperty("title", out var tp)) title = tp.GetString();
                            if (item.TryGetProperty("url", out var up)) url = up.GetString();
                            if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(url))
                            {
                                var entry = $"[{title ?? url}] {(url ?? "")}";
                                if (!_citations.Contains(entry))
                                {
                                    _citations.Add(entry);
                                    Log($"捕获引用: {entry.Substring(0, Math.Min(80, entry.Length))}", LogLevel.Debug);
                                }
                            }
                        }
                    }
                }

                ExtractFromElement(json, "");

                if (json.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    ExtractFromElement(dataProp, "");
                if (json.TryGetProperty("event_data", out var edProp) && edProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    ExtractFromElement(edProp, "");
            }
            catch { }
        }

        /// <summary>
        /// 解析 SSE 格式数据流
        /// </summary>
        private void ProcessSseData(string sseText)
        {
            using var reader = new StringReader(sseText);
            string? line;
            string currentEventType = "";

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    currentEventType = "";
                    continue;
                }

                line = line.Trim();

                if (line.StartsWith("event:"))
                {
                    currentEventType = line.Substring(6).Trim();
                    continue;
                }
                else if (line.StartsWith("data:"))
                {
                    string data = line.Substring(5).Trim();

                    if (data == "[DONE]")
                    {
                        if (_isStreaming)
                            EnqueueChunk("", true);
                        else if (_responseTcs != null && _responseBuffer.Length > 0)
                            _responseTcs.TrySetResult(_responseBuffer.ToString());
                        return;
                    }

                    if (SseParser.IsInternalMessage(data, currentEventType)) continue;

                    string? content = null;

                    if (_adapter != null)
                    {
                        content = _adapter.ExtractContentFromSseData(data, currentEventType);
                    }

                    if (content == null)
                        content = SseParser.ExtractContent(data);

                    if (!string.IsNullOrEmpty(content))
                    {
                        if (_isStreaming)
                            EnqueueChunk(content, false);
                        else
                        {
                            _responseBuffer.Append(content);
                            Log($"累计收到内容: {_responseBuffer.Length} 字符", LogLevel.Debug);
                        }
                    }
                    else
                    {
                        if (data.Contains("\"finish_reason\"") && data.Contains("\"stop\""))
                        {
                            if (_isStreaming)
                                EnqueueChunk("", true);
                            else if (_responseTcs != null && _responseBuffer.Length > 0)
                                _responseTcs.TrySetResult(_responseBuffer.ToString());
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 将内容 chunk 推入队列并通知消费者
        /// </summary>
        private void EnqueueChunk(string text, bool isDone)
        {
            _streamChunks.Enqueue((text, isDone));
            _streamSignal.Release();

            if (!string.IsNullOrEmpty(text))
            {
                _responseBuffer.Append(text);
            }
        }

        private string? ExtractContentFromMessage(string message)
        {
            try
            {
                // 优先尝试网络拦截格式
                if (message.Contains("NETWORK_DATA"))
                {
                    var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                    if (json.TryGetProperty("data", out var dataProp))
                    {
                        string dataStr = dataProp.GetString() ?? "";
                        var content = SseParser.ExtractContent(dataStr);
                        if (!string.IsNullOrEmpty(content)) return content;
                    }
                }

                // Fallback: 旧的消息格式解析
                string[] contentKeys = { "\"content\"", "\"v\"", "\"delta\"", "\"text\"" };

                foreach (var key in contentKeys)
                {
                    var idx = message.IndexOf(key);
                    if (idx > 0)
                    {
                        var start = message.IndexOf('"', idx + key.Length) + 1;
                        if (start <= 0) continue;

                        var end = -1;
                        int i = start;
                        while (i < message.Length)
                        {
                            if (message[i] == '"' && (i == 0 || message[i - 1] != '\\'))
                            {
                                end = i;
                                break;
                            }
                            i++;
                        }

                        if (end > start)
                        {
                            return message.Substring(start, end - start)
                                .Replace("\\n", "\n")
                                .Replace("\\\"", "\"")
                                .Replace("\\\\", "\\")
                                .Replace("\\r", "\r")
                                .Replace("\\t", "\t");
                        }
                    }
                }

                if (_adapter != null)
                {
                    var sseEvent = _adapter.ParseSseData(message);
                    if (sseEvent != null && !string.IsNullOrEmpty(sseEvent.Data))
                        return sseEvent.Data;
                }
            }
            catch { }

            return null;
        }

        #region Prompt 处理（非流式 fallback）

        /// <summary>
        /// 非流式发送 prompt，等待完整响应
        /// </summary>
        public async Task<string> SendPromptAsync(string modelId, string prompt)
        {
            return await await Dispatcher.InvokeAsync(() => SendPromptCoreAsync(modelId, prompt));
        }

        private async Task<string> SendPromptCoreAsync(string modelId, string prompt)
        {
            if (_adapter == null || WebView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 未就绪");

            // 等待请求锁
            if (!await _requestLock.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("正在处理其他请求，请稍候");

            _requestCts = new CancellationTokenSource();
            _responseBuffer.Clear();
            _responseTcs = new TaskCompletionSource<string>();
            _streamStartTime = DateTime.Now;

            try
            {
                Log($"发送 Prompt: {prompt.Substring(0, Math.Min(100, prompt.Length))}...", LogLevel.Info);

                await SwitchModeBeforePromptAsync(modelId);
                await InjectAndSendPromptAsync(prompt);

                // 等待完整响应（最多 90 秒）
                var timeoutTask = Task.Delay(90000, _requestCts.Token);
                var completedTask = await Task.WhenAny(_responseTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log("请求超时", LogLevel.Warn);
                    return _responseBuffer.Length > 0 ? _responseBuffer.ToString() : "请求超时";
                }

                string result = await _responseTcs.Task;
                Log($"收到回复，长度: {result.Length}", LogLevel.Info);
                return result;
            }
            catch (Exception ex)
            {
                Log($"SendPromptAsync 异常: {ex.Message}", LogLevel.Error);
                return $"[错误] {ex.Message}";
            }
            finally
            {
                _responseTcs = null;
                try { _requestCts?.Cancel(); } catch { }
                _requestCts?.Dispose();
                _requestCts = null;
                _requestLock.Release();
            }
        }

        #endregion

        #region Prompt 处理（流式）

        /// <summary>
        /// 流式发送 prompt，通过 streamCallback 实时回调每个 chunk
        /// </summary>
        public async Task<string> SendPromptStreamAsync(string modelId, string prompt, Action<string, bool> streamCallback)
        {
            return await await Dispatcher.InvokeAsync(() => SendPromptStreamCoreAsync(modelId, prompt, streamCallback));
        }

        private async Task<string> SendPromptStreamCoreAsync(string modelId, string prompt, Action<string, bool> streamCallback)
        {
            if (_adapter == null || WebView.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 未就绪");

            // 等待请求锁
            if (!await _requestLock.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("正在处理其他请求，请稍候");

            _requestCts = new CancellationTokenSource();
            _isStreaming = true;
            _responseBuffer.Clear();
            _streamChunks = new ConcurrentQueue<(string text, bool isDone)>();
            _streamSignal = new SemaphoreSlim(0);
            _streamStartTime = DateTime.Now;

            try
            {
                Log($"[流式] 发送 Prompt: {prompt.Substring(0, Math.Min(100, prompt.Length))}...", LogLevel.Info);

                await SwitchModeBeforePromptAsync(modelId);

                // 注入 prompt 并发送
                await InjectAndSendPromptAsync(prompt);

                // 消费 chunk 队列，实时回调
                while (true)
                {
                    var gotSignal = await _streamSignal.WaitAsync(TimeSpan.FromSeconds(60), _requestCts.Token);

                    // 批量处理所有待处理的 chunks
                    while (_streamChunks.TryDequeue(out var chunk))
                    {
                        if (!string.IsNullOrEmpty(chunk.text))
                        {
                            streamCallback(chunk.text, false);
                        }

                        if (chunk.isDone)
                        {
                            streamCallback("", true);
                            Log($"[流式] 完成，总长度: {_responseBuffer.Length}", LogLevel.Info);
                            return _responseBuffer.ToString();
                        }
                    }

                    // 超时检查
                    if (!gotSignal || (DateTime.Now - _streamStartTime).TotalSeconds > 90)
                    {
                        Log("[流式] 超时或无更多数据", LogLevel.Warn);
                        if (_responseBuffer.Length > 0)
                        {
                            streamCallback("", true);
                            return _responseBuffer.ToString();
                        }
                        throw new TimeoutException("流式响应超时");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("[流式] 请求已取消", LogLevel.Warn);
                if (_responseBuffer.Length > 0)
                {
                    streamCallback("", true);
                    return _responseBuffer.ToString();
                }
                return "";
            }
            catch (Exception ex)
            {
                Log($"[流式] 异常: {ex.Message}", LogLevel.Error);
                if (_responseBuffer.Length > 0)
                {
                    streamCallback("", true);
                    return _responseBuffer.ToString();
                }
                return $"[错误] {ex.Message}";
            }
            finally
            {
                _isStreaming = false;
                try { _requestCts?.Cancel(); } catch { }
                _requestCts?.Dispose();
                _requestCts = null;
                _requestLock.Release();
            }
        }

        #endregion

        #region Prompt 注入 + PoW 处理

        /// <summary>
        /// 注入 prompt 到 WebView 并触发发送，处理 PoW 流程
        /// </summary>
        private async Task InjectAndSendPromptAsync(string prompt)
        {
            DeepSeekAdapter? dsAdapter = _adapter as DeepSeekAdapter;
            bool needsPow = dsAdapter != null;

            _citations.Clear();

            if (!_controllerAvailable || WebView.CoreWebView2 == null)
            {
                Log("Bridge 未就绪，等待页面加载...", LogLevel.Warn);
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(500);
                    if (_controllerAvailable && WebView.CoreWebView2 != null) break;
                }
                if (!_controllerAvailable || WebView.CoreWebView2 == null)
                {
                    Log("Bridge 等待超时，尝试继续执行", LogLevel.Warn);
                }
            }

            string diagnosticScript = @"
(function() {
    var result = { bridge: 'none', interceptor: 'unknown', pageReady: false, consoleLines: [] };
    if (window.chrome && window.chrome.webview) {
        result.bridge = 'ready';
    } else if (typeof window.chrome === 'undefined') {
        result.bridge = 'no_chrome';
    } else {
        result.bridge = 'no_webview';
    }
    if (typeof window.fetch === 'function') {
        result.interceptor = (window.fetch.toString().indexOf('NETWORK_DATA') > -1) ? 'active' : 'passive';
    }
    result.pageReady = (document.readyState === 'complete' || document.readyState === 'interactive');
    result.url = window.location.href;
    
    // 检查输入框是否存在
    try {
        var input = document.getElementById('chat-input') || document.querySelector('textarea');
        result.hasInput = (input !== null);
        if (input) {
            result.inputVisible = (input.offsetParent !== null);
            result.inputReadOnly = input.readOnly;
            result.inputDisabled = input.disabled;
        }
    } catch (e) {
        result.inputCheckError = e.toString();
    }
    
    return JSON.stringify(result);
})();
";
            try
            {
                string diagResultRaw = await WebView.CoreWebView2!.ExecuteScriptAsync(diagnosticScript);
                string diagResult = diagResultRaw.Trim('"').Replace("\\\"", "\"");
                Log($"诊断: {diagResult}", LogLevel.Info);

                if (diagResult.Contains("no_chrome") || diagResult.Contains("no_webview"))
                {
                    Log("Bridge 不可用，等待重试...", LogLevel.Warn);
                    await Task.Delay(3000);
                    diagResultRaw = await WebView.CoreWebView2.ExecuteScriptAsync(diagnosticScript);
                    diagResult = diagResultRaw.Trim('"').Replace("\\\"", "\"");
                    Log($"重试诊断: {diagResult}", LogLevel.Info);
                }

                if (diagResult.Contains("\"interceptor\":\"passive\""))
                {
                    Log("拦截器未激活，重新注入网络拦截脚本...", LogLevel.Warn);
                    await WebView.CoreWebView2.ExecuteScriptAsync(_adapter!.GetNetworkInterceptorScript());
                    Log("已重新注入拦截脚本", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log($"诊断检查失败: {ex.Message}，尝试继续", LogLevel.Warn);
            }

            if (needsPow)
            {
                dsAdapter!.CapturedPowChallenge = null;
                _powChallengeTcs = new TaskCompletionSource<DeepSeekPowChallenge>();
            }

            int retryCount = 0;
            const int maxRetries = 2;

            while (retryCount < maxRetries)
            {
                try
                {
                    await _adapter!.InjectPromptAsync(WebView, prompt);

                    // DeepSeek PoW 流程
                    if (needsPow)
                    {
                        var challengeTimeout = Task.Delay(10000, _requestCts?.Token ?? CancellationToken.None);
                        var challengeTask = await Task.WhenAny(_powChallengeTcs!.Task, challengeTimeout);

                        if (challengeTask != challengeTimeout && _powChallengeTcs.Task.IsCompleted)
                        {
                            var challenge = await _powChallengeTcs.Task;
                            Log($"正在解决 PoW 挑战 (difficulty={challenge.difficulty})...", LogLevel.Info);

                            // 后台解决 PoW
                            string powSolution = await Task.Run(
                                () => dsAdapter!.SolvePowAsync(challenge, _requestCts?.Token ?? CancellationToken.None),
                                _requestCts?.Token ?? CancellationToken.None);

                            Log("PoW 已解决，注入解决方案...", LogLevel.Info);

                            // 注入解决方案并触发发送
                            await dsAdapter!.InjectPowSolutionAsync(WebView, powSolution);
                            await Task.Delay(500, _requestCts?.Token ?? CancellationToken.None);
                        }
                        else
                        {
                            Log("PoW 挑战捕获超时，继续等待响应...", LogLevel.Warn);
                        }
                    }

                    return; // 成功
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    Log($"注入失败（第 {retryCount} 次尝试）: {ex.Message}", LogLevel.Warn);

                    if (retryCount >= maxRetries)
                        throw;

                    await Task.Delay(1000, _requestCts?.Token ?? CancellationToken.None);
                }
            }
        }

        #endregion

        private int _logLineCount = 0;
        private const int MaxLogLines = 1000;

        internal void Log(string message, LogLevel level = LogLevel.Info)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            string capturedMessage = message;
            LogLevel capturedLevel = level;

            Dispatcher.BeginInvoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
                _logLineCount++;

                if (_logLineCount > MaxLogLines)
                {
                    var text = LogBox.Text;
                    var firstNewline = text.IndexOf('\n');
                    if (firstNewline >= 0)
                    {
                        LogBox.Text = text.Substring(firstNewline + 1);
                        _logLineCount--;
                    }
                }

                OnLogMessage?.Invoke(capturedMessage, capturedLevel);
            });
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendFromInputBoxAsync();
        }

        public async Task TriggerSendAsync()
        {
            await SendFromInputBoxAsync();
        }

        private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            {
                e.Handled = true;
                Dispatcher.Invoke(async () => await SendFromInputBoxAsync());
            }
        }

        private async Task SendFromInputBoxAsync()
        {
            if (string.IsNullOrWhiteSpace(InputBox.Text))
                return;

            string prompt = InputBox.Text;
            InputBox.Clear();

            if (_adapter != null && WebView.CoreWebView2 != null)
            {
                try
                {
                    await SendPromptAsync("", prompt);
                }
                catch (Exception ex)
                {
                    Log($"发送失败: {ex.Message}", LogLevel.Error);
                }
            }
            else
            {
                Log("[测试] WebView2 未就绪", LogLevel.Warn);
            }
        }

        private async void BtnRestartBrowser_Click(object sender, RoutedEventArgs e)
        {
            await RestartBrowserAsync();
        }

        public async Task TriggerRestartBrowserAsync()
        {
            await RestartBrowserAsync();
        }

        private async Task RestartBrowserAsync()
        {
            Log("重启浏览器内核...", LogLevel.Info);

            try
            {
                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.Stop();
                    PlaceholderText.Text = "正在刷新页面...";
                    ShowLoadingSpinner();
                    WebView.CoreWebView2.Reload();
                    Log("页面正在刷新...", LogLevel.Info);
                }
                else
                {
                    PlaceholderText.Text = "正在重新初始化...";
                    ShowLoadingSpinner();
                    await InitializeWebView2Async();
                    Log("浏览器内核已重启", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                HideLoadingSpinner();
                Log($"重启失败: {ex.Message}", LogLevel.Error);
            }
        }

        private async void BtnStopService_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                StopChannel();
            }
            else
            {
                BtnStopService.IsEnabled = false;
                BtnStopService.Content = "⏳ 正在启动...";
                PanelStatus.Text = "⏳ 正在初始化...";
                PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("TextSecondary");

                try
                {
                    await InitializeAsync();
                }
                catch (Exception ex)
                {
                    Log($"启动失败: {ex.Message}", LogLevel.Error);
                }

                BtnStopService.IsEnabled = true;
                if (_isRunning)
                {
                    BtnStopService.Content = "⏹ 停止 HTTP 服务";
                    BtnStopService.Background = (Brush)System.Windows.Application.Current.FindResource("AccentRed");
                }
                else
                {
                    BtnStopService.Content = "▶ 启动 HTTP 服务";
                    BtnStopService.Background = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
                }
            }
        }

        public void StopChannel()
        {
            // 关闭 HTTP 服务 + 清理 WebView2
            _httpServer?.Stop();

            try
            {
                _requestCts?.Cancel();

                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    WebView.NavigationCompleted -= WebView_NavigationCompleted;
                    WebView.CoreWebView2.Stop();
                }
            }
            catch (Exception ex)
            {
                Log($"清理 WebView2 出错: {ex.Message}", LogLevel.Warn);
            }

            WebView.Visibility = Visibility.Collapsed;
            PlaceholderText.Text = "WebView2 已停止，点击「启动 HTTP 服务」重新加载";
            ShowLoadingSpinner();

            _controllerAvailable = false;
            _isRunning = false;
            _pageLoadTcs?.TrySetCanceled();
            BtnStopService.Content = "▶ 启动 HTTP 服务";
            BtnStopService.Background = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
            PanelStatus.Text = "⏹ 服务已停止";
            PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("TextSecondary");
            Log("渠道已停止（HTTP 服务 + WebView2）", LogLevel.Warn);
        }

        private void BtnProxySettings_Click(object sender, RoutedEventArgs e)
        {
            _config.ProxySettings ??= new Models.ProxySettings();

            var dialog = new ProxyConfigWindow(_config.ProxySettings)
            {
                Owner = System.Windows.Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                _config.ProxySettings = dialog.Settings;
                OnProxySettingsChanged?.Invoke();
                Log($"代理配置已更新（启用={_config.ProxySettings.Enabled}，节点数={_config.ProxySettings.Nodes.Count}）", LogLevel.Info);
            }
        }

        private string? GetProxyArgs()
        {
            var ps = _config.ProxySettings;
            if (ps == null || !ps.Enabled || ps.Nodes.Count == 0)
                return null;

            int idx = ps.SelectedNodeIndex;
            if (idx < 0 || idx >= ps.Nodes.Count) idx = 0;
            var node = ps.Nodes[idx];

            string proxyServer = $"--proxy-server={node.Type}://{node.Address}:{node.Port}";

            if (ps.Mode == "bypass_cn")
            {
                proxyServer += " --proxy-bypass-list=<local>;*.cn;*.com.cn;*.org.cn;*.net.cn;*.gov.cn;*.edu.cn";
            }

            return proxyServer;
        }

        private void HideWebView()
        {
            try
            {
                if (WebView.CoreWebView2 != null)
                {
                    WebView.Visibility = Visibility.Collapsed;
                }
                ShowLoadingSpinner();
                PlaceholderText.Text = "WebView2 已停止，点击「启动 HTTP 服务」重新加载";
                Log("WebView2 已隐藏", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Log($"隐藏 WebView2 失败: {ex.Message}", LogLevel.Warn);
            }
        }

        private void ShowWebView()
        {
            try
            {
                ShowLoadingSpinner();
                WebView.Visibility = Visibility.Visible;
                Log("WebView2 已显示，开始初始化...", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                HideLoadingSpinner();
                Log($"显示 WebView2 失败: {ex.Message}", LogLevel.Warn);
            }
        }

        private void ShowLoadingSpinner()
        {
            LoadingPlaceholder.Visibility = Visibility.Visible;
        }

        private void HideLoadingSpinner()
        {
            LoadingPlaceholder.Visibility = Visibility.Collapsed;
        }

        private async Task CopyToClipboardAsync(string text)
        {
            var tcs = new TaskCompletionSource<bool>();
            var thread = new Thread(() =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            try
            {
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Log($"已复制: {text}", LogLevel.Debug);
            }
            catch (TimeoutException)
            {
                Log("复制超时（剪贴板被占用）", LogLevel.Error);
            }
            catch (Exception ex)
            {
                Log($"复制失败: {ex.Message}", LogLevel.Error);
            }
        }

        private async void BtnCopyBaseUrl_Click(object sender, RoutedEventArgs e)
            => await CopyToClipboardAsync(TxtBaseUrl.Text);

        private async void BtnCopyApiKey_Click(object sender, RoutedEventArgs e)
            => await CopyToClipboardAsync(TxtApiKey.Text);

        private async void BtnCopyModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ModelInfo model)
                await CopyToClipboardAsync(model.Id);
        }

        private async void ModelId_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox txt && !string.IsNullOrEmpty(txt.Text))
                await CopyToClipboardAsync(txt.Text);
        }

        private async void BtnTestModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not ModelInfo model)
                return;

            if (_adapter == null || WebView.CoreWebView2 == null)
            {
                Log("[测试] WebView2 未就绪，无法测试", LogLevel.Warn);
                return;
            }

            if (!_requestLock.Wait(0))
            {
                Log("[测试] 正在处理其他请求，请稍候", LogLevel.Warn);
                return;
            }

            _requestLock.Release();

            btn.IsEnabled = false;
            btn.Content = "⏳";
            Log($"[测试] 开始测试模型: {model.Id}，发送: hi", LogLevel.Info);

            try
            {
                var result = await SendPromptAsync(model.Id, "hi");
                if (result.StartsWith("[错误]") || result == "请求超时")
                {
                    Log($"[测试] 模型 {model.Id} 测试失败: {result}", LogLevel.Error);
                }
                else
                {
                    Log($"[测试] 模型 {model.Id} 测试成功，回复长度: {result.Length}", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log($"[测试] 模型 {model.Id} 测试异常: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "测试";
            }
        }

        private async void BtnDeepThink_Checked(object sender, RoutedEventArgs e)
        {
            _deepThinkEnabled = true;
            Log("深度思考模式已开启", LogLevel.Info);
            await SwitchModelModeAsync();
        }

        private async void BtnDeepThink_Unchecked(object sender, RoutedEventArgs e)
        {
            _deepThinkEnabled = false;
            Log("深度思考模式已关闭", LogLevel.Info);
            await SwitchModelModeAsync();
        }

        private async void BtnSearch_Checked(object sender, RoutedEventArgs e)
        {
            _searchEnabled = true;
            Log("智能搜索模式已开启", LogLevel.Info);
            await SwitchModelModeAsync();
        }

        private async void BtnSearch_Unchecked(object sender, RoutedEventArgs e)
        {
            _searchEnabled = false;
            Log("智能搜索模式已关闭", LogLevel.Info);
            await SwitchModelModeAsync();
        }

        private async Task SwitchModelModeAsync()
        {
            if (_adapter == null || WebView.CoreWebView2 == null) return;
            try
            {
                string script = _adapter.GetModeSwitchScript(_deepThinkEnabled, _searchEnabled);
                if (!string.IsNullOrEmpty(script))
                {
                    await WebView.CoreWebView2.ExecuteScriptAsync(script);
                    Log($"已切换模式: DeepThink={_deepThinkEnabled}, Search={_searchEnabled}", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log($"切换模式失败: {ex.Message}", LogLevel.Warn);
            }
        }

        private async Task SwitchModeBeforePromptAsync(string modelId)
        {
            if (_adapter == null || WebView.CoreWebView2 == null) return;

            bool wantDeepThink = _deepThinkEnabled;
            bool wantSearch = _searchEnabled;

            if (!string.IsNullOrEmpty(modelId))
            {
                if (_config.Id == "deepseek")
                {
                    wantDeepThink = modelId.Contains("reasoner");
                    wantSearch = modelId.Contains("search");
                }
            }

            if (wantDeepThink != _deepThinkEnabled || wantSearch != _searchEnabled)
            {
                try
                {
                    string script = _adapter.GetModeSwitchScript(wantDeepThink, wantSearch);
                    if (!string.IsNullOrEmpty(script))
                    {
                        await WebView.CoreWebView2.ExecuteScriptAsync(script);
                        Log($"API模式切换: DeepThink={wantDeepThink}, Search={wantSearch}", LogLevel.Info);
                    }

                    _deepThinkEnabled = wantDeepThink;
                    _searchEnabled = wantSearch;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        BtnDeepThink.IsChecked = _deepThinkEnabled;
                        BtnSearch.IsChecked = _searchEnabled;
                    });
                }
                catch (Exception ex)
                {
                    Log($"API模式切换失败: {ex.Message}", LogLevel.Warn);
                }
            }
        }
    }
}
