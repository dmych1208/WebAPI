using System.IO;
using System.Text.Json;
using WebAPI.Models;

namespace WebAPI.Common
{
    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config", "settings.json");

        private static FileSystemWatcher? _watcher;
        private static bool _watcherInitialized = false;
        private static readonly object _watcherLock = new object();

        public static event Action<List<ChannelConfig>>? ConfigReloaded;

        public static void StartWatching()
        {
            lock (_watcherLock)
            {
                if (_watcherInitialized)
                    return;

                _watcherInitialized = true;

                string? configDir = Path.GetDirectoryName(ConfigPath);
                if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir))
                    return;

                try
                {
                    _watcher = new FileSystemWatcher(configDir, "settings.json")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };

                    _watcher.Changed += OnConfigChanged;
                    _watcher.Created += OnConfigChanged;

                    LogManager.Write($"已启动配置文件监控: {ConfigPath}", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    LogManager.Write($"启动配置文件监控失败: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        private static void OnConfigChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                Thread.Sleep(200);

                LogManager.Write("检测到配置文件变更，正在重新加载...", LogLevel.Info);

                var channels = LoadChannels();
                if (channels.Count == 0)
                {
                    LogManager.Write("配置文件变更但内容为空，使用默认配置", LogLevel.Warn);
                    channels = GetDefaultChannels();
                }

                ConfigReloaded?.Invoke(channels);
                LogManager.Write($"配置已重新加载，共 {channels.Count} 个渠道", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Write($"重新加载配置失败: {ex.Message}", LogLevel.Error);
            }
        }

        public static List<ChannelConfig> LoadChannels()
        {
            var paths = new[]
            {
                ConfigPath,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "config", "settings.json"),
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
                        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var config = JsonSerializer.Deserialize<RootConfig>(json, jsonOptions);
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

        public static void SaveChannels(List<ChannelConfig> channels)
        {
            try
            {
                var root = new RootConfig { Channels = channels };
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
                var json = JsonSerializer.Serialize(root, jsonOptions);

                string configDir = Path.GetDirectoryName(ConfigPath)!;
                Directory.CreateDirectory(configDir);

                lock (_watcherLock)
                {
                    if (_watcher != null)
                        _watcher.EnableRaisingEvents = false;

                    File.WriteAllText(ConfigPath, json);
                }

                LogManager.Write($"配置已保存到 {ConfigPath}，共 {channels.Count} 个渠道", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogManager.Write($"保存配置失败: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                lock (_watcherLock)
                {
                    if (_watcher != null)
                        _watcher.EnableRaisingEvents = true;
                }
            }
        }
    }
}