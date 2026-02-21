using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MSNShared;

namespace MSNClient
{
    public partial class UnoWindow : Window
    {
        private readonly ClientState _state = App.State;
        private UnoPacket _lastPkt;
        private string LobbyId => _lastPkt.LobbyId;

        private Dictionary<string, Border> _playerNodes = new();
        private Dictionary<string, ImageSource?> _profilePics = new();

        // Canvas center
        private const double CenterX = 500;
        private const double CenterY = 350;

        public UnoWindow(UnoPacket initialState)
        {
            InitializeComponent();
            _lastPkt = initialState;
            _state.Net.PacketReceived += OnPacket;

            // Pre-fetch profile pictures
            foreach (var p in _lastPkt.Players)
            {
                var vm = _state.GetContact(p);
                if (vm != null && vm.ProfilePicture != null)
                    _profilePics[p] = vm.ProfilePicture;
            }
            if (_state.MyProfilePicture != null)
                _profilePics[_state.MyUsername] = _state.MyProfilePicture;

            RenderState();
        }

        private void OnPacket(Packet pkt)
        {
            if (pkt.Type == PacketType.Uno)
            {
                var unoPkt = pkt.GetData<UnoPacket>();
                if (unoPkt == null || unoPkt.LobbyId != LobbyId) return;

                Dispatcher.Invoke(() =>
                {
                    if (unoPkt.Msg == UnoMsgType.GameOver)
                    {
                        MessageBox.Show(unoPkt.Message, "Game Over", MessageBoxButton.OK, MessageBoxImage.Information);
                        Close();
                        return;
                    }

                    bool animatePlay = unoPkt.PlayedCard != null;
                    var prevPkt = _lastPkt;
                    _lastPkt = unoPkt;

                    if (animatePlay && prevPkt != null)
                    {
                        AnimateCardPlay(prevPkt.CurrentTurn, unoPkt.PlayedCard!, () => RenderState());
                    }
                    else
                    {
                        RenderState();
                    }
                });
            }
        }

        private void RenderState()
        {
            if (!_lastPkt.GameStarted)
            {
                StatusText.Text = $"Waiting for players ({_lastPkt.Players.Count}/{_lastPkt.MaxPlayers})...";
                StartGameBtn.Visibility = _lastPkt.Host == _state.MyUsername ? Visibility.Visible : Visibility.Collapsed;
                RenderOpponents();
                CenterArea.Visibility = Visibility.Collapsed;
                DirectionIcon.Visibility = Visibility.Collapsed;
                CurrentColorIndicator.Visibility = Visibility.Collapsed;
                return;
            }

            StartGameBtn.Visibility = Visibility.Collapsed;
            CenterArea.Visibility = Visibility.Visible;
            DirectionIcon.Visibility = Visibility.Visible;
            CurrentColorIndicator.Visibility = Visibility.Visible;

            DirectionIcon.Text = _lastPkt.IsClockwise ? "↻" : "↺";
            StatusText.Text = _lastPkt.CurrentTurn == _state.MyUsername ? "Your turn!" : $"{_lastPkt.PlayerDisplayNames.GetValueOrDefault(_lastPkt.CurrentTurn, _lastPkt.CurrentTurn)}'s turn";

            RenderOpponents();
            RenderCenterPile();
            RenderMyHand();

            // Highlight current player
            foreach (var kv in _playerNodes)
            {
                var border = kv.Value;
                if (kv.Key == _lastPkt.CurrentTurn)
                {
                    border.BorderBrush = new SolidColorBrush(Colors.Yellow);
                    border.BorderThickness = new Thickness(4);
                }
                else
                {
                    border.BorderBrush = new SolidColorBrush(Colors.White);
                    border.BorderThickness = new Thickness(2);
                }
            }

            // Highlight myself if it's my turn
            MyHandPanel.Opacity = _lastPkt.CurrentTurn == _state.MyUsername ? 1.0 : 0.6;

            UpdateColorIndicator();
        }

        private void UpdateColorIndicator()
        {
            Color c = Colors.Transparent;
            switch (_lastPkt.CurrentColor)
            {
                case UnoColor.Red: c = Colors.Red; break;
                case UnoColor.Blue: c = Colors.DodgerBlue; break;
                case UnoColor.Green: c = Colors.LimeGreen; break;
                case UnoColor.Yellow: c = Colors.Yellow; break;
            }
            CurrentColorIndicator.Background = new SolidColorBrush(c);
        }

        private void RenderOpponents()
        {
            // Clear existing opponents from canvas
            var toRemove = TableCanvas.Children.OfType<Border>().Where(b => b.Tag as string == "Opponent").ToList();
            foreach (var b in toRemove) TableCanvas.Children.Remove(b);
            _playerNodes.Clear();

            var opponents = _lastPkt.Players.Where(p => p != _state.MyUsername).ToList();
            if (opponents.Count == 0) return;

            // Semi-circle arrangement
            double startAngle = Math.PI;
            double endAngle = 0;
            double step = opponents.Count > 1 ? (Math.PI / (opponents.Count + 1)) : (Math.PI / 2);

            double radiusX = 350;
            double radiusY = 200;

            for (int i = 0; i < opponents.Count; i++)
            {
                var opp = opponents[i];
                double angle = startAngle - (i + 1) * step; // counter-clockwise from PI

                double x = CenterX + radiusX * Math.Cos(angle);
                double y = CenterY - radiusY * Math.Sin(angle);

                var node = CreatePlayerNode(opp);

                Canvas.SetLeft(node, x - 50); // node width is 100
                Canvas.SetTop(node, y - 50); // node height is ~100
                Canvas.SetZIndex(node, 10);

                TableCanvas.Children.Add(node);
                _playerNodes[opp] = node;
            }
        }

        private Border CreatePlayerNode(string username)
        {
            var border = new Border
            {
                Width = 100,
                Height = 120,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                Tag = "Opponent"
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var avatarBorder = new Border
            {
                Width = 50,
                Height = 50,
                CornerRadius = new CornerRadius(25),
                Background = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
                ClipToBounds = true
            };

            if (_profilePics.TryGetValue(username, out var img) && img != null)
            {
                avatarBorder.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
            }
            else
            {
                avatarBorder.Child = new TextBlock { Text = "🙂", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }

            stack.Children.Add(avatarBorder);

            stack.Children.Add(new TextBlock
            {
                Text = _lastPkt.PlayerDisplayNames.GetValueOrDefault(username, username),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // Card count
            if (_lastPkt.GameStarted)
            {
                var pInfo = _lastPkt.GamePlayers.FirstOrDefault(p => p.Username == username);
                int count = pInfo?.CardCount ?? 0;

                stack.Children.Add(new TextBlock
                {
                    Text = $"{count} Cards",
                    Foreground = Brushes.LightGray,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            border.Child = stack;
            return border;
        }

        private void RenderCenterPile()
        {
            DiscardPileUI.Child = null;
            if (_lastPkt.TopCard != null)
            {
                var cardUI = CreateCardVisual(_lastPkt.TopCard);
                DiscardPileUI.Child = cardUI;
            }
        }

        private void RenderMyHand()
        {
            MyHandPanel.Children.Clear();
            var myInfo = _lastPkt.GamePlayers.FirstOrDefault(p => p.Username == _state.MyUsername);
            if (myInfo?.Hand == null) return;

            foreach (var card in myInfo.Hand.OrderBy(c => c.Color).ThenBy(c => c.Value))
            {
                var visual = CreateCardVisual(card, isInteractive: true);
                visual.Margin = new Thickness(2, 0, 2, 0);

                visual.MouseEnter += (s, e) =>
                {
                    visual.RenderTransform = new TranslateTransform(0, -20);
                };
                visual.MouseLeave += (s, e) =>
                {
                    visual.RenderTransform = new TranslateTransform(0, 0);
                };

                visual.MouseLeftButtonUp += (s, e) => Card_Click(card);
                MyHandPanel.Children.Add(visual);
            }
        }

        private Border CreateCardVisual(UnoCard card, bool isInteractive = false)
        {
            Color bg = Colors.Black;
            switch (card.Color)
            {
                case UnoColor.Red: bg = Colors.Red; break;
                case UnoColor.Blue: bg = Colors.DodgerBlue; break;
                case UnoColor.Green: bg = Colors.LimeGreen; break;
                case UnoColor.Yellow: bg = Colors.Goldenrod; break; // bit darker than pure yellow
                case UnoColor.None: bg = Colors.Black; break;
            }

            string text = "";
            switch (card.Value)
            {
                case UnoValue.Skip: text = "⊘"; break;
                case UnoValue.Reverse: text = "⇄"; break;
                case UnoValue.DrawTwo: text = "+2"; break;
                case UnoValue.Wild: text = "WILD"; break;
                case UnoValue.WildDrawFour: text = "+4"; break;
                default: text = ((int)card.Value).ToString(); break;
            }

            var border = new Border
            {
                Width = 70,
                Height = 100,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(bg),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(3),
                Cursor = isInteractive ? Cursors.Hand : Cursors.Arrow,
                RenderTransform = new TranslateTransform(0, 0)
            };

            // Inner white oval for aesthetics
            var oval = new Border
            {
                CornerRadius = new CornerRadius(15),
                Background = Brushes.White,
                Margin = new Thickness(5, 10, 5, 10),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            oval.RenderTransform = new RotateTransform(15);

            var tb = new TextBlock
            {
                Text = text,
                FontSize = text.Length > 2 ? 14 : 24,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(bg == Colors.Black ? Colors.Black : bg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            tb.RenderTransform = new RotateTransform(-15);

            oval.Child = tb;

            // Small corner indicators
            var grid = new Grid();
            grid.Children.Add(oval);

            var tl = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var br = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };

            grid.Children.Add(tl);
            grid.Children.Add(br);

            border.Child = grid;

            border.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, Opacity = 0.5 };

            return border;
        }

        private void StartGame_Click(object sender, RoutedEventArgs e)
        {
            _ = _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
            {
                Msg = UnoMsgType.StartGame,
                LobbyId = LobbyId,
                From = _state.MyUsername
            }));
        }

        private void Deck_Click(object sender, MouseButtonEventArgs e)
        {
            if (_lastPkt.CurrentTurn != _state.MyUsername) return;

            _ = _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
            {
                Msg = UnoMsgType.DrawCard,
                LobbyId = LobbyId,
                From = _state.MyUsername
            }));
        }

        private void Card_Click(UnoCard card)
        {
            if (_lastPkt.CurrentTurn != _state.MyUsername) return;

            // Simple validation
            bool valid = card.Color == UnoColor.None ||
                         card.Color == _lastPkt.CurrentColor ||
                         card.Value == _lastPkt.TopCard?.Value;

            if (!valid)
            {
                MessageBox.Show("You can't play that card.", "Invalid Move", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (card.Color == UnoColor.None)
            {
                // Must pick color. Show UI.
                ColorPickerUI.Visibility = Visibility.Visible;
                ColorPickerUI.Tag = card; // Save card to play
                return;
            }

            PlayCard(card);
        }

        private void PlayCard(UnoCard card)
        {
            _ = _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
            {
                Msg = UnoMsgType.PlayCard,
                LobbyId = LobbyId,
                From = _state.MyUsername,
                PlayedCard = card
            }));
        }

        private void ColorPicker_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string cStr && Enum.TryParse<UnoColor>(cStr, out var c))
            {
                ColorPickerUI.Visibility = Visibility.Collapsed;
                var cardToPlay = ColorPickerUI.Tag as UnoCard;

                if (cardToPlay != null)
                {
                    PlayCard(cardToPlay);

                    // We must also send a ChooseColor packet shortly after or during
                    // The server handles playcard -> wild -> awaits ChooseColor.
                    // Wait, our UnoManager requires it as a two step process. First play, then wait for PendingColorChoice?
                    // Yes, we will just send PlayCard, and UnoManager will set PendingColorChoice = true and broadcast.
                    // Instead of a separate state, I will just send Both simultaneously if the protocol permits,
                    // but protocol is PlayCard then ChooseColor.

                    Task.Delay(100).ContinueWith(_ =>
                    {
                        _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
                        {
                            Msg = UnoMsgType.ChooseColor,
                            LobbyId = LobbyId,
                            From = _state.MyUsername,
                            ChosenColor = c
                        }));
                    });
                }
            }
        }

        private void AnimateCardPlay(string fromPlayer, UnoCard card, Action onComplete)
        {
            // Create a virtual card
            var animCard = CreateCardVisual(card);
            TableCanvas.Children.Add(animCard);
            Canvas.SetZIndex(animCard, 100);

            double startX, startY;
            double endX = CenterX + 60;
            double endY = CenterY - 25;

            if (fromPlayer == _state.MyUsername)
            {
                startX = CenterX - 35;
                startY = 600; // Bottom of screen
            }
            else
            {
                if (_playerNodes.TryGetValue(fromPlayer, out var node))
                {
                    startX = Canvas.GetLeft(node) + node.Width / 2 - 35;
                    startY = Canvas.GetTop(node) + node.Height / 2 - 50;
                }
                else
                {
                    startX = CenterX - 35; startY = 0; // Top
                }
            }

            Canvas.SetLeft(animCard, startX);
            Canvas.SetTop(animCard, startY);

            var tt = new TranslateTransform(0, 0);
            animCard.RenderTransform = tt;

            var dX = endX - startX;
            var dY = endY - startY;

            var animX = new DoubleAnimation(0, dX, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var animY = new DoubleAnimation(0, dY, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

            animX.Completed += (s, e) =>
            {
                TableCanvas.Children.Remove(animCard);
                onComplete?.Invoke();
            };

            tt.BeginAnimation(TranslateTransform.XProperty, animX);
            tt.BeginAnimation(TranslateTransform.YProperty, animY);
        }

        protected override void OnClosed(EventArgs e)
        {
            _state.Net.PacketReceived -= OnPacket;
            _ = _state.Net.SendAsync(Packet.Create(PacketType.Uno, new UnoPacket
            {
                Msg = UnoMsgType.LeaveLobby,
                LobbyId = LobbyId,
                From = _state.MyUsername
            }));
            base.OnClosed(e);
        }
    }
}
