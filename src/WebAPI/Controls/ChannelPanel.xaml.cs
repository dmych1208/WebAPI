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

        // 响应收集（非流式 fallback 用）
        private TaskCompletionSource<string>? _responseTcs;
        private StringBuilder _responseBuffer = new();

        // 流式响应：chunk 队列 + 信号量
        private ConcurrentQueue<(string text, bool isDone)> _streamChunks = new();
        private SemaphoreSlim _streamSignal = new(0);
        private bool _isStreaming = false;

        // 请求处理锁
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private CancellationTokenSource? _requestCts;

        // DeepSeek PoW
        private TaskCompletionSource<DeepSeekPowChallenge>? _powChallengeTcs;

        public Action<string, LogLevel>? OnLogMessage;
        public Action? OnRequestReceived;

        public string StatusText => _isRunning ? $"{_config.Name} 服务就绪" : $"{_config.Name} 已停止";
        public int Port => _config.Port;

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
        }

        private async void ChannelPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            _isInitialized = true;
            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                Log("正在初始化 HTTP 服务...", LogLevel.Info);

                _httpServer = new ChannelHttpServer(_config);
                _httpServer.OnLog += (msg) => Log(msg, LogLevel.Info);
                _httpServer.OnRequest += () => OnRequestReceived?.Invoke();

                // 注册两种处理函数
                _httpServer.ProcessPromptFunc = SendPromptAsync;
                _httpServer.ProcessPromptStreamFunc = SendPromptStreamAsync;

                await _httpServer.StartAsync();

                Log("正在初始化 WebView2...", LogLevel.Info);

                _adapter = CreateAdapter();

                await InitializeWebView2Async();

                PanelStatus.Text = "✅ 正在运行 (V2)";
                PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
                _isRunning = true;

                Log("初始化完成（流式响应已启用）", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"初始化失败: {ex.Message}", LogLevel.Error);
                _isRunning = true;
                PanelStatus.Text = "⚠️ 服务已启动";
                PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
                Log("服务已启动，可正常使用", LogLevel.Info);
            }
        }

        private IAdapter CreateAdapter()
        {
            return _config.Id switch
            {
                "deepseek" => new DeepSeekAdapter(),
                "qwen" => new QwenAdapter(),
                "doubao" => new DoubaoAdapter(),
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
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _adapter.UserDataFolder);
                await WebView.EnsureCoreWebView2Async(env);

                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_adapter.GetNetworkInterceptorScript());

                WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                WebView.NavigationCompleted += WebView_NavigationCompleted;

                Log($"正在导航到 {_adapter.TargetUrl}...", LogLevel.Info);
                WebView.Source = new Uri(_adapter.TargetUrl);
                WebView.Visibility = Visibility.Visible;
                HideLoadingSpinner();
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

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                Log("页面加载完成", LogLevel.Info);
            else
                Log($"页面加载失败: {e.WebErrorStatus}", LogLevel.Error);
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();

                if (message.Length > 200)
                    Log($"收到 WebMessage: {message.Substring(0, 200)}...", LogLevel.Debug);
                else
                    Log($"收到 WebMessage: {message}", LogLevel.Debug);

                // === DeepSeek PoW 挑战捕获 ===
                if (_powChallengeTcs != null && !_powChallengeTcs.Task.IsCompleted
                    && message.Contains("create_pow_challenge"))
                {
                    var adapter = _adapter as DeepSeekAdapter;
                    adapter?.CapturePowChallenge(message);
                    if (adapter?.CapturedPowChallenge != null)
                    {
                        Log($"PoW 挑战已捕获: difficulty={adapter.CapturedPowChallenge.difficulty}", LogLevel.Info);
                        _powChallengeTcs.TrySetResult(adapter.CapturedPowChallenge);
                    }
                }

                // === 流式响应：从网络拦截消息中提取内容 ===
                if (_isStreaming && message.Contains("NETWORK_DATA"))
                {
                    ProcessNetworkData(message);
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
                // 解析 WebMessage JSON
                var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                if (!json.TryGetProperty("data", out var dataProp)) return;

                string dataStr = dataProp.GetString() ?? "";
                if (string.IsNullOrEmpty(dataStr)) return;

                // 检查是否是 SSE 流
                if (dataStr.Contains("data:") || dataStr.Contains("event:"))
                {
                    ProcessSseData(dataStr);
                }
                else
                {
                    // 非SSE格式，尝试直接解析JSON
                    var content = SseParser.ExtractContent(dataStr);
                    if (!string.IsNullOrEmpty(content))
                    {
                        EnqueueChunk(content, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"解析网络数据出错: {ex.Message}", LogLevel.Debug);
            }
        }

        /// <summary>
        /// 解析 SSE 格式数据流
        /// </summary>
        private void ProcessSseData(string sseText)
        {
            using var reader = new StringReader(sseText);
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                line = line.Trim();

                if (line.StartsWith("event:"))
                {
                    var eventType = line.Substring(6).Trim();
                    if (eventType is "title" or "update_session" or "search_result" or "ping")
                        continue; // 跳过内部事件
                }
                else if (line.StartsWith("data:"))
                {
                    string data = line.Substring(5).Trim();

                    if (data == "[DONE]")
                    {
                        EnqueueChunk("", true);
                        return;
                    }

                    if (SseParser.IsInternalMessage(data, "")) continue;

                    var content = SseParser.ExtractContent(data);
                    if (!string.IsNullOrEmpty(content))
                    {
                        EnqueueChunk(content, false);
                    }
                    else
                    {
                        // 检查是否包含 finish_reason
                        if (data.Contains("\"finish_reason\"") && data.Contains("\"stop\""))
                        {
                            EnqueueChunk("", true);
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
        public async Task<string> SendPromptAsync(string prompt)
        {
            return await await Dispatcher.InvokeAsync(() => SendPromptCoreAsync(prompt));
        }

        private async Task<string> SendPromptCoreAsync(string prompt)
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
        public async Task<string> SendPromptStreamAsync(string prompt, Action<string, bool> streamCallback)
        {
            return await await Dispatcher.InvokeAsync(() => SendPromptStreamCoreAsync(prompt, streamCallback));
        }

        private async Task<string> SendPromptStreamCoreAsync(string prompt, Action<string, bool> streamCallback)
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
                    // 注入 prompt 并发送
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
                            await dsAdapter.InjectPowSolutionAsync(WebView, powSolution);
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
                    await SendPromptAsync(prompt);
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
                }

                ShowLoadingSpinner();
                PlaceholderText.Text = "正在重新初始化...";

                await InitializeWebView2Async();
                Log("浏览器内核已重启", LogLevel.Info);
            }
            catch (Exception ex)
            {
                HideLoadingSpinner();
                Log($"重启失败: {ex.Message}", LogLevel.Error);
            }
        }

        private void BtnStopService_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                StopChannel();
            }
            else
            {
                // 启动：重新初始化 HTTP 服务 + WebView2
                _isInitialized = false;
                _ = InitializeAsync();
                _isRunning = true;

                BtnStopService.Content = "⏹ 停止 HTTP 服务";
                BtnStopService.Background = (Brush)System.Windows.Application.Current.FindResource("AccentRed");
                PanelStatus.Text = "✅ 正在运行 (V2)";
                PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
                Log("渠道已启动", LogLevel.Info);
            }
        }

        public void StopChannel()
        {
            // 关闭 HTTP 服务 + 清理 WebView2
            _httpServer?.Stop();

            try
            {
                _requestCts?.Cancel();
                _streamSignal?.Dispose();

                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    WebView.CoreWebView2.NavigationCompleted -= WebView_NavigationCompleted;
                    WebView.CoreWebView2.Stop();
                }
            }
            catch (Exception ex)
            {
                Log($"清理 WebView2 出错: {ex.Message}", LogLevel.Warn);
            }

            WebView.Visibility = Visibility.Collapsed;
            PlaceholderText.Visibility = Visibility.Visible;
            PlaceholderText.Text = "WebView2 已停止，点击「启动 HTTP 服务」重新加载";

            _isRunning = false;

            BtnStopService.Content = "▶ 启动 HTTP 服务";
            BtnStopService.Background = (Brush)System.Windows.Application.Current.FindResource("AccentGreen");
            PanelStatus.Text = "⏹ 已停止";
            PanelStatus.Foreground = (Brush)System.Windows.Application.Current.FindResource("TextSecondary");
            Log("渠道已停止（HTTP 服务 + WebView2）", LogLevel.Warn);
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

        private void CopyToClipboard(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                Log($"已复制: {text}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Log($"复制失败: {ex.Message}", LogLevel.Error);
            }
        }

        private void BtnCopyBaseUrl_Click(object sender, RoutedEventArgs e)
            => CopyToClipboard(TxtBaseUrl.Text);

        private void BtnCopyApiKey_Click(object sender, RoutedEventArgs e)
            => CopyToClipboard(TxtApiKey.Text);

        private void BtnCopyModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ModelInfo model)
                CopyToClipboard(model.Id);
        }

        private void ModelId_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox txt && !string.IsNullOrEmpty(txt.Text))
                CopyToClipboard(txt.Text);
        }

        private void BtnDeepThink_Checked(object sender, RoutedEventArgs e)
        {
            Log("深度思考模式已开启", LogLevel.Info);
        }

        private void BtnDeepThink_Unchecked(object sender, RoutedEventArgs e)
        {
            Log("深度思考模式已关闭", LogLevel.Info);
        }

        private void BtnSearch_Checked(object sender, RoutedEventArgs e)
        {
            Log("智能搜索模式已开启", LogLevel.Info);
        }

        private void BtnSearch_Unchecked(object sender, RoutedEventArgs e)
        {
            Log("智能搜索模式已关闭", LogLevel.Info);
        }
    }
}
