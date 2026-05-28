using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebAPI.Common
{
    public class WindowStateData
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsMaximized { get; set; }
        public int SelectedTabIndex { get; set; }
    }

    public static class WindowStateManager
    {
        private static readonly string StatePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config", "window_state.json");

        public static WindowStateData? Load()
        {
            try
            {
                if (!File.Exists(StatePath))
                    return null;

                var json = File.ReadAllText(StatePath);
                return JsonSerializer.Deserialize<WindowStateData>(json);
            }
            catch (Exception ex)
            {
                LogManager.Write($"加载窗口状态失败: {ex.Message}", LogLevel.Warn);
                return null;
            }
        }

        public static void Save(WindowStateData state)
        {
            try
            {
                var dir = Path.GetDirectoryName(StatePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(state, options);
                File.WriteAllText(StatePath, json);
            }
            catch (Exception ex)
            {
                LogManager.Write($"保存窗口状态失败: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
