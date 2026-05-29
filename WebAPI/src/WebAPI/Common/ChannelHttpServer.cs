using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using WebAPI.Common;
using WebAPI.Models;

namespace WebAPI.Common
{
    public class ChannelHttpServer : IDisposable
    {
        private HttpListener? _listener;
        private readonly ChannelConfig _config;
        private bool _isRunning = false;
        private int _requestCount = 0;
        private SynchronizationContext? _uiSyncContext;

        // 请求队列：同一渠道同时只处理一个请求，后续请求排队
        private readonly SemaphoreSlim _requestSemaphore = new(1, 1);
        private readonly ConcurrentQueue<PendingRequest> _pendingQueue = new();
        private int _queueLength = 0;

        /// <summary>
        /// 流式响应回调：每当从 WebView 收到一段文本时调用
        /// 参数: (chunkText, isDone)
        /// </summary>
        public Action<string, bool>? OnStreamChunk { get; set; }

        /// <summary>
        /// 处理完整 prompt 并返回结果的函数（非流式 fallback）
        /// </summary>
        public Func<string, Task<string>>? ProcessPromptFunc { get; set; }

        /// <summary>
        /// 流式处理函数：注入 prompt 后，通过 OnStreamChunk 回调逐步返回数据
        /// 返回值：最终完整响应文本
        /// </summary>
        public Func<string, Action<string, bool>, Task<string>>? ProcessPromptStreamFunc { get; set; }

        public event Action<string>? OnLog;
        public event Action? OnRequest;

        public bool IsRunning => _isRunning;
        public int RequestCount => _requestCount;
        public int QueueLength => _queueLength;

        public ChannelHttpServer(ChannelConfig config)
        {
            _config = config;
        }

        public async Task StartAsync()
        {
            if (_isRunning) return;

            _uiSyncContext = SynchronizationContext.Current;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_config.Port}/");
                _listener.Start();
                _isRunning = true;

                OnLog?.Invoke($"HTTP 服务启动成功，监听端口: {_config.Port}");

                _ = ListenLoop();

                await Task.Delay(100);
            }
            catch (HttpListenerException)
            {
                OnLog?.Invoke($"端口 {_config.Port} 可能已被占用，尝试继续使用现有服务...");
                _isRunning = true;
                OnLog?.Invoke($"HTTP 服务监听中: {_config.Port}");
            }
        }

        private async Task ListenLoop()
        {
            try
            {
                while (_listener != null && _listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
            }
            catch (HttpListenerException) when (!_isRunning)
            {
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"监听循环异常: {ex.Message}");
            }
        }

        private async void HandleRequest(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _requestCount);

            if (_uiSyncContext != null)
                _uiSyncContext.Post(_ => OnRequest?.Invoke(), null);
            else
                OnRequest?.Invoke();

            try
            {
                AddCorsHeaders(ctx);

                var request = ctx.Request;
                var response = ctx.Response;

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                var path = request.Url?.AbsolutePath ?? "";

                switch (path)
                {
                    case "/v1/chat/completions":
                        await HandleChatCompletionsAsync(ctx);
                        break;
                    case "/v1/models":
                        HandleModels(ctx);
                        break;
                    case "/health":
                        HandleHealth(ctx);
                        break;
                    case "/shutdown":
                        HandleShutdown(ctx);
                        break;
                    default:
                        WriteError(response, "Not Found", 404);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"请求处理异常: {ex.Message}");
                try
                {
                    WriteError(ctx.Response, $"Internal Server Error: {ex.Message}", 500);
                }
                catch { }
            }
        }

        private async Task HandleChatCompletionsAsync(HttpListenerContext ctx)
        {
            var request = ctx.Request;
            var response = ctx.Response;

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            var chatReq = RequestParser.Parse(body);
            string fullPrompt = BuildConversationPrompt(chatReq);

            // 排队等待
            Interlocked.Increment(ref _queueLength);
            OnLog?.Invoke($"[排队] model={chatReq.Model}, stream={chatReq.Stream}, prompt_len={fullPrompt.Length}, 队列={_queueLength}");

            bool acquired = false;
            try
            {
                // 等待轮到自己（最多等 120 秒）
                acquired = await _requestSemaphore.WaitAsync(TimeSpan.FromSeconds(120));
                if (!acquired)
                {
                    WriteError(response, "请求超时：队列等待时间过长", 503);
                    return;
                }

                Interlocked.Decrement(ref _queueLength);

                if (chatReq.Stream)
                {
                    await HandleStreamResponseAsync(ctx, chatReq, fullPrompt);
                }
                else
                {
                    await HandleNormalResponseAsync(ctx, chatReq, fullPrompt);
                }
            }
            finally
            {
                if (acquired)
                    _requestSemaphore.Release();
                else
                    Interlocked.Decrement(ref _queueLength);
            }
        }

        /// <summary>
        /// 流式响应：从 WebView 实时拦截数据，逐 chunk 写入 HTTP Response
        /// </summary>
        private async Task HandleStreamResponseAsync(HttpListenerContext ctx, ChatRequest chatReq, string fullPrompt)
        {
            var response = ctx.Response;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.SendChunked = true;

            var id = ResponseConverter.GenerateId();
            var created = ResponseConverter.UnixTimestamp();

            // 发送 SSE 首个 chunk（role 字段）
            var roleChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, "", null, "assistant");
            await WriteSseAsync(response, roleChunk);

            string finalContent;

            if (ProcessPromptStreamFunc != null)
            {
                // 流式模式：每个 chunk 通过回调实时写入
                var chunkQueue = new ConcurrentQueue<(string text, bool isDone)>();
                var chunkSignal = new SemaphoreSlim(0);

                // 注册回调
                Action<string, bool> streamCallback = (text, isDone) =>
                {
                    chunkQueue.Enqueue((text, isDone));
                    chunkSignal.Release();
                };

                // 启动 prompt 处理（后台）
                var processTask = Task.Run(() => ProcessPromptStreamFunc(fullPrompt, streamCallback));

                var sb = new StringBuilder();

                try
                {
                    using var output = response.OutputStream;
                    using var writer = new StreamWriter(output, Encoding.UTF8) { AutoFlush = true };

                    while (true)
                    {
                        // 等待新 chunk 或完成
                        var signalTask = chunkSignal.WaitAsync(TimeSpan.FromSeconds(60));
                        var completed = await signalTask;

                        // 批量处理所有待处理的 chunks
                        while (chunkQueue.TryDequeue(out var chunk))
                        {
                            if (!string.IsNullOrEmpty(chunk.text))
                            {
                                sb.Append(chunk.text);
                                var sseChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, chunk.text, null, null);
                                await writer.WriteAsync(sseChunk);
                            }

                            if (chunk.isDone)
                            {
                                // 发送结束 chunk
                                var doneChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, "", "stop", null);
                                await writer.WriteAsync(doneChunk);
                                await writer.WriteAsync("data: [DONE]\n\n");
                                await writer.FlushAsync();
                                finalContent = sb.ToString();
                                return;
                            }
                        }

                        // 检查处理任务是否已异常完成
                        if (processTask.IsFaulted)
                        {
                            var ex = processTask.Exception?.InnerException;
                            var errorContent = ex?.Message ?? "未知错误";
                            var sseChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, $"[错误] {errorContent}", "stop", null);
                            await writer.WriteAsync(sseChunk);
                            await writer.WriteAsync("data: [DONE]\n\n");
                            await writer.FlushAsync();
                            return;
                        }

                        if (processTask.IsCompleted && chunkQueue.IsEmpty)
                        {
                            // 处理完成但没有收到 isDone 信号，用最终内容兜底
                            finalContent = await processTask;
                            if (!string.IsNullOrEmpty(finalContent) && sb.Length == 0)
                            {
                                // fallback：把完整内容当作一个 chunk 发送
                                var sseChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, finalContent, null, null);
                                await writer.WriteAsync(sseChunk);
                            }
                            var doneChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, "", "stop", null);
                            await writer.WriteAsync(doneChunk);
                            await writer.WriteAsync("data: [DONE]\n\n");
                            await writer.FlushAsync();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"流式写入异常: {ex.Message}");
                    finalContent = sb.ToString();
                }
            }
            else
            {
                // Fallback：无流式处理函数，用非流式方式获取后模拟流式输出
                string result;
                try
                {
                    result = ProcessPromptFunc != null
                        ? await ProcessPromptFunc(fullPrompt)
                        : $"[{_config.Name}] 无处理函数";
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[错误] 处理请求失败: {ex.Message}");
                    result = $"处理请求时出错: {ex.Message}";
                }

                // 模拟流式输出
                using var output = response.OutputStream;
                using var writer = new StreamWriter(output, Encoding.UTF8) { AutoFlush = true };

                var buffer = new StringBuilder();
                foreach (char c in result)
                {
                    buffer.Append(c);
                    if (buffer.Length >= 3 || c == result[^1])
                    {
                        var sseChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, buffer.ToString(), null, null);
                        await writer.WriteAsync(sseChunk);
                        await writer.FlushAsync();
                        buffer.Clear();
                        await Task.Delay(10);
                    }
                }

                var doneChunk = ResponseConverter.BuildSseChunk(id, chatReq.Model, "", "stop", null);
                await writer.WriteAsync(doneChunk);
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
            }
        }

        /// <summary>
        /// 非流式响应：等待完整内容后一次性返回
        /// </summary>
        private async Task HandleNormalResponseAsync(HttpListenerContext ctx, ChatRequest chatReq, string fullPrompt)
        {
            string result;
            try
            {
                result = ProcessPromptFunc != null
                    ? await ProcessPromptFunc(fullPrompt)
                    : $"[{_config.Name}] 无处理函数";
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[错误] 处理请求失败: {ex.Message}");
                result = $"处理请求时出错: {ex.Message}";
            }

            var json = ResponseConverter.BuildFullResponse(chatReq.Model, result);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            WriteJson(ctx.Response, json);
        }

        private static string BuildConversationPrompt(ChatRequest chatReq)
        {
            var sb = new StringBuilder();

            foreach (var msg in chatReq.Messages)
            {
                if (msg.Role == "system")
                {
                    sb.AppendLine($"【系统】{msg.Content}");
                }
            }

            foreach (var msg in chatReq.Messages)
            {
                if (msg.Role == "system") continue;

                string roleLabel = msg.Role == "user" ? "【用户】" : "【助手】";
                sb.AppendLine($"{roleLabel}{msg.Content}");
            }

            return sb.ToString().TrimEnd();
        }

        private void HandleModels(HttpListenerContext ctx)
        {
            var response = ctx.Response;
            response.ContentType = "application/json; charset=utf-8";

            var models = _config.Models.Select(m => new
            {
                id = m.Id,
                @object = "model",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                owned_by = _config.Id
            }).ToList();

            var result = new { @object = "list", data = models };
            WriteJson(response, System.Text.Json.JsonSerializer.Serialize(result));
        }

        private void HandleHealth(HttpListenerContext ctx)
        {
            var response = ctx.Response;
            response.ContentType = "application/json; charset=utf-8";
            var status = new { status = "ok", channel = _config.Id, port = _config.Port, requests = _requestCount, queue = _queueLength };
            WriteJson(response, System.Text.Json.JsonSerializer.Serialize(status));
        }

        private void HandleShutdown(HttpListenerContext ctx)
        {
            var response = ctx.Response;
            response.ContentType = "application/json; charset=utf-8";
            var msg = new { message = "shutting down" };
            WriteJson(response, System.Text.Json.JsonSerializer.Serialize(msg));

            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                Stop();
            });
        }

        private static async Task WriteSseAsync(HttpListenerResponse response, string sseData)
        {
            var buffer = Encoding.UTF8.GetBytes(sseData);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            await response.OutputStream.FlushAsync();
        }

        private static void WriteJson(HttpListenerResponse response, string json)
        {
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static void WriteError(HttpListenerResponse response, string message, int code)
        {
            try
            {
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = code;
                var error = new { error = new { message, type = "server_error", code } };
                var json = System.Text.Json.JsonSerializer.Serialize(error);
                WriteJson(response, json);
            }
            catch { }
        }

        private static void AddCorsHeaders(HttpListenerContext ctx)
        {
            var resp = ctx.Response;
            resp.Headers.Set("Access-Control-Allow-Origin", "*");
            resp.Headers.Set("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            resp.Headers.Set("Access-Control-Allow-Headers", "Content-Type, Authorization, x-api-key");
        }

        public void Stop()
        {
            _isRunning = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            OnLog?.Invoke("HTTP 服务已停止");
        }

        public void Dispose() => Stop();

        private class PendingRequest
        {
            public HttpListenerContext Context { get; set; } = null!;
            public TaskCompletionSource<bool> Completion { get; set; } = new();
        }
    }
}
