using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MSNShared;

namespace MSNClient
{
    public partial class BlackjackLobbyWindow : Window
    {
        private readonly ClientState _state = App.State;

        public BlackjackLobbyWindow()
        {
            InitializeComponent();
            _state.Net.PacketReceived += OnPacket;
            Closed += (_, _) => _state.Net.PacketReceived -= OnPacket;
            _ = _state.Net.SendAsync(Packet.Create(PacketType.BlackjackLobbyList, new { }));
        }

        private void OnPacket(Packet pkt)
        {
            Dispatcher.Invoke(() =>
            {
                switch (pkt.Type)
                {
                    case PacketType.BlackjackLobbies:
                        var lobbies = pkt.GetData<List<BlackjackLobbyInfo>>();
                        if (lobbies != null) PopulateLobbies(lobbies);
                        break;

                    case PacketType.Blackjack:
                        var bp = pkt.GetData<BlackjackPacket>();
                        if (bp != null && bp.Msg == BlackjackMsgType.LobbyState)
                        {
                            var gameWin = new BlackjackWindow(bp);
                            gameWin.Owner = this.Owner;
                            gameWin.Show();
                            Close();
                        }
                        break;
                }
            });
        }

        private void PopulateLobbies(List<BlackjackLobbyInfo> lobbies)
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
                    CornerRadius = new CornerRadius(3)
                };
                row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(60, 26, 107, 60));
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new TextBlock
                {
                    Text = lobby.GameStarted ? "🎮" : "🃏",
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
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 40, 0))
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"Host: {lobby.HostDisplayName}  •  {lobby.PlayerCount}/{lobby.MaxPlayers} players" +
                           (lobby.GameStarted ? "  •  In Progress" : ""),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(60, 100, 60))
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
                    await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
                    {
                        Msg = BlackjackMsgType.JoinLobby,
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
            await _state.Net.SendAsync(Packet.Create(PacketType.BlackjackLobbyList, new { }));
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            var name = LobbyNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = $"{_state.MyDisplayName}'s Table";
            int.TryParse(MaxPlayersBox.Text, out var maxPlayers);

            CreateBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            {
                Msg = BlackjackMsgType.CreateLobby,
                LobbyName = name,
                MaxPlayers = Math.Clamp(maxPlayers > 0 ? maxPlayers : 6, 2, 7)
            }));
        }
    }
}
