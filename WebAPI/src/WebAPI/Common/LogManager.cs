using System.IO;

namespace WebAPI.Common
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static class LogManager
    {
        private static readonly object _lock = new();
        private static string? _logDir;
        private static bool _initialized = false;

        public static void Init()
        {
            if (_initialized) return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            var projectLogDir = Path.Combine(baseDir, "..", "..", "..", "..", "log");
            if (Directory.Exists(projectLogDir))
                _logDir = Path.GetFullPath(projectLogDir);
            else
                _logDir = Path.Combine(baseDir, "log");

            Directory.CreateDirectory(_logDir);
            _initialized = true;
        }

        private static string CurrentLogFile => Path.Combine(_logDir ?? "log", $"{DateTime.Now:yyyy-MM-dd}.log");

        public static void Write(string message, LogLevel level = LogLevel.Info)
        {
            string levelStr = level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Info => "INFO ",
                LogLevel.Warn => "WARN ",
                LogLevel.Error => "ERROR",
                _ => "INFO "
            };

            string line = $"[{DateTime.Now:HH:mm:ss}] [{levelStr}] {message}";

            ConsoleColor color = level switch
            {
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => ConsoleColor.White
            };

            lock (_lock)
            {
                var prevColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = color;
                    Console.WriteLine(line);
                }
                finally
                {
                    Console.ForegroundColor = prevColor;
                }

                try
                {
                    File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ERROR] 写入日志失败: {ex.Message}");
                    Console.ForegroundColor = prevColor;
                }
            }
        }

        public static void CleanupOldLogs(int keepDays = 30)
        {
            if (_logDir == null || !Directory.Exists(_logDir)) return;

            lock (_lock)
            {
                try
                {
                    var cutoff = DateTime.Now.AddDays(-keepDays);
                    var files = Directory.GetFiles(_logDir, "*.log");
                    int removed = 0;

                    foreach (var file in files)
                    {
                        var fi = new FileInfo(file);
                        if (fi.LastWriteTime < cutoff && fi.CreationTime < cutoff)
                        {
                            fi.Delete();
                            removed++;
                        }
                    }

                    if (removed > 0)
                        Console.WriteLine($"[LOG] 已清理 {removed} 个过期日志文件");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] 清理日志失败: {ex.Message}");
                }
            }
        }
    }
}