using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MSNShared;

namespace MSNClient
{
    public partial class GarticPhoneWindow : Window
    {
        private readonly ClientState _state = App.State;
        private readonly string _lobbyId;
        private bool _isHost;
        private bool _gameStarted;

        // Drawing state
        private bool _isDrawing;
        private Point _lastPoint;
        private Color _currentColor = Colors.Black;

        // Phase state
        private bool _submitted;

        private static readonly Color[] PaletteColors =
        {
            Colors.Black, Colors.White, Colors.Gray,
            Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Gold,
            Colors.Green, Colors.LimeGreen, Colors.Teal,
            Colors.Blue, Colors.DodgerBlue, Colors.Navy,
            Colors.Purple, Colors.Magenta, Colors.HotPink,
            Colors.Brown, Colors.SaddleBrown, Colors.Tan
        };

        public GarticPhoneWindow(GarticPhonePacket lobbyState)
        {
            InitializeComponent();
            _lobbyId = lobbyState.LobbyId;

            // Build color palette
            foreach (var color in PaletteColors)
            {
                var btn = new Button
                {
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(1),
                    Background = new SolidColorBrush(color),
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                var c = color;
                btn.Click += (_, _) => _currentColor = c;
                ColorPalette.Children.Add(btn);
            }

            _state.Net.PacketReceived += OnPacket;
            Closed += (_, _) =>
            {
                _state.Net.PacketReceived -= OnPacket;
                _ = _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
                {
                    Msg = GarticPhoneMsgType.LeaveLobby,
                    LobbyId = _lobbyId
                }));
            };

            ApplyLobbyState(lobbyState);
        }

        private void OnPacket(Packet pkt)
        {
            if (pkt.Type != PacketType.GarticPhone) return;
            var gp = pkt.GetData<GarticPhonePacket>();
            if (gp == null || gp.LobbyId != _lobbyId) return;

            Dispatcher.Invoke(() =>
            {
                switch (gp.Msg)
                {
                    case GarticPhoneMsgType.LobbyState:
                        ApplyLobbyState(gp);
                        break;
                    case GarticPhoneMsgType.PhaseState:
                        ApplyPhaseState(gp);
                        break;
                    case GarticPhoneMsgType.ChainResult:
                        ShowChainResult(gp);
                        break;
                    case GarticPhoneMsgType.GameOver:
                        ShowGameOver(gp);
                        break;
                }
            });
        }

        private void ApplyLobbyState(GarticPhonePacket gp)
        {
            _isHost = gp.Host == _state.MyUsername;
            _gameStarted = gp.GameStarted;

            LobbyTitle.Text = $"📞 {gp.LobbyName}";
            LobbyInfo.Text = $"{gp.Players.Count}/{gp.MaxPlayers} players • Draw: {gp.DrawTimeSeconds}s • Describe: {gp.DescribeTimeSeconds}s";

            PlayerListPanel.Children.Clear();
            foreach (var player in gp.Players)
            {
                var displayName = gp.PlayerDisplayNames.GetValueOrDefault(player, player);
                var isHost = player == gp.Host;
                PlayerListPanel.Children.Add(new TextBlock
                {
                    Text = $"{(isHost ? "👑 " : "   ")}{displayName}",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(isHost ? Color.FromRgb(74, 20, 140) : Color.FromRgb(80, 80, 80)),
                    FontWeight = isHost ? FontWeights.Bold : FontWeights.Normal,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }

            StartBtn.Visibility = _isHost && !_gameStarted && gp.Players.Count >= 2
                ? Visibility.Visible : Visibility.Collapsed;

            if (!_gameStarted)
            {
                ShowPanel("lobby");
            }
        }

        private void ApplyPhaseState(GarticPhonePacket gp)
        {
            _submitted = false;
            PhaseProgressText.Text = $"Phase {gp.PhaseIndex + 1} of {gp.TotalPhases}";

            if (gp.PhaseType == "write")
            {
                ShowPanel("write");
                PhraseBox.Text = "";
                PhraseBox.Focus();
                SubmitPhraseBtn.IsEnabled = true;
                PhaseText.Text = "✍️ Write Phase";
            }
            else if (gp.PhaseType == "draw")
            {
                ShowPanel("draw");
                DrawingCanvas.Children.Clear();
                DrawPromptText.Text = $"🎨 Draw: \"{gp.Prompt}\"";
                PhaseText.Text = "🎨 Drawing Phase";
                SubmitDrawingBtn.IsEnabled = true;
            }
            else if (gp.PhaseType == "describe")
            {
                ShowPanel("describe");
                DescriptionBox.Text = "";
                PhaseText.Text = "📝 Describe Phase";
                SubmitDescriptionBtn.IsEnabled = true;

                if (!string.IsNullOrEmpty(gp.DrawingBase64))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(gp.DrawingBase64);
                        var image = new BitmapImage();
                        using (var ms = new MemoryStream(bytes))
                        {
                            image.BeginInit();
                            image.CacheOption = BitmapCacheOption.OnLoad;
                            image.StreamSource = ms;
                            image.EndInit();
                        }
                        DescribeImage.Source = image;
                    }
                    catch
                    {
                        DescribeImage.Source = null;
                    }
                }
                else
                {
                    DescribeImage.Source = null;
                }
            }

            StartTimer(gp.TimeLeft);
        }

        private CancellationTokenSource? _timerCts;

        private void StartTimer(int seconds)
        {
            _timerCts?.Cancel();
            _timerCts = new CancellationTokenSource();
            var cts = _timerCts;

            TimerText.Text = seconds.ToString();

            _ = Task.Run(async () =>
            {
                for (int t = seconds; t >= 0; t--)
                {
                    if (cts.IsCancellationRequested) return;
                    var time = t;
                    Dispatcher.Invoke(() =>
                    {
                        TimerText.Text = time.ToString();
                        TimerText.Foreground = time <= 10
                            ? new SolidColorBrush(Colors.Red)
                            : new SolidColorBrush(Color.FromRgb(255, 215, 0));
                    });
                    try { await Task.Delay(1000, cts.Token); }
                    catch { return; }
                }
            });
        }

        // ─── Chain Reveal (Gartic Phone chat-bubble style) ───
        private void ShowChainResult(GarticPhonePacket gp)
        {
            _isHost = gp.Host == _state.MyUsername;
            ShowPanel("reveal");
            RevealTitle.Text = $"{gp.ChainOwnerDisplay.ToUpperInvariant()}'S ALBUM";

            // Build player list on the left
            RevealPlayerList.Children.Clear();
            foreach (var player in gp.Players)
            {
                var displayName = gp.PlayerDisplayNames.GetValueOrDefault(player, player);
                var isOwner = player == gp.ChainOwner;

                var playerBorder = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 2),
                    Background = isOwner
                        ? new SolidColorBrush(Color.FromArgb(120, 100, 255, 150))   // green highlight for chain owner
                        : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    Cursor = Cursors.Hand
                };

                var playerText = new TextBlock
                {
                    Text = displayName.ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White
                };

                playerBorder.Child = playerText;
                RevealPlayerList.Children.Add(playerBorder);
            }

            // Build chain steps as chat bubbles
            RevealStepsPanel.Children.Clear();
            bool leftSide = true; // alternate sides like a chat

            for (int i = 0; i < gp.ChainSteps.Count; i++)
            {
                var step = gp.ChainSteps[i];
                var isFirst = i == 0;

                // Outer container — align left or right
                var container = new StackPanel
                {
                    HorizontalAlignment = leftSide ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 6),
                    MaxWidth = 450
                };

                // Player name label above the bubble
                var nameLabel = new TextBlock
                {
                    Text = step.PlayerDisplay.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 210, 255)),
                    HorizontalAlignment = leftSide ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    Margin = new Thickness(8, 0, 8, 2)
                };
                container.Children.Add(nameLabel);

                // Chat bubble
                var bubble = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 10, 14, 10),
                    MinWidth = 80
                };

                if (step.Type == "phrase" || step.Type == "description")
                {
                    // Text bubble — white background
                    bubble.Background = Brushes.White;

                    var textContent = new TextBlock
                    {
                        Text = step.Content,
                        FontSize = step.Type == "phrase" ? 17 : 15,
                        FontWeight = step.Type == "phrase" ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Left
                    };
                    bubble.Child = textContent;
                }
                else if (step.Type == "drawing")
                {
                    // Drawing bubble — white background with image
                    bubble.Background = Brushes.White;
                    bubble.Padding = new Thickness(6);

                    if (!string.IsNullOrEmpty(step.Content))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(step.Content);
                            var img = new BitmapImage();
                            using (var ms = new MemoryStream(bytes))
                            {
                                img.BeginInit();
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.StreamSource = ms;
                                img.EndInit();
                            }
                            bubble.Child = new Image
                            {
                                Source = img,
                                MaxHeight = 250,
                                MaxWidth = 400,
                                Stretch = Stretch.Uniform
                            };
                        }
                        catch
                        {
                            bubble.Child = new TextBlock
                            {
                                Text = "(drawing failed to load)",
                                FontStyle = FontStyles.Italic,
                                Foreground = Brushes.Gray
                            };
                        }
                    }
                }

                container.Children.Add(bubble);
                RevealStepsPanel.Children.Add(container);

                // Alternate sides for each step
                leftSide = !leftSide;
            }

            // Scroll to top
            RevealScrollViewer.ScrollToTop();

            // Chain counter and Next button
            ChainCountText.Text = $"Chain {gp.ChainIndex + 1} of {gp.TotalChains}";
            NextChainBtn.Visibility = _isHost ? Visibility.Visible : Visibility.Collapsed;
            NextChainBtn.IsEnabled = true;
            NextChainBtn.Content = gp.ChainIndex + 1 < gp.TotalChains ? "Next Chain ▶" : "Finish ▶";

            PhaseText.Text = "🔗 Chain Reveal";
            TimerText.Text = "";
        }

        private void ShowGameOver(GarticPhonePacket gp)
        {
            _timerCts?.Cancel();
            ShowPanel("gameover");
            GameOverText.Text = gp.Message;
            PhaseText.Text = "🎉 Game Over";
            TimerText.Text = "";
        }

        private void ShowPanel(string panelName)
        {
            LobbyPanel.Visibility = panelName == "lobby" ? Visibility.Visible : Visibility.Collapsed;
            WritePanel.Visibility = panelName == "write" ? Visibility.Visible : Visibility.Collapsed;
            DrawPanel.Visibility = panelName == "draw" ? Visibility.Visible : Visibility.Collapsed;
            DescribePanel.Visibility = panelName == "describe" ? Visibility.Visible : Visibility.Collapsed;
            WaitingPanel.Visibility = panelName == "waiting" ? Visibility.Visible : Visibility.Collapsed;
            RevealPanel.Visibility = panelName == "reveal" ? Visibility.Visible : Visibility.Collapsed;
            GameOverPanel.Visibility = panelName == "gameover" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── Write Phase ───
        private async void SubmitPhrase_Click(object sender, RoutedEventArgs e)
        {
            if (_submitted) return;
            var phrase = PhraseBox.Text.Trim();
            if (string.IsNullOrEmpty(phrase)) return;

            _submitted = true;
            SubmitPhraseBtn.IsEnabled = false;

            await _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.SubmitPhrase,
                LobbyId = _lobbyId,
                Description = phrase
            }));

            ShowPanel("waiting");
        }

        // ─── Drawing ───
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_submitted) return;
            _isDrawing = true;
            _lastPoint = e.GetPosition(DrawingCanvas);
            DrawingCanvas.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDrawing || _submitted) return;
            var pos = e.GetPosition(DrawingCanvas);

            var line = new Line
            {
                X1 = _lastPoint.X,
                Y1 = _lastPoint.Y,
                X2 = pos.X,
                Y2 = pos.Y,
                Stroke = new SolidColorBrush(_currentColor),
                StrokeThickness = BrushSizeSlider.Value,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            DrawingCanvas.Children.Add(line);
            _lastPoint = pos;
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDrawing = false;
            DrawingCanvas.ReleaseMouseCapture();
        }

        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            if (_submitted) return;
            DrawingCanvas.Children.Clear();
        }

        private async void SubmitDrawing_Click(object sender, RoutedEventArgs e)
        {
            if (_submitted) return;
            _submitted = true;
            SubmitDrawingBtn.IsEnabled = false;

            var base64 = RenderCanvasToBase64();

            await _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.SubmitDrawing,
                LobbyId = _lobbyId,
                DrawingBase64 = base64
            }));

            ShowPanel("waiting");
        }

        // ─── Describe Phase ───
        private async void SubmitDescription_Click(object sender, RoutedEventArgs e)
        {
            if (_submitted) return;
            var text = DescriptionBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            _submitted = true;
            SubmitDescriptionBtn.IsEnabled = false;

            await _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.SubmitDescription,
                LobbyId = _lobbyId,
                Description = text
            }));

            ShowPanel("waiting");
        }

        // ─── Host: Next Chain ───
        private async void NextChain_Click(object sender, RoutedEventArgs e)
        {
            NextChainBtn.IsEnabled = false;

            await _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.NextChain,
                LobbyId = _lobbyId
            }));
        }

        private string RenderCanvasToBase64()
        {
            try
            {
                var width = (int)DrawingCanvas.ActualWidth;
                var height = (int)DrawingCanvas.ActualHeight;
                if (width <= 0 || height <= 0) return "";

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(DrawingCanvas);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                return "";
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            StartBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.StartGame,
                LobbyId = _lobbyId
            }));
        }

        private async void Leave_Click(object sender, RoutedEventArgs e)
        {
            await _state.Net.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.LeaveLobby,
                LobbyId = _lobbyId
            }));
            Close();
        }
    }
}
