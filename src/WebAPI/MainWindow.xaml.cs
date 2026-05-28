using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using WebAPI.Common;
using WebAPI.Models;
using WebAPI.Controls;
using Path = System.IO.Path;

namespace WebAPI
{
    public partial class MainWindow : Window
    {
        private List<ChannelConfig> _channels = new();
        private Dictionary<string, ChannelPanel> _channelPanels = new();
        private int _totalRequests = 0;
        private DateTime _startTime;

        public MainWindow()
        {
            InitializeComponent();
            _startTime = DateTime.Now;
            Loaded += MainWindow_Loaded;
            SetApplicationIcon();
        }

        // 不允许最小化到托盘（托盘功能待实现）
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // 不处理最小化逻辑，保留窗口状态
        }
        
        private void SetApplicationIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.png");
                if (File.Exists(iconPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    this.Icon = bitmap;
                }
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 先恢复窗口状态，再加载渠道（避免闪烁）
            RestoreWindowState();

            _channels = ConfigManager.LoadChannels();

            if (_channels.Count == 0)
            {
                LogManager.Write("未找到渠道配置，使用默认配置", LogLevel.Warn);
                _channels = ConfigManager.GetDefaultChannels();
            }

            foreach (var ch in _channels)
            {
                CreateChannelTab(ch);
            }

            CreateDevChannelTab();

            // 恢复选中的标签页
            RestoreSelectedTab();

            UpdateStatusBar();
            StartUptimeTimer();
            LogManager.Write($"WebAPI 启动完成，共 {_channels.Count} 个渠道");
        }

        private void RestoreWindowState()
        {
            try
            {
                var state = WindowStateManager.Load();
                if (state != null)
                {
                    // 先设窗口状态，再设位置大小（顺序很重要）
                    if (state.IsMaximized)
                    {
                        WindowState = WindowState.Maximized;
                    }
                    else
                    {
                        // 只有非最大化状态才恢复位置大小
                        if (state.Left >= 0 && state.Top >= 0 && state.Width > 0 && state.Height > 0)
                        {
                            Left = state.Left;
                            Top = state.Top;
                            Width = state.Width;
                            Height = state.Height;
                        }
                        WindowState = WindowState.Normal;
                    }
                    LogManager.Write("已恢复窗口状态", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                LogManager.Write($"恢复窗口状态失败: {ex.Message}", LogLevel.Warn);
            }
        }

        private void RestoreSelectedTab()
        {
            try
            {
                var state = WindowStateManager.Load();
                if (state != null && state.SelectedTabIndex >= 0 && state.SelectedTabIndex < ChannelTabs.Items.Count)
                {
                    ChannelTabs.SelectedIndex = state.SelectedTabIndex;
                }
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowState();
            base.OnClosing(e);
        }

        private void SaveWindowState()
        {
            try
            {
                var state = new WindowStateData
                {
                    Left = Left,
                    Top = Top,
                    Width = Width,
                    Height = Height,
                    IsMaximized = WindowState == WindowState.Maximized,
                    SelectedTabIndex = ChannelTabs.SelectedIndex
                };
                WindowStateManager.Save(state);
                LogManager.Write("已保存窗口状态", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                LogManager.Write($"保存窗口状态失败: {ex.Message}", LogLevel.Warn);
            }
        }

        private void CreateChannelTab(ChannelConfig config)
        {
            var tabItem = new TabItem
            {
                Header = CreateTabHeader(config),
                Tag = config.Id,
                Background = Application.Current.FindResource("BgPanel") as Brush,
                BorderThickness = new Thickness(0)
            };

            var panel = new ChannelPanel(config);
            panel.OnLogMessage += (msg, level) => LogManager.Write($"[{config.Name}] {msg}", level);
            panel.OnRequestReceived += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    _totalRequests++;
                    StatusRequests.Text = $"请求: {_totalRequests}";
                });
            };

            tabItem.Content = panel;
            ChannelTabs.Items.Add(tabItem);

            _channelPanels[config.Id] = panel;
        }

        private void CreateDevChannelTab()
        {
            var tabItem = new TabItem
            {
                Header = "🔄 其他渠道开发中",
                IsEnabled = false,
                Background = Application.Current.FindResource("BgPanel") as Brush,
                BorderThickness = new Thickness(0)
            };

            var placeholderPanel = new Grid();
            var textBlock = new TextBlock
            {
                Text = "更多渠道正在开发中，敬请期待...",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.FindResource("TextSecondary") as Brush,
                FontSize = 14
            };
            placeholderPanel.Children.Add(textBlock);
            tabItem.Content = placeholderPanel;

            ChannelTabs.Items.Add(tabItem);
        }

        private object CreateTabHeader(ChannelConfig config)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            var statusDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = (Brush)Application.Current.FindResource("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new TextBlock
            {
                Text = $"{config.Icon} {config.Name}",
                VerticalAlignment = VerticalAlignment.Center
            };

            sp.Children.Add(statusDot);
            sp.Children.Add(text);
            return sp;
        }

        private void ChannelTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChannelTabs.SelectedItem is TabItem tab && tab.Tag is string channelId)
            {
                UpdateStatusBar();
            }
        }

        private void UpdateStatusBar()
        {
            if (ChannelTabs.SelectedItem is TabItem tab && tab.Content is ChannelPanel panel)
            {
                StatusText.Text = panel.StatusText;
                StatusPorts.Text = $"端口: {panel.Port}";
            }
        }

        private void StartUptimeTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                var elapsed = DateTime.Now - _startTime;
                StatusUptime.Text = $"运行: {(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            };
            timer.Start();
        }
    }
}