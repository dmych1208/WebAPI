# WebAPI - AI 网页工具转本地 API 代理服务

将 DeepSeek、通义千问、豆包等网页版 AI 工具转换为本地 OpenAI 兼容 API 接口。

## 特性

- **多渠支持**: DeepSeek、通义千问、豆包
- **OpenAI 兼容**: 完全兼容 OpenAI API 格式，可直接用于各种客户端
- **流式响应**: 支持 SSE 流式输出
- **WebView2 内核**: 基于微软 WebView2 的真实浏览器环境
- **独立端口**: 每个渠道独立端口，互不干扰
- **实时日志**: 内置日志窗口，实时查看请求状态

## 快速开始

### 系统要求

- Windows 10/11
- .NET 9.0 Runtime (或使用 self-contained 版本)
- Microsoft Edge WebView2 Runtime (通常已预装)

### 安装

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
  -d '{"model":"deepseek-chat","messages":[{"role":"user","content":"你好"}],"stream":false}'

# 流式请求
curl -X POST http://127.0.0.1:55555/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sk-any" \
  -d '{"model":"deepseek-chat","messages":[{"role":"user","content":"你好"}],"stream":true}'
```

### Python (OpenAI SDK)

```python
from openai import OpenAI

# DeepSeek
client = OpenAI(base_url="http://127.0.0.1:55555/v1", api_key="sk-any")
response = client.chat.completions.create(
    model="deepseek-chat",
    messages=[{"role": "user", "content": "你好"}],
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

## 功能

- **健康检查**: `GET /health` 返回服务状态
- **模型列表**: `GET /v1/models` 返回可用模型列表
- **对话接口**: `POST /v1/chat/completions` OpenAI 兼容对话接口
- **关闭服务**: `GET /shutdown` 优雅关闭服务

## 常见问题

### Q: 为什么需要登录？
A: 本工具通过 WebView2 嵌入真实浏览器环境，需要你在对应 AI 平台保持登录状态。

### Q: 响应速度慢？
A: 首次请求可能需要等待页面加载，后续请求会快很多。可以使用"重启浏览器内核"按钮重置状态。

### Q: 出现"找不到输入框"错误？
A: 目标网页的 DOM 结构可能已更新。请尝试点击"重启浏览器内核"按钮。

### Q: 支持 macOS/Linux 吗？
A: 当前仅支持 Windows。macOS/Linux 支持正在规划中。

## 技术栈

- .NET 9.0 WPF
- WebView2
- HttpListener
- OpenAI 兼容 API 格式

## 许可证

MIT License

## 开发计划

| 阶段 | 内容 | 状态 |
|------|------|------|
| Phase 0 | 项目初始化与基础框架 | ✅ 完成 |
| Phase 1 | DeepSeek 渠道 | ✅ 完成 |
| Phase 2 | 通义千问渠道 | ✅ 完成 |
| Phase 3 | 豆包渠道 | ✅ 完成 |
| Phase 4 | UI打磨与功能完善 | ⏳ 进行中 |
| Phase 5 | 测试、文档与发布 | ⏳ 进行中 |

## 贡献

欢迎提交 Issue 和 Pull Request！
