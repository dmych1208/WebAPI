# WebAPI - AI 网页工具转本地 API 代理服务

将 DeepSeek、通义千问、豆包等网页版 AI 工具转换为本地 OpenAI 兼容 API 接口。

## 特性

- **多渠道支持**: DeepSeek、通义千问、豆包
- **OpenAI 兼容**: 完全兼容 OpenAI API 格式，可直接用于各种客户端
- **流式响应**: 支持 SSE 流式输出
- **WebView2 内核**: 基于微软 WebView2 的真实浏览器环境
- **独立端口**: 每个渠道独立端口，互不干扰
- **实时日志**: 内置日志窗口，实时查看请求状态
- **状态监控**: 底部状态栏实时显示各渠道服务状态（绿灯可用/红灯不可用）
- **模型测试**: 每个模型旁的测试按钮可快速验证连通性

## 快速开始

### 系统要求

- Windows 10/11
- .NET 9.0 SDK（开发） / Runtime（运行）
- Microsoft Edge WebView2 Runtime（通常已预装）

### 从源码构建

```bash
# 克隆仓库
git clone https://github.com/yourname/WebAPI.git
cd WebAPI

# 还原依赖并构建
dotnet restore
dotnet build -c Release

# 运行
dotnet run --project src/WebAPI/WebAPI.csproj -c Release
```

### 直接使用

1. 下载最新 Release 版本
2. 解压到任意目录
3. 运行 `WebAPI.exe`

### 配置

各渠道默认配置：

| 渠道 | Base URL | 默认端口 |
|------|----------|----------|
| DeepSeek | `http://127.0.0.1:55555` | 55555 |
| 通义千问 | `http://127.0.0.1:56666` | 56666 |
| 豆包 | `http://127.0.0.1:55556` | 55556 |

## 使用示例

### cURL

```bash
# 非流式请求
curl -X POST http://127.0.0.1:55555/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sk-any" \
  -d '{"model":"deepseek-chat","messages":[{"role":"user","content":"hi"}],"stream":false}'

# 流式请求
curl -X POST http://127.0.0.1:55555/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sk-any" \
  -d '{"model":"deepseek-chat","messages":[{"role":"user","content":"hi"}],"stream":true}'
```

### Python (OpenAI SDK)

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:55555/v1", api_key="sk-any")
response = client.chat.completions.create(
    model="deepseek-chat",
    messages=[{"role": "user", "content": "hi"}],
    stream=True
)
for chunk in response:
    if chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="")
```

### 配置文件

编辑 `config/settings.json` 可以自定义端口和模型列表。

## 可用模型

### DeepSeek
- `deepseek-chat` - 标准对话(极速)
- `deepseek-chat-search` - 联网对话(搜索)
- `deepseek-reasoner` - 深度思考(R1)
- `deepseek-reasoner-search` - R1联网(弱)

### 通义千问
- `qwen-turbo` - 千问加速
- `qwen-plus` - 千问增强
- `qwen-max` - 千问旗舰
- `qwen-max-long` - 千问长文本

### 豆包
- `doubao-pro` - 豆包Pro
- `doubao-lite` - 豆包Lite
- `doubao-role` - 角色扮演

## API 接口

| 接口 | 方法 | 说明 |
|------|------|------|
| `/health` | GET | 健康检查 |
| `/v1/models` | GET | 可用模型列表 |
| `/v1/chat/completions` | POST | OpenAI 兼容对话接口 |
| `/shutdown` | GET | 优雅关闭服务 |

## 项目结构

```
WebAPI/
├── src/WebAPI/
│   ├── Adapters/                  # 平台适配器
│   │   ├── IAdapter.cs            # 适配器接口
│   │   ├── DeepSeekAdapter.cs     # DeepSeek 适配
│   │   ├── QwenAdapter.cs         # 通义千问 适配
│   │   └── DoubaoAdapter.cs       # 豆包 适配
│   ├── Common/                    # 公共模块
│   │   ├── ChannelHttpServer.cs   # HTTP 服务器
│   │   ├── ConfigManager.cs       # 配置管理
│   │   ├── LogManager.cs          # 日志管理
│   │   ├── RequestParser.cs       # 请求解析
│   │   ├── ResponseConverter.cs   # 响应转换
│   │   ├── SseParser.cs           # SSE 流解析
│   │   └── WindowStateManager.cs  # 窗口状态持久化
│   ├── Controls/
│   │   ├── ChannelPanel.xaml      # 渠道面板 UI
│   │   └── ChannelPanel.xaml.cs   # 渠道面板逻辑
│   ├── Models/
│   │   └── Models.cs              # 数据模型
│   ├── App.xaml / App.xaml.cs     # 应用入口
│   ├── MainWindow.xaml / .cs      # 主窗口
│   └── WebAPI.csproj              # 项目配置
├── config/
│   └── settings.json              # 运行时配置
├── test/                          # 测试脚本
├── IconInjector/                  # 图标注入工具
└── WebAPI.sln                     # 解决方案文件
```

## 二次开发指南

### 添加新渠道

1. 在 `Adapters/` 目录下创建新的适配器类，实现 `IAdapter` 接口：

```csharp
public class NewAdapter : IAdapter
{
    public string PlatformId => "newplatform";
    public string PlatformName => "新平台";
    public string TargetUrl => "https://newplatform.com/chat";
    public int DefaultPort => 55557;
    public string UserDataFolder => Path.Combine(..., "NewPlatform");
    public List<ModelInfo> AvailableModels => new() { ... };

    public string GetNetworkInterceptorScript() { ... }
    public string GetDomControlScript(string prompt) { ... }
    public async Task InjectPromptAsync(WebView2 webView, string prompt) { ... }
    public SseParser.SseEvent? ParseSseData(string rawLine) { ... }
}
```

2. 在 `ChannelPanel.xaml.cs` 的 `CreateAdapter()` 方法中注册新适配器
3. 在 `ConfigManager.cs` 的 `GetDefaultChannels()` 中添加默认配置

### 适配器核心方法说明

| 方法 | 作用 |
|------|------|
| `GetNetworkInterceptorScript()` | 返回 JS 脚本，拦截 WebView2 中的网络请求，将响应数据通过 `postMessage` 发送给 C# 端 |
| `GetDomControlScript(prompt)` | 返回 JS 脚本，在聊天页面中找到输入框、填入文本、点击发送按钮 |
| `InjectPromptAsync(webView, prompt)` | 执行 DOM 控制脚本，触发消息发送 |
| `ParseSseData(rawLine)` | 解析平台特有的 SSE 数据格式 |

### 网络拦截脚本工作原理

1. 重写 `window.fetch` 和 `XMLHttpRequest`，拦截匹配 URL 的请求
2. 对于 SSE 流式响应，使用 `ReadableStream` 逐 chunk 读取，通过 `postMessage` 发送 `NETWORK_DATA` 消息
3. 流结束时发送 `NETWORK_DONE` 消息
4. C# 端通过 `CoreWebView2.WebMessageReceived` 事件接收消息

### DOM 控制脚本注意事项

- 使用 `nativeInputValueSetter` 绕过 React/Vue 的受控组件限制
- 输入框选择器应排除搜索框、侧边栏等非聊天元素
- 发送按钮优先在输入框的父容器中查找，避免匹配到其他区域的按钮
- 如果找不到发送按钮，回退到模拟 Enter 键发送

### 技术栈

- .NET 9.0 WPF
- Microsoft.Web.WebView2
- Hardcodet.NotifyIcon.Wpf（系统托盘）
- System.Text.Json
- HttpListener（HTTP 服务器）

## 常见问题

### Q: 为什么需要登录？
A: 本工具通过 WebView2 嵌入真实浏览器环境，需要你在对应 AI 平台保持登录状态。首次使用时请在 WebView2 中登录各平台账号。

### Q: 响应速度慢？
A: 首次请求需要等待页面加载和 WebView2 初始化（约 1-2 分钟），后续请求会快很多。

### Q: 出现"找不到输入框"错误？
A: 目标网页的 DOM 结构可能已更新。请尝试点击"重启浏览器内核"按钮，或更新适配器中的选择器。

### Q: 支持 macOS/Linux 吗？
A: 当前仅支持 Windows（依赖 WebView2 和 WPF）。macOS/Linux 支持正在规划中。

## 许可证

MIT License

## 贡献

欢迎提交 Issue 和 Pull Request！

### 开发环境搭建

1. 安装 Visual Studio 2022 或更高版本（含 .NET 桌面开发工作负载）
2. 安装 .NET 9.0 SDK
3. 克隆仓库并打开 `WebAPI.sln`
4. 还原 NuGet 包
5. 按 F5 启动调试

### 编码规范

- 遵循 C# 命名约定
- 不添加多余注释（代码即文档）
- 适配器脚本使用 IIFE 包裹，避免全局污染
- 异步操作使用 `async/await`，避免阻塞 UI 线程
