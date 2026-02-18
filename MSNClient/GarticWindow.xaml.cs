using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MSNShared;

namespace MSNClient
{
    public partial class GarticWindow : Window
    {
        private readonly ClientState _state = App.State;
        private string _lobbyId = "";
        private bool _isDrawer;
        private bool _gameStarted;
        private string _myUsername = "";
        private Point _lastPoint;
        private bool _isDrawing;
        private Color _currentColor = Colors.Black;
        private readonly List<string> _drawHistory = new(); // accumulated draw data for new joiners

        // Color palette
        private static readonly Color[] Palette =
        {
            Colors.Black, Colors.White,
            Color.FromRgb(200, 0, 0),    Color.FromRgb(0, 150, 0),
            Color.FromRgb(0, 0, 200),    Color.FromRgb(255, 165, 0),
            Color.FromRgb(128, 0, 128),  Color.FromRgb(255, 192, 203),
            Color.FromRgb(255, 255, 0),  Color.FromRgb(0, 200, 200),
            Color.FromRgb(139, 69, 19),  Color.FromRgb(128, 128, 128)
        };

        public GarticWindow(GarticPacket lobbyState)
        {
            InitializeComponent();
            _myUsername = _state.MyUsername ?? "";
            _lobbyId = lobbyState.LobbyId;

            _state.Net.PacketReceived += OnPacket;
            Closed += OnClosed;

            InitColorPalette();
            ApplyLobbyState(lobbyState);
        }

        // ── Initialization ─────────────────────────────────────────────────

        private void InitColorPalette()
        {
            foreach (var color in Palette)
            {
                var btn = new Border
                {
                    Width = 20,
                    Height = 20,
                    Background = new SolidColorBrush(color),
                    BorderBrush = Brushes.DarkGray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                var c = color;
                btn.MouseLeftButtonDown += (s, e) =>
                {
                    _currentColor = c;
                    // Highlight selected
                    foreach (Border child in ColorPalette.Children)
                        child.BorderBrush = Brushes.DarkGray;
                    btn.BorderBrush = Brushes.Red;
                    btn.BorderThickness = new Thickness(2);
                };
                ColorPalette.Children.Add(btn);
            }
            // Select first color
            if (ColorPalette.Children.Count > 0)
            {
                var first = (Border)ColorPalette.Children[0];
                first.BorderBrush = Brushes.Red;
                first.BorderThickness = new Thickness(2);
            }
        }

        // ── Packet handling ────────────────────────────────────────────────

        private void OnPacket(Packet pkt)
        {
            if (pkt.Type != PacketType.Gartic) return;
            var gp = pkt.GetData<GarticPacket>();
            if (gp == null || gp.LobbyId != _lobbyId) return;

            Dispatcher.Invoke(() => HandleGarticPacket(gp));
        }

        private void HandleGarticPacket(GarticPacket pkt)
        {
            switch (pkt.Msg)
            {
                case GarticMsgType.LobbyState:
                    ApplyLobbyState(pkt);
                    break;

                case GarticMsgType.RoundState:
                    ApplyRoundState(pkt);
                    break;

                case GarticMsgType.DrawData:
                    ApplyDrawData(pkt.DrawDataJson);
                    break;

                case GarticMsgType.ClearCanvas:
                    DrawingCanvas.Children.Clear();
                    _drawHistory.Clear();
                    AddChatMessage("System", "🗑 Canvas cleared.");
                    break;

                case GarticMsgType.ChatGuess:
                    AddChatMessage(pkt.DisplayName, pkt.Message, isGuess: true);
                    break;

                case GarticMsgType.ChatMessage:
                    AddChatMessage(pkt.From, pkt.Message, isSystem: pkt.From == "System");
                    break;

                case GarticMsgType.CorrectGuess:
                    AddChatMessage("System", $"✅ {pkt.DisplayName} guessed the word!", isCorrect: true);
                    UpdateScores(pkt.Scores);
                    break;

                case GarticMsgType.WordReveal:
                    AddChatMessage("System", $"⏰ The word was: {pkt.Word}", isReveal: true);
                    WordHintText.Text = pkt.Word;
                    UpdateScores(pkt.Scores);
                    break;

                case GarticMsgType.GameOver:
                    HandleGameOver(pkt);
                    break;
            }
        }

        // ── Lobby state ────────────────────────────────────────────────────

        private void ApplyLobbyState(GarticPacket pkt)
        {
            _gameStarted = pkt.GameStarted;
            Title = $"🎨 Gartic — {pkt.LobbyName}";

            UpdatePlayerList(pkt.Players, pkt.PlayerDisplayNames, pkt.Scores, "");
            StartGameBtn.Visibility = (pkt.Host == _myUsername && !_gameStarted) ? Visibility.Visible : Visibility.Collapsed;

            if (!_gameStarted)
            {
                RoundText.Text = "Waiting for players...";
                WordHintText.Text = "";
                TimerText.Text = "";
                CanvasOverlay.Text = pkt.Host == _myUsername
                    ? "You are the host! Click 'Start Game' when ready."
                    : "Waiting for the host to start the game...";
                CanvasOverlay.Visibility = Visibility.Visible;
                DrawToolbar.Visibility = Visibility.Collapsed;
                GuessBox.IsEnabled = false;
            }
        }

        // ── Round state ────────────────────────────────────────────────────

        private void ApplyRoundState(GarticPacket pkt)
        {
            _gameStarted = true;
            _isDrawer = pkt.CurrentDrawer == _myUsername;
            StartGameBtn.Visibility = Visibility.Collapsed;

            RoundText.Text = $"Round {pkt.Round}/{pkt.TotalRounds}";
            TimerText.Text = $"⏰ {pkt.TimeLeft}s";

            UpdatePlayerList(pkt.Players, pkt.PlayerDisplayNames, pkt.Scores, pkt.CurrentDrawer);
            UpdateScores(pkt.Scores);

            if (_isDrawer)
            {
                WordHintText.Text = pkt.Word;
                CanvasOverlay.Visibility = Visibility.Collapsed;
                DrawToolbar.Visibility = Visibility.Visible;
                GuessBox.IsEnabled = false;
                DrawingCanvas.Cursor = Cursors.Cross;

                // If this is a new round (not just a timer update), clear canvas
                if (pkt.TimeLeft == pkt.RoundTimeSeconds)
                {
                    DrawingCanvas.Children.Clear();
                    _drawHistory.Clear();
                }
            }
            else
            {
                WordHintText.Text = FormatHint(pkt.WordHint);
                CanvasOverlay.Visibility = Visibility.Collapsed;
                DrawToolbar.Visibility = Visibility.Collapsed;
                GuessBox.IsEnabled = true;
                DrawingCanvas.Cursor = Cursors.Arrow;

                if (pkt.TimeLeft == pkt.RoundTimeSeconds)
                {
                    DrawingCanvas.Children.Clear();
                    _drawHistory.Clear();
                }
            }
        }

        private string FormatHint(string hint)
        {
            return string.Join(" ", hint.ToArray());
        }

        // ── Drawing ────────────────────────────────────────────────────────

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isDrawer || !_gameStarted) return;
            _isDrawing = true;
            _lastPoint = e.GetPosition(DrawingCanvas);
            DrawingCanvas.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDrawing || !_isDrawer) return;
            var pos = e.GetPosition(DrawingCanvas);
            var thickness = BrushSizeSlider.Value;

            DrawLine(_lastPoint, pos, _currentColor, thickness);

            // Send drawing data to server
            var data = JsonSerializer.Serialize(new DrawStroke
            {
                X1 = _lastPoint.X,
                Y1 = _lastPoint.Y,
                X2 = pos.X,
                Y2 = pos.Y,
                R = _currentColor.R,
                G = _currentColor.G,
                B = _currentColor.B,
                Thickness = thickness
            });

            _drawHistory.Add(data);

            _ = _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
            {
                Msg = GarticMsgType.DrawData,
                LobbyId = _lobbyId,
                DrawDataJson = data
            }));

            _lastPoint = pos;
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDrawing = false;
            DrawingCanvas.ReleaseMouseCapture();
        }

        private void DrawLine(Point from, Point to, Color color, double thickness)
        {
            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            DrawingCanvas.Children.Add(line);
        }

        private void ApplyDrawData(string? json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var stroke = JsonSerializer.Deserialize<DrawStroke>(json);
                if (stroke == null) return;
                var color = Color.FromRgb(stroke.R, stroke.G, stroke.B);
                DrawLine(new Point(stroke.X1, stroke.Y1), new Point(stroke.X2, stroke.Y2),
                    color, stroke.Thickness);
                _drawHistory.Add(json);
            }
            catch { }
        }

        private async void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            DrawingCanvas.Children.Clear();
            _drawHistory.Clear();
            await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
            {
                Msg = GarticMsgType.ClearCanvas,
                LobbyId = _lobbyId
            }));
        }

        // ── Chat / Guess ───────────────────────────────────────────────────

        private void GuessBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) SendGuess();
        }

        private void SendGuess_Click(object sender, RoutedEventArgs e)
        {
            SendGuess();
        }

        private async void SendGuess()
        {
            var text = GuessBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            GuessBox.Text = "";

            await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
            {
                Msg = GarticMsgType.ChatGuess,
                LobbyId = _lobbyId,
                Message = text
            }));
        }

        private void AddChatMessage(string from, string message, bool isSystem = false,
            bool isGuess = false, bool isCorrect = false, bool isReveal = false)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            if (isSystem || isCorrect || isReveal)
            {
                var color = isCorrect ? Color.FromRgb(0, 140, 0) :
                            isReveal ? Color.FromRgb(200, 0, 0) :
                            Color.FromRgb(80, 100, 140);
                panel.Children.Add(new TextBlock
                {
                    Text = message,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(color),
                    FontWeight = (isCorrect || isReveal) ? FontWeights.Bold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 200
                });
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"{from}: ",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153))
                });
                panel.Children.Add(new TextBlock
                {
                    Text = message,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 180
                });
            }

            ChatPanel.Children.Add(panel);

            // Auto-scroll
            ChatScroller.ScrollToEnd();
        }

        // ── Player list ────────────────────────────────────────────────────

        private void UpdatePlayerList(List<string> players, Dictionary<string, string> displayNames,
            Dictionary<string, int> scores, string currentDrawer)
        {
            PlayerListPanel.Children.Clear();
            var sortedPlayers = players.OrderByDescending(p => scores.GetValueOrDefault(p, 0)).ToList();

            var rank = 1;
            foreach (var player in sortedPlayers)
            {
                var isDrawer = player == currentDrawer;
                var displayName = displayNames.GetValueOrDefault(player, player);
                var score = scores.GetValueOrDefault(player, 0);
                var isMe = player == _myUsername;

                var row = new Border
                {
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 1, 0, 1),
                    Background = isDrawer ? new SolidColorBrush(Color.FromArgb(40, 255, 200, 0)) :
                                 isMe ? new SolidColorBrush(Color.FromArgb(30, 0, 120, 255)) :
                                 Brushes.Transparent,
                    CornerRadius = new CornerRadius(3)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new TextBlock
                {
                    Text = isDrawer ? "🎨" : $"#{rank}",
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 140))
                };

                var nameBlock = new TextBlock
                {
                    Text = displayName + (isMe ? " (you)" : ""),
                    FontSize = 10,
                    FontWeight = isDrawer ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 30, 80)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var scoreBlock = new TextBlock
                {
                    Text = $"{score} pts",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 120, 160)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                Grid.SetColumn(icon, 0);
                Grid.SetColumn(nameBlock, 1);
                Grid.SetColumn(scoreBlock, 2);

                grid.Children.Add(icon);
                grid.Children.Add(nameBlock);
                grid.Children.Add(scoreBlock);
                row.Child = grid;
                PlayerListPanel.Children.Add(row);
                rank++;
            }
        }

        private void UpdateScores(Dictionary<string, int>? scores)
        {
            // Scores are updated whenever the player list is rebuilt in ApplyRoundState
            // This method could be used for mid-round score updates if needed
        }

        // ── Game over ──────────────────────────────────────────────────────

        private void HandleGameOver(GarticPacket pkt)
        {
            _gameStarted = false;
            _isDrawer = false;

            RoundText.Text = "🏆 Game Over!";
            TimerText.Text = "";
            WordHintText.Text = "";
            CanvasOverlay.Text = "Game Over! 🏆";
            CanvasOverlay.Visibility = Visibility.Visible;
            DrawToolbar.Visibility = Visibility.Collapsed;
            GuessBox.IsEnabled = false;

            // Build final scoreboard in chat
            AddChatMessage("System", "═══ FINAL SCORES ═══", isSystem: true);
            var sorted = pkt.Scores.OrderByDescending(kv => kv.Value).ToList();
            var medal = new[] { "🥇", "🥈", "🥉" };
            for (int i = 0; i < sorted.Count; i++)
            {
                var prefix = i < 3 ? medal[i] : $"#{i + 1}";
                var name = pkt.PlayerDisplayNames.GetValueOrDefault(sorted[i].Key, sorted[i].Key);
                AddChatMessage("System", $"{prefix} {name}: {sorted[i].Value} pts", isSystem: true);
            }

            UpdatePlayerList(pkt.Players, pkt.PlayerDisplayNames, pkt.Scores, "");
        }

        // ── Controls ───────────────────────────────────────────────────────

        private async void StartGame_Click(object sender, RoutedEventArgs e)
        {
            StartGameBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
            {
                Msg = GarticMsgType.StartGame,
                LobbyId = _lobbyId
            }));
        }

        private async void Leave_Click(object sender, RoutedEventArgs e)
        {
            await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
            {
                Msg = GarticMsgType.LeaveLobby,
                LobbyId = _lobbyId
            }));
            Close();
        }

        private async void OnClosed(object? sender, EventArgs e)
        {
            _state.Net.PacketReceived -= OnPacket;
            _state.OpenGarticGames.Remove(_lobbyId);

            // Notify server we're leaving
            try
            {
                await _state.Net.SendAsync(Packet.Create(PacketType.Gartic, new GarticPacket
                {
                    Msg = GarticMsgType.LeaveLobby,
                    LobbyId = _lobbyId
                }));
            }
            catch { }
        }
    }

    // ── Drawing stroke data (serialized in DrawDataJson) ───────────────

    public class DrawStroke
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public double Thickness { get; set; } = 3;
    }
}
