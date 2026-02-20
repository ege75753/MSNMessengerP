using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MSNShared;

namespace MSNClient
{
    public class NetworkClient
    {
        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public bool IsConnected => _tcp?.Connected ?? false;
        public string? ConnectedHost { get; private set; }
        public int ConnectedPort { get; private set; }

        public event Action<Packet>? PacketReceived;
        public event Action? Disconnected;
        public event Action<string>? ConnectionError;

        public async Task<bool> ConnectAsync(string host, int port, CancellationToken ct = default)
        {
            try
            {
                Disconnect();
                _tcp = new TcpClient { NoDelay = true };
                await _tcp.ConnectAsync(host, port, ct);
                _stream = _tcp.GetStream();
                ConnectedHost = host;
                ConnectedPort = port;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                ConnectionError?.Invoke(ex.Message);
                return false;
            }
        }

        public async Task SendAsync(Packet packet)
        {
            if (_stream is null || !IsConnected) return;
            await _sendLock.WaitAsync();
            try
            {
                var data = Encoding.UTF8.GetBytes(packet.Serialize());
                await _stream.WriteAsync(data);
            }
            catch (Exception ex)
            {
                ConnectionError?.Invoke(ex.Message);
                Disconnect();
            }
            finally { _sendLock.Release(); }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buf = new byte[8192];
            var sb = new StringBuilder();

            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    int read;
                    try { read = await _stream.ReadAsync(buf, ct); }
                    catch { break; }
                    if (read == 0) break;

                    sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                    var data = sb.ToString();
                    int nl;
                    while ((nl = data.IndexOf('\n')) >= 0)
                    {
                        var line = data[..nl].Trim();
                        data = data[(nl + 1)..];
                        if (!string.IsNullOrEmpty(line))
                        {
                            var pkt = Packet.Deserialize(line);
                            if (pkt != null) PacketReceived?.Invoke(pkt);
                        }
                    }
                    sb.Clear(); sb.Append(data);
                }
            }
            catch { }
            finally
            {
                Disconnected?.Invoke();
            }
        }

        public void Disconnect()
        {
            _cts?.Cancel();
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _tcp = null; _stream = null;
        }

        // ── LAN Discovery (UDP broadcast + subnet scan) ─────────────────────────
        /// <summary>
        /// Discovers MSN servers on the local network.
        /// 1. Sends a UDP broadcast to 255.255.255.255:discoveryPort.
        /// 2. Unicasts to every /24 subnet host in parallel.
        /// 3. Collects responses for <paramref name="timeoutMs"/> ms.
        /// </summary>
        public static async Task<List<ServerAnnounceData>> DiscoverServersAsync(
            int discoveryPort = 443, int timeoutMs = 2500)
        {
            var results = new List<ServerAnnounceData>();
            var seen = new HashSet<string>();
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;

                var msg = Encoding.UTF8.GetBytes("MSN_DISCOVER");

                // 1. Broadcast
                try { await udp.SendAsync(msg, msg.Length, "255.255.255.255", discoveryPort); } catch { }

                // 2. Unicast to every host in each local /24 subnet.
                //    Use SYNCHRONOUS Send() so the socket stays open for the receive loop below.
                var localSubnets = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up
                             && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.GetAddressBytes())
                    .ToList();

                foreach (var prefix in localSubnets)
                    for (int h = 1; h <= 254; h++)
                    {
                        var target = $"{prefix[0]}.{prefix[1]}.{prefix[2]}.{h}";
                        try { udp.Send(msg, msg.Length, target, discoveryPort); } catch { }
                    }

                // 3. Collect responses within the timeout window
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        udp.Client.ReceiveTimeout = (int)Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds);
                        var result = await udp.ReceiveAsync();
                        var json = Encoding.UTF8.GetString(result.Buffer);
                        var info = JsonSerializer.Deserialize<ServerAnnounceData>(json);
                        if (info != null)
                        {
                            info.Host = result.RemoteEndPoint.Address.ToString();
                            var key = $"{info.Host}:{info.Port}";
                            if (seen.Add(key)) results.Add(info);
                        }
                    }
                    catch { break; }
                }
            }
            catch { }
            return results;
        }

        // ── TCP-based server query (works over ngrok / WAN) ─────────────────────
        /// <summary>
        /// Queries a server via TCP — works over ngrok because it uses TCP only.
        /// Sends ServerDiscovery, expects ServerAnnounce back. Returns null if unreachable.
        /// </summary>
        public static async Task<ServerAnnounceData?> QueryServerAsync(
            string host, int port, int timeoutMs = 3000)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                using var stream = tcp.GetStream();
                stream.WriteTimeout = timeoutMs;
                stream.ReadTimeout = timeoutMs;

                // Send ServerDiscovery packet
                var pkt = Packet.Create(PacketType.ServerDiscovery, new { });
                var data = Encoding.UTF8.GetBytes(pkt.Serialize());
                await stream.WriteAsync(data);

                // Read response (newline-delimited JSON)
                var buf = new byte[4096];
                var sb = new StringBuilder();
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    stream.ReadTimeout = (int)Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    int read = await stream.ReadAsync(buf);
                    if (read == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                    var text = sb.ToString();
                    int nl = text.IndexOf('\n');
                    if (nl >= 0)
                    {
                        var line = text[..nl].Trim();
                        var response = Packet.Deserialize(line);
                        if (response?.Type == PacketType.ServerAnnounce)
                        {
                            var info = response.GetData<ServerAnnounceData>();
                            if (info != null) { info.Host = host; info.Port = port; return info; }
                        }
                        break;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
