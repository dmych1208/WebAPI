using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WebAPI.Models;

namespace WebAPI.Controls
{
    public partial class ProxyConfigWindow : Window
    {
        public ProxySettings Settings { get; private set; }
        public ObservableCollection<ProxyNode> DisplayNodes { get; } = new();

        public ProxyConfigWindow(ProxySettings settings)
        {
            InitializeComponent();
            Settings = settings;
            LoadSettings();
        }

        private void LoadSettings()
        {
            ToggleEnabled.IsChecked = Settings.Enabled;
            UpdateToggleText();
            foreach (var node in Settings.Nodes)
                DisplayNodes.Add(node);
            NodeListView.ItemsSource = DisplayNodes;
            if (DisplayNodes.Count > 0)
                SelectNode(Settings.SelectedNodeIndex < DisplayNodes.Count ? Settings.SelectedNodeIndex : 0);
            foreach (ComboBoxItem item in CmbMode.Items)
            {
                if (item.Tag?.ToString() == Settings.Mode)
                {
                    CmbMode.SelectedItem = item;
                    break;
                }
            }
        }

        private void UpdateToggleText()
        {
            TxtProxyStatus.Text = ToggleEnabled.IsChecked == true ? "已启用" : "已关闭";
        }

        private void SelectNode(int index)
        {
            if (index >= 0 && index < DisplayNodes.Count)
            {
                NodeListView.SelectedIndex = index;
                NodeListView.ScrollIntoView(NodeListView.SelectedItem);
            }
        }

        private ProxyNode? GetSelectedNode()
        {
            return NodeListView.SelectedItem as ProxyNode;
        }

        private void ToggleEnabled_Changed(object sender, RoutedEventArgs e) => UpdateToggleText();

        private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Settings.Mode = (CmbMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "bypass_cn";
        }

        private async void BtnPasteFromClipboard_Click(object sender, RoutedEventArgs e)
        {
            string clip = "";
            try 
            {
                var tcs = new TaskCompletionSource<string>();
                var thread = new Thread(() =>
                {
                    try { tcs.SetResult(Clipboard.GetText()); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                clip = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取剪贴板失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(clip))
            {
                MessageBox.Show("剪贴板为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var lines = clip.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int added = 0;
            foreach (var line in lines)
            {
                var node = ParseProxyLine(line.Trim());
                if (node != null)
                {
                    if (!DisplayNodes.Any(n => n.Address == node.Address && n.Port == node.Port))
                    {
                        DisplayNodes.Add(node);
                        added++;
                    }
                }
            }

            if (added > 0)
            {
                if (DisplayNodes.Count == added) SelectNode(0);
            }
            else
            {
                MessageBox.Show("未识别到有效代理节点\n支持格式:\n- socks5://127.0.0.1:10808\n- 127.0.0.1:10808\n- ss:// / vmess:// / trojan:// / vless:// 格式", 
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private ProxyNode? ParseProxyLine(string line)
        {
            try
            {
                // 简单协议: socks5://, socks4://, http://, https://
                if (line.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                    return ParseAddress(line[9..], "socks5");
                if (line.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
                    return ParseAddress(line[9..], "socks4");
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    return ParseAddress(line[7..], "http");
                if (line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return ParseAddress(line[8..], "https");
                
                // Shadowsocks/V2Ray/Trojan/VLESS (base64 或简单格式)
                if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) || 
                    line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) || 
                    line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) || 
                    line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("wless://", StringComparison.OrdinalIgnoreCase))
                {
                    // 提取 @ 后面的地址端口
                    int atIdx = line.IndexOf('@');
                    if (atIdx > 0)
                    {
                        string afterAt = line.Substring(atIdx + 1);
                        // 查找下一个 / 或 ? 或 #
                        int slashIdx = afterAt.IndexOfAny(new[] { '/', '?', '#' });
                        if (slashIdx > 0)
                            afterAt = afterAt.Substring(0, slashIdx);
                        
                        // 查找端口号 (格式 addr:port)
                        int colonIdx = afterAt.LastIndexOf(':');
                        if (colonIdx > 0 && int.TryParse(afterAt.Substring(colonIdx + 1), out int port) && port > 0 && port < 65536)
                        {
                            string addr = afterAt.Substring(0, colonIdx);
                            string proto = line.Split(':')[0].ToLowerInvariant();
                            return new ProxyNode { Name = $"{addr}:{port}", Address = addr, Port = port, Type = proto };
                        }
                    }
                }

                // 简单 host:port 格式
                if (line.Contains(":"))
                {
                    var parts = line.Split(':');
                    if (parts.Length >= 2)
                    {
                        string addr = parts[0].Trim();
                        if (int.TryParse(parts[1].Trim(), out int port) && port > 0 && port < 65536)
                            return new ProxyNode { Name = $"{addr}:{port}", Address = addr, Port = port, Type = "socks5" };
                    }
                }
            }
            catch { }
            return null;
        }

        private ProxyNode? ParseAddress(string hostport, string type)
        {
            var parts = hostport.Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int port))
                return new ProxyNode { Name = $"{parts[0]}:{port}", Address = parts[0].Trim(), Port = port, Type = type };
            return null;
        }

        private async void BtnSpeedTestAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var node in DisplayNodes)
            {
                await TestNodeLatency(node);
            }
            NodeListView.Items.Refresh();
        }

        private async void BtnNodeSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProxyNode node)
            {
                await TestNodeLatency(node);
                NodeListView.Items.Refresh();
            }
        }

        private async Task TestNodeLatency(ProxyNode node)
        {
            node.Latency = -2;
            NodeListView.Items.Refresh();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(node.Address, node.Port, cts.Token);
                sw.Stop();
                node.Latency = (int)sw.ElapsedMilliseconds;
            }
            catch
            {
                node.Latency = -3;
            }
        }

        private void NodeListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ActivateNode();
        }

        private void NodeListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ActivateNode();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteNode();
                e.Handled = true;
            }
        }

        private void BtnDeleteNode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ProxyNode node)
            {
                DisplayNodes.Remove(node);
                if (DisplayNodes.Count > 0) SelectNode(0);
            }
        }

        private void ActivateNode()
        {
            var node = GetSelectedNode();
            if (node != null)
            {
                Settings.SelectedNodeIndex = NodeListView.SelectedIndex;
                Settings.Enabled = true;
                ToggleEnabled.IsChecked = true;
                UpdateToggleText();
            }
        }

        private void DeleteNode()
        {
            var node = GetSelectedNode();
            if (node != null)
            {
                int idx = NodeListView.SelectedIndex;
                DisplayNodes.Remove(node);
                if (DisplayNodes.Count > 0) SelectNode(Math.Min(idx, DisplayNodes.Count - 1));
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            Settings.Nodes = DisplayNodes.ToList();
            Settings.Enabled = ToggleEnabled.IsChecked == true;
            Settings.SelectedNodeIndex = NodeListView.SelectedIndex >= 0 ? NodeListView.SelectedIndex : 0;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}