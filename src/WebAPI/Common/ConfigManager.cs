using System.IO;
using System.Text.Json;
using WebAPI.Models;

namespace WebAPI.Common
{
    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config", "settings.json");

        public static List<ChannelConfig> LoadChannels()
        {
            var paths = new[]
            {
                ConfigPath,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "config", "settings.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json")
            };

            foreach (var path in paths.Select(p => Path.GetFullPath(p)))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var config = JsonSerializer.Deserialize<RootConfig>(json);
                        if (config?.Channels != null && config.Channels.Count > 0)
                            return config.Channels.Where(c => c.Enabled).ToList();
                    }
                    catch (Exception ex)
                    {
                        LogManager.Write($"读取配置文件失败 ({path}): {ex.Message}", LogLevel.Warn);
                    }
                }
            }

            return new List<ChannelConfig>();
        }

        public static List<ChannelConfig> GetDefaultChannels()
        {
            return new List<ChannelConfig>
            {
                new()
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    Enabled = true,
                    Port = 55555,
                    TargetUrl = "https://chat.deepseek.com/",
                    Icon = "💬",
                    UserDataFolder = "DeepSeek_Data",
                    Models = new List<ModelInfo>
                    {
                        new() { Id = "deepseek-chat", Name = "标准对话(极速)" },
                        new() { Id = "deepseek-chat-search", Name = "联网对话(搜索)" },
                        new() { Id = "deepseek-reasoner", Name = "深度思考(R1)" },
                        new() { Id = "deepseek-reasoner-search", Name = "R1联网(弱)" }
                    }
                },
                new()
                {
                    Id = "qwen",
                    Name = "通义千问",
                    Enabled = true,
                    Port = 56666,
                    TargetUrl = "https://qianwen.aliyun.com/",
                    Icon = "🔮",
                    UserDataFolder = "Qwen_Data",
                    Models = new List<ModelInfo>
                    {
                        new() { Id = "qwen-turbo", Name = "千问加速" },
                        new() { Id = "qwen-plus", Name = "千问增强" },
                        new() { Id = "qwen-max", Name = "千问旗舰" }
                    }
                },
                new()
                {
                    Id = "doubao",
                    Name = "豆包",
                    Enabled = true,
                    Port = 55556,
                    TargetUrl = "https://www.doubao.com/",
                    Icon = "🫘",
                    UserDataFolder = "Doubao_Data",
                    Models = new List<ModelInfo>
                    {
                        new() { Id = "doubao-pro", Name = "豆包Pro" },
                        new() { Id = "doubao-lite", Name = "豆包Lite" }
                    }
                }
            };
        }

        private class RootConfig
        {
            public List<ChannelConfig>? Channels { get; set; }
        }
    }
}