using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MSNShared;

namespace MSNClient
{
    public partial class LoginWindow : Window
    {
        private readonly ClientState _state = App.State;

        public LoginWindow()
        {
            InitializeComponent();
            _state.Net.PacketReceived += OnPacket;
            _state.Net.ConnectionError += msg => Dispatcher.Invoke(() =>
            {
                ConnectionStatus.Text = $"❌ {msg}";
                ConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            });
        }

        private void OnPacket(Packet pkt)
        {
            Dispatcher.Invoke(() =>
            {
                switch (pkt.Type)
                {
                    case PacketType.LoginAck:
                        var ack = pkt.GetData<LoginAckData>();
                        if (ack?.Success == true)
                        {
                            _state.MyUsername = ack.User!.Username;
                            _state.MyDisplayName = ack.User.DisplayName;
                            // Open main window
                            var main = new MainWindow();
                            main.Show();
                            Close();
                        }
                        else
                        {
                            LoginError.Text = ack?.Message ?? "Login failed.";
                            LoginError.Visibility = Visibility.Visible;
                        }
                        break;

                    case PacketType.RegisterAck:
                        var rack = pkt.GetData<RegisterAckData>();
                        if (rack?.Success == true)
                        {
                            RegSuccess.Text = "✅ " + rack.Message;
                            RegSuccess.Visibility = Visibility.Visible;
                            RegError.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            RegError.Text = rack?.Message ?? "Registration failed.";
                            RegError.Visibility = Visibility.Visible;
                            RegSuccess.Visibility = Visibility.Collapsed;
                        }
                        break;

                    case PacketType.Error:
                        var err = pkt.GetData<ErrorData>();
                        LoginError.Text = err?.Message ?? "An error occurred.";
                        LoginError.Visibility = Visibility.Visible;
                        break;
                }
            });
        }

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            LoginError.Visibility = Visibility.Collapsed;
            var host = HostBox.Text.Trim();
            var portText = PortBox.Text.Trim();

            if (!int.TryParse(portText, out var port)) { LoginError.Text = "Invalid port."; LoginError.Visibility = Visibility.Visible; return; }
            if (string.IsNullOrWhiteSpace(UsernameBox.Text)) { LoginError.Text = "Enter a username."; LoginError.Visibility = Visibility.Visible; return; }
            if (PasswordBox.Password.Length == 0) { LoginError.Text = "Enter a password."; LoginError.Visibility = Visibility.Visible; return; }

            ConnectionStatus.Text = "Connecting...";
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;

            if (!_state.Net.IsConnected)
            {
                var ok = await _state.Net.ConnectAsync(host, port);
                if (!ok)
                {
                    ConnectionStatus.Text = "❌ Could not connect.";
                    ConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }
            }

            ConnectionStatus.Text = "✅ Connected";
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.Green;

            var statusTag = (StatusCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Online";
            var status = Enum.TryParse<UserStatus>(statusTag, out var s) ? s : UserStatus.Online;

            await _state.Net.SendAsync(Packet.Create(PacketType.Login, new LoginData
            {
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password,
                Status = status
            }));
        }

        private async void Register_Click(object sender, RoutedEventArgs e)
        {
            RegError.Visibility = Visibility.Collapsed;
            RegSuccess.Visibility = Visibility.Collapsed;

            if (RegPasswordBox.Password != RegConfirmBox.Password)
            {
                RegError.Text = "Passwords do not match."; RegError.Visibility = Visibility.Visible; return;
            }

            var host = HostBox.Text.Trim();
            if (!int.TryParse(PortBox.Text.Trim(), out var port)) { RegError.Text = "Invalid port."; RegError.Visibility = Visibility.Visible; return; }

            if (!_state.Net.IsConnected)
            {
                var ok = await _state.Net.ConnectAsync(host, port);
                if (!ok) { RegError.Text = "Could not connect to server."; RegError.Visibility = Visibility.Visible; return; }
            }

            await _state.Net.SendAsync(Packet.Create(PacketType.Register, new RegisterData
            {
                Username = RegUsernameBox.Text.Trim(),
                DisplayName = RegDisplayBox.Text.Trim(),
                Email = RegEmailBox.Text.Trim(),
                Password = RegPasswordBox.Password
            }));
        }

        private async void FindServers_Click(object sender, RoutedEventArgs e)
        {
            ConnectionStatus.Text = "Scanning LAN...";
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
            ServerList.Items.Clear();

            if (!int.TryParse(PortBox.Text.Trim(), out var dp)) dp = 443;

            var servers = await NetworkClient.DiscoverServersAsync(dp + 1);

            if (servers.Count == 0)
            {
                ConnectionStatus.Text = "No servers found on LAN.";
                ServerList.Visibility = Visibility.Collapsed;
            }
            else
            {
                ConnectionStatus.Text = $"Found {servers.Count} server(s):";
                foreach (var s in servers)
                    ServerList.Items.Add($"{s.Host}:{s.Port}  [{s.ServerName}]  ({s.UserCount} online)");
                ServerList.Visibility = Visibility.Visible;
            }
        }

        private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ServerList.SelectedItem is string item)
            {
                var parts = item.Split(':');
                if (parts.Length >= 2)
                {
                    HostBox.Text = parts[0];
                    PortBox.Text = parts[1].Split(' ')[0];
                }
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) SignIn_Click(sender, e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _state.Net.PacketReceived -= OnPacket;
            base.OnClosed(e);
        }
    }
}
