# WebAPI - 多平台 AI 对话 API 中继服务

基于 .NET 9 WPF + WebView2 构建的多平台 AI 对话 API 中继，将各大 AI 平台网页版转为兼容 OpenAI 格式的本地 API，方便各类 AI 客户端接入。

## 支持平台

| 平台 | 端口 | 是否需要代理 | 通信协议 |
|------|------|-------------|----------|
| DeepSeek | 55555 | 否 | SSE + PoW |
| 通义千问 | 56666 | 否 | SSE |
| 豆包 | 55556 | 否 | 自定义事件 |
| Gemini | 56667 | 是（内置代理支持） | SSE |
| Grok | 56668 | 是（内置代理支持） | SSE |
| 元宝增强 | 56669 | 否 | SSE |
| Kimi | 56670 | 否 | connect+json |

## 架构设计

### 整体架构

```
                         ┌─────────────────────┐
                         │    API 客户端        │
                         │ (ChatGPT-Next-Web等) │
                         └──────────┬──────────┘
                                    │ HTTP POST /v1/chat/completions
                                    ▼
┌─────────────────────────────────────────────────────────┐
│                     WebAPI 主进程                         │
│                                                         │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐ │
│  │ HttpListener │   │ HttpListener │   │ HttpListener │ │
│  │   :55555     │   │   :56666     │   │   :56670     │ │
│  │  (DeepSeek)  │   │  (通义千问)   │   │  (Kimi)      │ │
│  └──────┬───────┘   └──────┬───────┘   └──────┬───────┘ │
│         │                  │                  │           │
│  ┌──────▼──────────────────▼──────────────────▼───────┐  │
│  │              ChannelPanel (每个渠道一个)            │  │
│  │  ┌────────────────────────────────────────────┐   │  │
│  │  │           WebView2 浏览器实例                │   │  │
│  │  │  ┌──────────────────────────────────┐     │   │  │
│  │  │  │  JS 网络拦截器 (DocumentCreated)  │     │   │  │
│  │  │  │  - Fetch 拦截 (response.clone)   │     │   │  │
│  │  │  │  - XHR 拦截                       │     │   │  │
│  │  │  │  - EventSource 拦截               │     │   │  │
│  │  │  │    (addEventListener + onmessage) │     │   │  │
│  │  │  │  - window.chrome.webview.postMsg  │     │   │  │
│  │  │  └──────────────┬───────────────────┘     │   │  │
│  │  │                 │                          │   │  │
│  │  │         CoreWebView2_WebMessageReceived    │   │  │
│  │  │                 │                          │   │  │
│  │  │  ┌──────────────▼───────────────────┐     │   │  │
│  │  │  │  C# ProcessNetworkData           │     │   │  │
│  │  │  │  - SSE 数据解析                  │     │   │  │
│  │  │  │  - 平台特定 ExtractContent       │     │   │  │
│  │  │  │  - 引用来源捕获                  │     │   │  │
│  │  │  │  - 流式 Chunk 入队               │     │   │  │
│  │  │  └──────────────────────────────────┘     │   │  │
│  │  └────────────────────────────────────────────┘   │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

### 核心流程

#### 请求处理流程

```
POST /v1/chat/completions
  │
  ├─ HandleChatCompletionsAsync()
  │   ├─ 读取请求体 → ChatRequest
  │   ├─ 构建完整 Prompt（系统/用户/助手消息拼接）
  │   │
  │   ├─ 流式模式 (stream=true)
  │   │   └─ HandleStreamResponseAsync()
  │   │       ├─ 发送 SSE 首个 chunk（role 字段）
  │   │       ├─ 创建 chunkQueue + chunkSignal
  │   │       ├─ Task.Run → SendPromptStreamAsync()
  │   │       │   └─ Dispatcher.Invoke → UI 线程
  │   │       │       ├─ Bridge 就绪 + 拦截器诊断检查
  │   │       │       ├─ SwitchModeBeforePrompt()  模式切换
  │   │       │       ├─ InjectAndSendPromptAsync()  注入 Prompt
  │   │       │       │   ├─ DOM 注入 (CompositionEvent/原生 setter)
  │   │       │       │   ├─ DeepSeek PoW 挑战捕获 + CPU 并行求解
  │   │       │       │   └─ 点击发送按钮 (Enter + MouseEvent)
  │   │       │       └─ 循环等待 _streamSignal (60s 超时)
  │   │       │           └─ streamCallback(text, isDone) → chunkQueue
  │   │       └─ 循环消费 chunkQueue → SSE 写入 HTTP Response
  │   │           └─ data: [DONE] 结束
  │   │
  │   └─ 非流式模式 (stream=false)
  │       └─ SendPromptAsync()
  │           ├─ 等待完整响应 (90s 超时)
  │           └─ 构建 OpenAI 格式 JSON 响应
```

#### 数据拦截与回传

```
AI 平台网页 → fetch / XHR / EventSource
    │
    ├─ JS 网络拦截器 捕获响应数据
    │   ├─ fetch: response.clone() → 独立读取 clone body
    │   ├─ XHR: progress 事件 → 增量读取 responseText
    │   └─ EventSource: addEventListener + onmessage/onopen/onerror 拦截
    │
    ├─ window.chrome.webview.postMessage({type:"NETWORK_DATA", url, data})
    │
    ▼
CoreWebView2_WebMessageReceived (UI线程)
    │
    ├─ DeepSeek PoW 挑战检测 → CapturePowChallenge
    │
    ├─ ProcessNetworkData()
    │   ├─ 过滤搜索重写器
    │   ├─ CaptureCitations()
    │   ├─ ProcessSseData() → SseParser → PlatformAdapter.ExtractContentFromSseData()
    │   └─ EnqueueChunk() → _streamSignal.Release()
    │
    ▼
SendPromptStreamCoreAsync 消费循环
    │
    └─ streamCallback(text, isDone) → chunkQueue → HTTP SSE Response
```

### 信息交换策略

#### 1. JS → C#: WebMessage 通道

所有页面数据通过 `window.chrome.webview.postMessage()` 传递：

| 消息类型 | 说明 | 数据格式 |
|---------|------|---------|
| `NETWORK_DATA` | 拦截到的网络响应 chunk | `{type, url, data: "原始响应文本"}` |
| `NETWORK_DONE` | 网络请求完成 | `{type, url}` |

#### 2. C# → JS: ExecuteScriptAsync + AddScriptToExecuteOnDocumentCreatedAsync

| 注入方式 | 时机 | 用途 |
|---------|------|------|
| `AddScriptToExecuteOnDocumentCreatedAsync` | 每次文档创建时 | 网络拦截器（持久注入） |
| `ExecuteScriptAsync` | 每次请求时 | DOM 操作（输入 prompt） |
| `ExecuteScriptAsync` | 模式切换时 | 深度思考/联网搜索按钮 |

#### 3. 反检测机制

- `navigator.webdriver` 删除 + 伪造 `plugins`/`languages`
- Chrome 124 User-Agent 伪装
- 页面加载后重新注入反检测脚本
- CompositionEvent 输入法事件序列（绕过 React 检测）

#### 4. DeepSeek PoW 流程

```
发送 Prompt → DeepSeek 返回 PoW 挑战
    ↓
fetch clone 拦截 → postMessage → C# 捕获 challenge
    ↓
CPU 多核并行暴力搜索 SHA-256 nonce
    ↓
注入 PoW 解决方案 → 点击发送按钮
    ↓
fetch/EventSource 拦截 → SSE 数据流回传
```

## 项目结构

```
WebAPI/
├── src/WebAPI/
│   ├── Adapters/              # 平台适配器
│   │   ├── IAdapter.cs        # 适配器接口
│   │   ├── DeepSeekAdapter.cs # DeepSeek (含 PoW 挑战求解)
│   │   ├── QwenAdapter.cs     # 通义千问 (CompositionEvent 输入)
│   │   ├── DoubaoAdapter.cs   # 豆包
│   │   ├── GeminiAdapter.cs   # Gemini (fetch+XHR+EventSource)
│   │   ├── GrokAdapter.cs     # Grok
│   │   ├── YuanbaoAdapter.cs  # 元宝增强
│   │   └── KimiAdapter.cs     # Kimi (connect+json 协议)
│   ├── Common/
│   │   ├── ChannelHttpServer.cs # HTTP 服务器 (HttpListener)
│   │   ├── ConfigManager.cs     # 配置管理 (JSON 读写+热重载)
│   │   ├── LogManager.cs        # 日志管理
│   │   └── SseParser.cs         # SSE 数据解析
│   ├── Controls/
│   │   ├── ChannelPanel.xaml/.cs     # 渠道面板 (WebView2 宿主+诊断)
│   │   └── ProxyConfigWindow.xaml/.cs # 代理配置窗口
│   ├── Models/
│   │   ├── Models.cs           # ChannelConfig, ModelInfo, ChatRequest 等
│   │   └── ProxyModels.cs      # ProxyNode, ProxySettings
│   ├── App.xaml/.cs
│   └── MainWindow.xaml/.cs     # 主窗口 (标签页+状态栏+健康检查)
├── config/
│   └── settings.json           # 渠道 + 代理配置 (支持热重载)
└── WebAPI.sln
```

## 安装与运行

### 系统要求
- Windows 10 / 11
- .NET 9.0 SDK
- WebView2 Runtime (Windows 11 自带，Win10 需安装)

### 构建

```powershell
cd WebAPI
dotnet build
```

### 运行

```powershell
dotnet run --project src/WebAPI
# 或直接运行
.\src\WebAPI\bin\Debug\net9.0-windows\WebAPI.exe
```

### 客户端接入

以 OpenAI 兼容格式连接：

```
Base URL:  http://127.0.0.1:55555
API Key:   sk-any (任意值)
Model:     deepseek-chat / qwen-plus / kimi-k2.5-fast 等
```

## 配置说明

`config/settings.json` 支持热重载（修改后自动生效）：

```json
{
  "channels": [
    {
      "id": "deepseek",
      "name": "DeepSeek",
      "icon": "⚡",
      "enabled": true,
      "port": 55555,
      "targetUrl": "https://chat.deepseek.com/",
      "models": [
        { "id": "deepseek-chat", "name": "标准对话(极速)" },
        { "id": "deepseek-chat-search", "name": "联网对话(搜索)" },
        { "id": "deepseek-reasoner", "name": "深度思考(R1)" },
        { "id": "deepseek-reasoner-search", "name": "R1联网(弱)" }
      ]
    },
    {
      "id": "gemini",
      "name": "Gemini",
      "icon": "🌐",
      "enabled": true,
      "port": 56667,
      "targetUrl": "https://gemini.google.com/",
      "models": [
        { "id": "gemini-2.5-pro", "name": "Gemini 2.5 Pro" },
        { "id": "gemini-2.5-flash", "name": "Gemini 2.5 Flash" }
      ],
      "proxySettings": {
        "enabled": true,
        "mode": "bypass_cn",
        "selectedNodeIndex": 0,
        "nodes": [
          { "name": "本地代理", "address": "127.0.0.1", "port": 10808, "type": "socks5" }
        ]
      }
    }
  ]
}
```

## 代理配置

Gemini、Grok 等国外平台内置代理支持：

1. 打开对应渠道面板
2. 点击「🛡 代理设置」
3. 从剪贴板批量粘贴代理节点（格式：`socks5://127.0.0.1:10808` 或 `127.0.0.1:10808`，多行空格/Tab/逗号分隔）
4. 点击「⚡ 一键测速」测试节点 TCP 连接延迟（2s 超时）
5. 双击节点或按 Enter 激活
6. 选择模式：🌏 绕过中国大陆 / 🌐 全局模式
7. 点击保存后需重启渠道

## 网络拦截策略详解

### Fetch 拦截

使用 `response.clone()` 策略，对原始响应做零侵入旁观式拦截：

```javascript
const clone = response.clone();           // 克隆响应
(async () => {
    const reader = clone.body.getReader(); // 从副本读取
    // ... 逐 chunk 通过 postMessage 发送到 C#
})();
return response;                           // 返回原始响应
```

### EventSource 拦截

双重拦截确保覆盖：

1. **addEventListener 包装**：拦截所有通过 `addEventListener` 注册的回调
2. **onmessage/onopen/onerror 属性拦截**：使用 `Object.defineProperty` 拦截属性赋值，自动转为 `addEventListener` 调用

### 诊断机制

每次发送 prompt 前执行诊断脚本，检测：
- Bridge 状态（`window.chrome.webview` 是否可用）
- 拦截器状态（`window.fetch.toString()` 中是否包含 NETWORK_DATA）
- 页面就绪状态（`document.readyState`）
- 当前页面 URL

如果拦截器未激活，自动重新注入网络拦截脚本。

## 状态栏健康检查

底部状态栏使用双重健康检查：
1. **HTTP 健康检查**：每 10 秒 GET `/health` 端点
2. **页面健康检查**：页面加载后检测 `document.title` 是否包含错误信息（ERR_、无法访问、proxy、timeout 等）

两项都通过才显示绿灯 🟢，否则红灯 🔴。

## 已知问题与限制

- Gemini、Grok 需要代理才能访问
- DeepSeek 首次请求需要解决 PoW 挑战（CPU 暴力求解，通常 < 1 秒）
- 长时间运行 WebView2 内存会增长，建议定期重启
- 同时只处理一个请求（排队机制，最多等待 120 秒）

## 许可证

MIT License