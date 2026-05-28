# Changelog

## v1.0.0 (2026-05-28)

### 新增

- **DeepSeek 渠道**
  - 支持 deepseek-chat 标准对话
  - 支持 deepseek-reasoner 深度思考模式
  - 支持 deepseek-chat-search 联网搜索
  - PoW 挑战自动解决
  - 流式/非流式响应

- **通义千问渠道**
  - 支持 qwen-turbo / qwen-plus / qwen-max / qwen-max-long
  - 深度思考/智能搜索按钮联动
  - 流式/非流式响应

- **豆包渠道**
  - 支持 doubao-pro / doubao-lite / doubao-role
  - 流式/非流式响应

- **核心功能**
  - OpenAI 兼容 API 格式
  - SSE 流式响应
  - 多标签页管理
  - 实时日志窗口
  - 服务状态指示
  - 窗口状态记忆
  - 应用图标注入

### 技术栈

- .NET 9.0 WPF
- WebView2
- HttpListener
- System.Text.Json

### 已知限制

- 仅支持 Windows 平台
- 需要目标平台保持登录状态
- 首次请求可能较慢（页面加载）
