using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MSNShared;

namespace MSNClient
{
    /// <summary>
    /// Full server browser: LAN (UDP broadcast + /24 subnet scan) and WAN/ngrok (TCP query).
    /// Persists manually added servers to %AppData%\MSNMessenger\saved_servers.json.
    /// </summary>
    public partial class ServerBrowserWindow : Window
    {
        public ServerAnnounceData? SelectedServer { get; private set; }

        private const int DiscoveryPort = 443;
        private const int ScanTimeoutMs = 3500;

        private static readonly string SaveFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MSNMessenger", "saved_servers.json");

        private readonly Dictionary<string, ServerRow> _servers = new();
        private bool _scanning;

        public ServerBrowserWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSavedServers();
            _ = ScanLanAsync();
        }

        // ── LAN scan ────────────────────────────────────────────────────────────

        private void ScanLan_Click(object sender, RoutedEventArgs e) => _ = ScanLanAsync();

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _servers.Values.ToList())
                _ = PingAndUpdateRowAsync(row);
        }

        private async Task ScanLanAsync()
        {
            if (_scanning) return;
            _scanning = true;
            SetScanning(true);

            try
            {
                // Always probe localhost via TCP first — works even if UDP broadcast is blocked
                SubtitleText.Text = "Checking localhost…";
                var local = await NetworkClient.QueryServerAsync("127.0.0.1", DiscoveryPort, 1500);
                if (local != null)
                {
                    var lkey = $"127.0.0.1:{local.Port}";
                    if (!_servers.ContainsKey(lkey)) AddRow(local, source: "Local");
                    else UpdateRow(_servers[lkey], local);
                }

                // Also probe the machine's own LAN IP(s) via TCP
                foreach (var localIp in GetLocalIPs())
                {
                    var tcp = await NetworkClient.QueryServerAsync(localIp, DiscoveryPort, 800);
                    if (tcp != null)
                    {
                        var k = $"{tcp.Host}:{tcp.Port}";
                        if (!_servers.ContainsKey(k)) AddRow(tcp, source: "LAN");
                        else UpdateRow(_servers[k], tcp);
                    }
                }

                // UDP broadcast + subnet scan
                SubtitleText.Text = "Scanning LAN…";
                var found = await NetworkClient.DiscoverServersAsync(DiscoveryPort, ScanTimeoutMs);
                foreach (var info in found)
                {
                    var key = $"{info.Host}:{info.Port}";
                    if (!_servers.ContainsKey(key)) AddRow(info, source: "LAN");
                    else UpdateRow(_servers[key], info);
                }

                SubtitleText.Text = _servers.Count > 0
                    ? $"Found {_servers.Count} server(s)"
                    : "No servers found — add a WAN/ngrok address below";
                StatusText.Text = $"Scan complete";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Scan error: {ex.Message}";
            }
            finally
            {
                _scanning = false;
                SetScanning(false);
                RefreshEmptyState();
            }
        }

        private static IEnumerable<string> GetLocalIPs()
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                         && i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString());
        }

        // ── Manual WAN / ngrok entry ─────────────────────────────────────────────

        private async void TestManual_Click(object sender, RoutedEventArgs e)
        {
            var (host, port) = ParseHostPort(ManualHostBox.Text);
            StatusText.Text = $"Querying {host}:{port} via TCP…";

            // TCP query — works over ngrok
            var info = await NetworkClient.QueryServerAsync(host, port, 4000);
            if (info != null)
                StatusText.Text = $"✅ {host}:{port} → \"{info.ServerName}\" ({info.UserCount} users online)";
            else
                StatusText.Text = $"❌ No MSN server at {host}:{port}";
        }

        private async void AddManual_Click(object sender, RoutedEventArgs e)
        {
            var (host, port) = ParseHostPort(ManualHostBox.Text);
            StatusText.Text = $"Querying {host}:{port}…";

            var info = await NetworkClient.QueryServerAsync(host, port, 4000);
            info ??= new ServerAnnounceData { Host = host, Port = port, ServerName = host, UserCount = -1 };

            var key = $"{host}:{port}";
            if (_servers.ContainsKey(key))
                UpdateRow(_servers[key], info);
            else
                AddRow(info, source: "WAN");

            StatusText.Text = $"Added / updated: {key}";
            RefreshEmptyState();
            SaveServers();
        }

        private static (string host, int port) ParseHostPort(string raw)
        {
            raw = raw.Trim();
            // Handle tcp://host:port (ngrok format)
            if (raw.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
                raw = raw[6..];
            var idx = raw.LastIndexOf(':');
            if (idx > 0 && int.TryParse(raw[(idx + 1)..], out int p))
                return (raw[..idx], p);
            return (raw, DiscoveryPort);
        }

        // ── Ping ─────────────────────────────────────────────────────────────────

        private async Task PingAndUpdateRowAsync(ServerRow row)
        {
            long ms = await MeasurePingAsync(row.Data.Host, row.Data.Port);
            Dispatcher.Invoke(() =>
            {
                row.PingText.Text = ms >= 0 ? $"{ms} ms" : "—";
                row.PingText.Foreground = new SolidColorBrush(ms < 0 ? Color.FromRgb(255, 80, 80)
                    : ms < 80 ? Color.FromRgb(80, 220, 120)
                    : ms < 200 ? Color.FromRgb(255, 200, 50)
                    : Color.FromRgb(255, 100, 50));
            });
        }

        private static async Task<long> MeasurePingAsync(string host, int port)
        {
            try
            {
                using var tcp = new TcpClient();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(2));
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch { return -1; }
        }

        // ── Row management ───────────────────────────────────────────────────────

        private class ServerRow
        {
            public ServerAnnounceData Data = null!;
            public string Source = "";
            public Border RowBorder = null!;
            public TextBlock NameText = null!;
            public TextBlock PlayersText = null!;
            public TextBlock PingText = null!;
        }

        private void AddRow(ServerAnnounceData info, string source)
        {
            var key = $"{info.Host}:{info.Port}";
            var row = new ServerRow { Data = info, Source = source };
            bool odd = _servers.Count % 2 == 0;

            row.RowBorder = new Border
            {
                Background = new SolidColorBrush(odd ? Color.FromRgb(13, 17, 23) : Color.FromRgb(22, 27, 34)),
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 88, 166, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            row.RowBorder.MouseLeftButtonUp += (_, _) => SelectRow(row);
            row.RowBorder.MouseLeftButtonDown += (_, e2) => { if (e2.ClickCount == 2) ConnectToRow(row); };

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });

            // Badge + name
            var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
            var badgeFg = source == "LAN" ? Color.FromRgb(80, 200, 120)
                        : source == "WAN" ? Color.FromRgb(180, 120, 240)
                        : Color.FromRgb(120, 180, 255);
            var badgeBg = source == "LAN" ? Color.FromRgb(20, 80, 40)
                        : source == "WAN" ? Color.FromRgb(60, 30, 80)
                        : Color.FromRgb(20, 50, 90);
            var badge = new Border { Background = new SolidColorBrush(badgeBg), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, 0, 6, 0) };
            badge.Child = new TextBlock { Text = source, FontSize = 8, Foreground = new SolidColorBrush(badgeFg) };
            nameStack.Children.Add(badge);
            row.NameText = new TextBlock { Text = info.ServerName, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(230, 237, 243)), VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(row.NameText);
            Grid.SetColumn(nameStack, 0); g.Children.Add(nameStack);

            g.Children.Add(MakeCell(info.Host, Color.FromRgb(139, 148, 158), 1));
            g.Children.Add(MakeCell(info.Port.ToString(), Color.FromRgb(139, 148, 158), 2));
            row.PlayersText = MakeCell(info.UserCount >= 0 ? info.UserCount.ToString() : "?", Color.FromRgb(88, 166, 255), 3);
            g.Children.Add(row.PlayersText);
            row.PingText = MakeCell("…", Color.FromRgb(100, 200, 100), 4);
            g.Children.Add(row.PingText);

            row.RowBorder.Child = g;
            ServerListPanel.Children.Add(row.RowBorder);
            _servers[key] = row;

            _ = PingAndUpdateRowAsync(row);
        }

        private static TextBlock MakeCell(string text, Color color, int col)
        {
            var tb = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tb, col); return tb;
        }

        private void UpdateRow(ServerRow row, ServerAnnounceData info)
        {
            row.Data = info;
            row.NameText.Text = info.ServerName;
            row.PlayersText.Text = info.UserCount >= 0 ? info.UserCount.ToString() : "?";
            _ = PingAndUpdateRowAsync(row);
        }

        private ServerRow? _selectedRow;

        private void SelectRow(ServerRow row)
        {
            if (_selectedRow != null)
            {
                bool odd2 = ServerListPanel.Children.IndexOf(_selectedRow.RowBorder) % 2 == 0;
                _selectedRow.RowBorder.Background = new SolidColorBrush(odd2 ? Color.FromRgb(13, 17, 23) : Color.FromRgb(22, 27, 34));
                _selectedRow.RowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            }
            _selectedRow = row;
            row.RowBorder.Background = new SolidColorBrush(Color.FromRgb(30, 55, 90));
            row.RowBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(88, 166, 255));
            row.RowBorder.BorderThickness = new Thickness(2, 1, 0, 1);
            ConnectBtn.IsEnabled = true;
            StatusText.Text = $"Selected: {row.Data.ServerName}  ({row.Data.Host}:{row.Data.Port})";
        }

        private void ConnectToRow(ServerRow row) { SelectRow(row); FinishConnect(); }

        private void Connect_Click(object sender, RoutedEventArgs e) => FinishConnect();

        private void FinishConnect()
        {
            if (_selectedRow == null) return;
            SelectedServer = _selectedRow.Data;
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ── Persistent storage ───────────────────────────────────────────────────

        private void SaveServers()
        {
            try
            {
                var toSave = _servers.Values
                    .Where(r => r.Source is "WAN" or "Saved")
                    .Select(r => r.Data)
                    .ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(SaveFile)!);
                File.WriteAllText(SaveFile, JsonSerializer.Serialize(toSave));
            }
            catch { }
        }

        private void LoadSavedServers()
        {
            try
            {
                if (!File.Exists(SaveFile)) return;
                var saved = JsonSerializer.Deserialize<List<ServerAnnounceData>>(File.ReadAllText(SaveFile));
                if (saved == null) return;
                foreach (var info in saved)
                    AddRow(info, source: "Saved");
            }
            catch { }
        }

        // ── UI helpers ───────────────────────────────────────────────────────────

        private void SetScanning(bool scanning)
        {
            // Don't use a blocking overlay — rows must stay clickable while scanning.
            // Just disable the scan button and update the subtitle.
            LoadingOverlay.Visibility = Visibility.Collapsed; // always hidden
            ScanLanBtn.IsEnabled = !scanning;
            if (scanning) SubtitleText.Text = "Scanning…";
        }

        private void RefreshEmptyState()
        {
            EmptyState.Visibility = _servers.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
