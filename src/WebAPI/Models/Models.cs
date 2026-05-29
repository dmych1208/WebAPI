namespace WebAPI.Models
{
    /// <summary>渠道配置</summary>
    public class ChannelConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Port { get; set; } = 55555;
        public string TargetUrl { get; set; } = "";
        public string Icon { get; set; } = "💬";
        public string UserDataFolder { get; set; } = "";
        public ProxySettings? ProxySettings { get; set; }
        public List<ModelInfo> Models { get; set; } = new();
        /// <summary>图片缓存上限(MB)，仅 Gemini 有效</summary>
        public int ImageCacheMaxMB { get; set; } = 512;
        /// <summary>图片保存目录，仅 Gemini 有效</summary>
        public string ImageSaveDirectory { get; set; } = @"C:\Users\Administrator\Downloads\GeminiWebRelay";
    }

    /// <summary>模型信息</summary>
    public class ModelInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
    }

    /// <summary>OpenAI 格式请求体</summary>
    public class ChatRequest
    {
        public string Model { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = new();
        public bool Stream { get; set; } = true;
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
