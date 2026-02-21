using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using MSNShared;

namespace MSNClient
{
    public partial class UnoLobbyWindow : Window
    {
        private readonly ClientState _state = App.State;

        public UnoLobbyWindow()
        {
            InitializeComponent();
            _state.Net.PacketReceived += OnPacket;
            RequestLobbyList();
        }

        private void RequestLobbyList()
        {
            RefreshBtn.IsEnabled = false;
            CreateBtn.IsEnabled = false;
            NoLobbiesText.Text = "Loading lobbies...";
            NoLobbiesText.Visibility = Visibility.Visible;
            LobbyListPanel.Children.Clear();
            _ = _state.Net.SendAsync(Packet.Create(PacketType.UnoLobbyList, new { }));

            Task.Delay(1000).ContinueWith(t => Dispatcher.Invoke(() =>
            {
                RefreshBtn.IsEnabled = true;
                CreateBtn.IsEnabled = true;
            }));
        }

        private void OnPacket(Packet pkt)
        {
            switch (pkt.Type)
            {
                case PacketType.UnoLobbies:
                    var lobbies = pkt.GetData<List<UnoLobbyInfo>>();
                    if (lobbies != null) Dispatcher.Invoke(() => BuildLobbyList(lobbies));
                    break;

                case PacketType.Uno:
                    var unoPkt = pkt.GetData<UnoPacket>();
                    if (unoPkt?.Msg == UnoMsgType.LobbyState)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var win = new UnoWindow(unoPkt);
                            win.Show();
                            Close();
                        });
                    }
                    else if (unoPkt?.Msg == UnoMsgType.GameOver)
                    {
                        // handled by window or ignored here
                    }
                    break;
            }
        }

        private void BuildLobbyList(List<UnoLobbyInfo> lobbies)
        {
            LobbyListPanel.Children.Clear();
            if (lobbies.Count == 0)
            {
                NoLobbiesText.Text = "No lobbies available. Create one below!";
                NoLobbiesText.Visibility = Visibility.Visible;
            }
            else
            {
                NoLobbiesText.Visibility = Visibility.Collapsed;
                foreach (var lobby in lobbies)
                {
                    var item = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 4),
                        Cursor = Cursors.Hand
                    };

                    item.MouseEnter += (s, e) => item.Background = new SolidColorBrush(Color.FromRgb(250, 240, 240));
                    item.MouseLeave += (s, e) => item.Background = Brushes.White;

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var infoStack = new StackPanel();
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = lobby.LobbyName,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                        FontSize = 12
                    });

                    infoStack.Children.Add(new TextBlock
                    {
                        Text = $"Host: {lobby.HostDisplayName} • Players: {lobby.PlayerCount}/{lobby.MaxPlayers}",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                    Grid.SetColumn(infoStack, 0);
                    grid.Children.Add(infoStack);

                    var statusText = new TextBlock
                    {
                        Text = lobby.GameStarted ? "In Progress" : "Waiting...",
                        Foreground = lobby.GameStarted
                            ? new SolidColorBrush(Color.FromRgb(180, 50, 50))
                            : new SolidColorBrush(Color.FromRgb(50, 150, 50)),
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 11
                    };

                    Grid.SetColumn(statusText, 1);
                    grid.Children.Add(statusText);

                    item.Child = grid;

                    item.MouseLeftButtonUp += (s, e) =>
                    {
                        if (lobby.GameStarted || lobby.PlayerCount >= lobby.MaxPlayers)
                        {
                            MessageBox.Show("Cannot join this lobby (full or already started).",
                                "Lobby Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        CreateBtn.IsEnabled = false; // Disable to prevent double-click
                        _ = _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
                        {
                            Msg = UnoMsgType.JoinLobby,
                            LobbyId = lobby.LobbyId,
                            From = _state.MyUsername
                        }));
                    };

                    LobbyListPanel.Children.Add(item);
                }
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RequestLobbyList();

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(MaxPlayersBox.Text, out int maxP)) maxP = 4;

            CreateBtn.IsEnabled = false;
            _ = _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
            {
                Msg = UnoMsgType.CreateLobby,
                From = _state.MyUsername,
                LobbyName = LobbyNameBox.Text,
                MaxPlayers = maxP
            }));
        }

        protected override void OnClosed(EventArgs e)
        {
            _state.Net.PacketReceived -= OnPacket;
            base.OnClosed(e);
        }
    }
}
