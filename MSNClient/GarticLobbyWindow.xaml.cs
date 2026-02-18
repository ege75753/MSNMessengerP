using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MSNShared;

namespace MSNClient
{
    public partial class GarticLobbyWindow : Window
    {
        private readonly ClientState _state = App.State;

        public GarticLobbyWindow()
        {
            InitializeComponent();
            _state.Net.PacketReceived += OnPacket;
            Closed += (_, _) => _state.Net.PacketReceived -= OnPacket;

            // Request lobby list
            _ = _state.Net.SendAsync(Packet.Create(PacketType.GarticLobbyList, new { }));
        }

        private void OnPacket(Packet pkt)
        {
            Dispatcher.Invoke(() =>
            {
                switch (pkt.Type)
                {
                    case PacketType.GarticLobbies:
                        var lobbies = pkt.GetData<List<GarticLobbyInfo>>();
                        if (lobbies != null) PopulateLobbies(lobbies);
                        break;

                    case PacketType.Gartic:
                        var gp = pkt.GetData<GarticPacket>();
                        if (gp != null && gp.Msg == GarticMsgType.LobbyState)
                        {
                            // We joined or created a lobby — open the game window
                            var gameWin = new GarticWindow(gp);
                            gameWin.Owner = this.Owner;
                            gameWin.Show();
                            Close();
                        }
                        break;
                }
            });
        }

        private void PopulateLobbies(List<GarticLobbyInfo> lobbies)
        {
            LobbyListPanel.Children.Clear();
            NoLobbiesText.Visibility = lobbies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var lobby in lobbies)
            {
                var row = new Border
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Arrow,
                    CornerRadius = new CornerRadius(3)
                };
                row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(60, 100, 160, 240));
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new TextBlock
                {
                    Text = lobby.GameStarted ? "🎮" : "🎨",
                    FontSize = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = lobby.LobbyName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 30, 80))
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"Host: {lobby.HostDisplayName} • {lobby.PlayerCount}/{lobby.MaxPlayers} players" +
                           (lobby.GameStarted ? " • In Progress" : ""),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 100, 140))
                });

                var joinBtn = new Button
                {
                    Content = lobby.GameStarted ? "In Progress" : "Join",
                    Style = (Style)FindResource("MSNBtn"),
                    Padding = new Thickness(14, 4, 14, 4),
                    IsEnabled = !lobby.GameStarted && lobby.PlayerCount < lobby.MaxPlayers,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var lobbyId = lobby.LobbyId;
                joinBtn.Click += async (s, e) =>
                {
                    joinBtn.IsEnabled = false;
                    await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
                    {
                        Msg = GarticMsgType.JoinLobby,
                        LobbyId = lobbyId
                    }));
                };

                Grid.SetColumn(icon, 0);
                Grid.SetColumn(info, 1);
                Grid.SetColumn(joinBtn, 2);

                grid.Children.Add(icon);
                grid.Children.Add(info);
                grid.Children.Add(joinBtn);
                row.Child = grid;
                LobbyListPanel.Children.Add(row);
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await _state.Net.SendAsync(Packet.Create(PacketType.GarticLobbyList, new { }));
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            var name = LobbyNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = $"{_state.MyDisplayName}'s Game";

            int.TryParse(MaxPlayersBox.Text, out var maxPlayers);
            int.TryParse(RoundsBox.Text, out var rounds);
            int.TryParse(TimeBox.Text, out var time);

            CreateBtn.IsEnabled = false;

            await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
            {
                Msg = GarticMsgType.CreateLobby,
                LobbyName = name,
                MaxPlayers = maxPlayers > 0 ? maxPlayers : 8,
                RoundCount = rounds > 0 ? rounds : 3,
                RoundTimeSeconds = time > 0 ? time : 60
            }));
        }
    }
}
