using System.Net;
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

        // ── LAN Discovery ──────────────────────────────────────────────────────
        public static async Task<List<ServerAnnounceData>> DiscoverServersAsync(
            int discoveryPort = 443, int timeoutMs = 2000)
        {
            var results = new List<ServerAnnounceData>();
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = timeoutMs;

                var msg = Encoding.UTF8.GetBytes("MSN_DISCOVER");
                await udp.SendAsync(msg, msg.Length, "255.255.255.255", discoveryPort);

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        udp.Client.ReceiveTimeout = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                        var result = await udp.ReceiveAsync();
                        var json = Encoding.UTF8.GetString(result.Buffer);
                        var info = JsonSerializer.Deserialize<ServerAnnounceData>(json);
                        if (info != null)
                        {
                            info.Host = result.RemoteEndPoint.Address.ToString();
                            if (!results.Any(r => r.Host == info.Host && r.Port == info.Port))
                                results.Add(info);
                        }
                    }
                    catch { break; }
                }
            }
            catch { }
            return results;
        }
    }
}
