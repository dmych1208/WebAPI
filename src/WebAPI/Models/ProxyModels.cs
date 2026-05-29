namespace WebAPI.Models
{
    public class ProxyNode
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 10808;
        public string Type { get; set; } = "socks5";
        public int Latency { get; set; } = -1;
    }

    public class ProxySettings
    {
        public bool Enabled { get; set; }
        public List<ProxyNode> Nodes { get; set; } = new();
        public int SelectedNodeIndex { get; set; }
        public string Mode { get; set; } = "bypass_cn";
    }
}