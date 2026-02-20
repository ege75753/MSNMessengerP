using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MSNShared;

namespace MSNClient
{
    public partial class BlackjackWindow : Window
    {
        private readonly ClientState _state = App.State;
        private BlackjackPacket _lobby;
        private string _myUsername;
        private bool _isHost;
        private int _currentBet;
        private readonly List<int> _placedChips = new();
        private readonly Dictionary<string, TextBlock> _handTotals = new();

        // Seat angles: index 0 = me (bottom), rest clockwise
        private static readonly double[] SeatAngles = { 90, 38, 0, 322, 218, 180, 142 };

        // Per-seat UI references
        private readonly Dictionary<string, FrameworkElement> _seatPanels = new();
        private readonly Dictionary<string, Panel> _cardAreas = new();
        private readonly Dictionary<string, TextBlock> _balanceTexts = new();
        private readonly Dictionary<string, TextBlock> _betTexts = new();
        private Panel? _dealerCardArea;

        // Deck position (center top, where cards fly from)
        private Point _deckPos;
        private Point _tableCenter;
        private double _rxOuter, _ryOuter;

        public BlackjackWindow(BlackjackPacket initial)
        {
            InitializeComponent();
            _myUsername = App.State.MyUsername;
            _lobby = initial;
            _state.Net.PacketReceived += OnPacket;
            Closed += (_, _) => _state.Net.PacketReceived -= OnPacket;
        }

        // ── Layout ───────────────────────────────────────────────────────────────

        private void TableCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BuildCanvas();
        }

        private void BuildCanvas()
        {
            TableCanvas.Children.Clear();
            _seatPanels.Clear();
            _cardAreas.Clear();
            _balanceTexts.Clear();
            _betTexts.Clear();
            _handTotals.Clear();
            _dealerCardArea = null;

            double w = TableCanvas.ActualWidth, h = TableCanvas.ActualHeight;
            if (w < 100 || h < 100) return;

            _tableCenter = new Point(w / 2, h / 2 - 10);
            _rxOuter = Math.Min(w * 0.44, h * 0.78);
            _ryOuter = _rxOuter * 0.54;
            double rxInner = _rxOuter - 14, ryInner = _ryOuter - 10;

            // ── Draw shadow ring ──
            var shadow = new Ellipse
            {
                Width = (_rxOuter + 20) * 2,
                Height = (_ryOuter + 20) * 2,
                Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0))
            };
            Canvas.SetLeft(shadow, _tableCenter.X - shadow.Width / 2 + 6);
            Canvas.SetTop(shadow, _tableCenter.Y - shadow.Height / 2 + 6);
            TableCanvas.Children.Add(shadow);

            // ── Outer rim ──
            var rim = new Ellipse
            {
                Width = _rxOuter * 2,
                Height = _ryOuter * 2,
                Fill = new SolidColorBrush(Color.FromRgb(70, 40, 20)),
                Stroke = new SolidColorBrush(Color.FromRgb(120, 80, 30)),
                StrokeThickness = 4
            };
            Canvas.SetLeft(rim, _tableCenter.X - _rxOuter);
            Canvas.SetTop(rim, _tableCenter.Y - _ryOuter);
            TableCanvas.Children.Add(rim);

            // ── Felt ──
            var felt = new Ellipse
            {
                Width = rxInner * 2,
                Height = ryInner * 2,
                Fill = new RadialGradientBrush(
                    Color.FromRgb(30, 105, 55),
                    Color.FromRgb(14, 70, 35))
            };
            Canvas.SetLeft(felt, _tableCenter.X - rxInner);
            Canvas.SetTop(felt, _tableCenter.Y - ryInner);
            TableCanvas.Children.Add(felt);

            // ── Inner decorative ring ──
            var ring = new Ellipse
            {
                Width = rxInner * 1.55,
                Height = ryInner * 1.55,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(ring, _tableCenter.X - rxInner * 0.775);
            Canvas.SetTop(ring, _tableCenter.Y - ryInner * 0.775);
            TableCanvas.Children.Add(ring);

            // ── POT display in center ──
            var potBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 4, 12, 4)
            };
            var potStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var potLabel = new TextBlock { Text = "POT", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), HorizontalAlignment = HorizontalAlignment.Center };
            var potValue = new TextBlock { Text = $"${_lobby.Pot}", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)), HorizontalAlignment = HorizontalAlignment.Center };
            potValue.Tag = "pot";
            potStack.Children.Add(potLabel);
            potStack.Children.Add(potValue);
            potBorder.Child = potStack;
            Canvas.SetLeft(potBorder, _tableCenter.X - 55);
            Canvas.SetTop(potBorder, _tableCenter.Y - 24);
            TableCanvas.Children.Add(potBorder);

            // ── Deck position (where cards "come from") ──
            _deckPos = new Point(_tableCenter.X, _tableCenter.Y - _ryOuter * 0.35);

            // ── Dealer seat (top of oval) ──
            var dealerPos = GetSeatPoint(270); // 270° = top
            AddDealerPanel(dealerPos);

            // ── Player seats ──
            var players = _lobby.Players;
            int myIdx = players.IndexOf(_myUsername);
            if (myIdx < 0) myIdx = 0;

            for (int si = 0; si < players.Count; si++)
            {
                // Rotate so "me" is always at seat 0
                int angleIdx = ((si - myIdx) % players.Count + players.Count) % players.Count;
                var pos = GetSeatPoint(SeatAngles[Math.Min(angleIdx, SeatAngles.Length - 1)]);
                AddPlayerPanel(players[si], pos, si == myIdx);
                AddBettingCircle(players[si], pos);  // dashed circle on the felt
            }

            // ── Populate with current game data ──
            RefreshScores();
            if (_lobby.GameStarted || _lobby.DealerHand.Count > 0)
            {
                PopulateDealerCards(animate: false);
                PopulatePlayerCards(animate: false);
            }
            UpdateFooter();
        }

        // ── Seat position math ───────────────────────────────────────────────────

        private Point GetSeatPoint(double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(
                _tableCenter.X + _rxOuter * 1.02 * Math.Cos(rad),
                _tableCenter.Y + _ryOuter * 1.05 * Math.Sin(rad));
        }

        // ── Dealer panel ─────────────────────────────────────────────────────────

        private void AddDealerPanel(Point pos)
        {
            var outer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var namePlate = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(210, 20, 20, 60)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(10, 3, 10, 3)
            };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            nameRow.Children.Add(new TextBlock { Text = "🤖 ", FontSize = 13 });
            nameRow.Children.Add(new TextBlock { Text = "DEALER", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)) });
            namePlate.Child = nameRow;
            outer.Children.Add(namePlate);

            var cardRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            _dealerCardArea = cardRow;
            outer.Children.Add(cardRow);

            if (_lobby.DealerHand.Count > 0)
                foreach (var c in _lobby.DealerHand) cardRow.Children.Add(MakeCard(c, false));

            Canvas.SetLeft(outer, pos.X - 65);
            Canvas.SetTop(outer, pos.Y - 40);
            TableCanvas.Children.Add(outer);
        }

        // ── Player panel ─────────────────────────────────────────────────────────

        private void AddPlayerPanel(string username, Point pos, bool isMe)
        {
            var display = _lobby.PlayerDisplayNames.GetValueOrDefault(username, username);
            var balance = _lobby.Balances.GetValueOrDefault(username, 1000);
            var bet = _lobby.Bets.GetValueOrDefault(username, 0);
            var result = _lobby.Results?.GetValueOrDefault(username);

            Color plateColor = isMe
                ? Color.FromArgb(220, 20, 80, 30)
                : Color.FromArgb(200, 20, 40, 80);

            bool isCurrent = _lobby.CurrentPlayer == username && _lobby.GameStarted;
            if (isCurrent) plateColor = Color.FromArgb(230, 100, 80, 0);

            var outer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            // Card area ABOVE name plate
            var cardRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
            _cardAreas[username] = cardRow;
            outer.Children.Add(cardRow);

            // Hand total (big, readable number)
            var htLbl = new TextBlock
            {
                Text = "",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            _handTotals[username] = htLbl;
            if (_lobby.Hands.TryGetValue(username, out var initHand) && initHand.Count > 0)
                UpdateHandTotal(htLbl, initHand);
            outer.Children.Add(htLbl);

            // Name + balance plate
            var plate = new Border
            {
                Background = new SolidColorBrush(plateColor),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 100
            };

            var plateStack = new StackPanel();
            var nameText = new TextBlock
            {
                Text = (isMe ? "⭐ " : "") + display,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = isMe ? new SolidColorBrush(Color.FromRgb(120, 255, 140)) : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            plateStack.Children.Add(nameText);

            var balText = new TextBlock
            {
                Text = $"${balance}",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _balanceTexts[username] = balText;
            plateStack.Children.Add(balText);

            if (bet > 0 || _lobby.GameStarted)
            {
                var betText = new TextBlock
                {
                    Text = bet > 0 ? $"Bet: ${bet}" : "",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 180, 100)),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                _betTexts[username] = betText;
                plateStack.Children.Add(betText);
            }

            // Result badge
            if (!string.IsNullOrEmpty(result))
            {
                var resultText = new TextBlock
                {
                    Text = result switch
                    {
                        "Win" => "✅ WIN",
                        "Blackjack" => "🃏 BLACKJACK!",
                        "Push" => "🤝 PUSH",
                        "Bust" => "💥 BUST",
                        _ => "❌ LOSE"
                    },
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = result switch
                    {
                        "Win" or "Blackjack" => new SolidColorBrush(Color.FromRgb(80, 255, 120)),
                        "Push" => new SolidColorBrush(Color.FromRgb(255, 230, 100)),
                        _ => new SolidColorBrush(Color.FromRgb(255, 80, 80))
                    },
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                plateStack.Children.Add(resultText);
            }

            plate.Child = plateStack;
            if (isCurrent)
            {
                plate.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 215, 0));
                plate.BorderThickness = new Thickness(2);
            }
            outer.Children.Add(plate);

            _seatPanels[username] = outer;

            // Offset so panel is centered on pos
            Canvas.SetLeft(outer, pos.X - 55);
            Canvas.SetTop(outer, pos.Y - 70);
            TableCanvas.Children.Add(outer);
        }

        // ── Card rendering ───────────────────────────────────────────────────────

        private static Border MakeCard(BjCard card, bool small = false)
        {
            double w = small ? 36 : 44, h = small ? 52 : 63;
            var border = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1.2),
                Margin = new Thickness(2, 0, 2, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 2, BlurRadius = 4, Opacity = 0.5 }
            };
            if (card.Hidden)
            {
                border.Background = new LinearGradientBrush(Color.FromRgb(20, 60, 140), Color.FromRgb(10, 30, 80), 45);
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(60, 100, 200));
                border.Child = new TextBlock { Text = "🂠", FontSize = small ? 22 : 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(80, 130, 220)) };
            }
            else
            {
                bool red = card.Suit is "♥" or "♦";
                var suitColor = red ? Color.FromRgb(200, 0, 0) : Color.FromRgb(20, 20, 20);
                border.Background = Brushes.White;
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(190, 190, 190));
                var g = new Grid();
                g.Children.Add(new TextBlock { Text = card.Rank, FontSize = small ? 8 : 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 1, 0, 0), Foreground = new SolidColorBrush(suitColor) });
                g.Children.Add(new TextBlock { Text = card.Suit, FontSize = small ? 14 : 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(suitColor) });
                g.Children.Add(new TextBlock { Text = card.Rank, FontSize = small ? 8 : 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 1), Foreground = new SolidColorBrush(suitColor), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(180) });
                border.Child = g;
            }
            return border;
        }

        // ── Card population + animation ───────────────────────────────────────────

        private void PopulateDealerCards(bool animate)
        {
            if (_dealerCardArea == null) return;
            _dealerCardArea.Children.Clear();
            for (int i = 0; i < _lobby.DealerHand.Count; i++)
                AddCardAnimated(_dealerCardArea, _lobby.DealerHand[i], animate ? i : -1, isSmall: false);
        }

        private void PopulatePlayerCards(bool animate)
        {
            foreach (var player in _lobby.Players)
            {
                if (!_cardAreas.TryGetValue(player, out var area)) continue;
                area.Children.Clear();
                if (_lobby.Hands.TryGetValue(player, out var hand))
                    for (int i = 0; i < hand.Count; i++)
                        AddCardAnimated(area, hand[i], animate ? i * _lobby.Players.Count : -1, isSmall: false);
            }
        }

        private void AddCardAnimated(Panel area, BjCard card, int delaySlot, bool isSmall)
        {
            var cardElem = MakeCard(card, isSmall);
            area.Children.Add(cardElem);
            if (delaySlot < 0) return;

            // Flip-in animation: scaleX 0→1 with delay
            var st = new ScaleTransform(0, 1);
            cardElem.RenderTransformOrigin = new Point(0.5, 0.5);
            cardElem.RenderTransform = st;
            var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(280)))
            {
                BeginTime = TimeSpan.FromMilliseconds(delaySlot * 350),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        }

        // ── Chip stack visual ────────────────────────────────────────────────────

        private static FrameworkElement BuildChipStack(int amount)
        {
            // Determine chip colour from dominant denomination
            var chipColor = amount >= 100 ? Color.FromRgb(228, 120, 30)
                          : amount >= 50 ? Color.FromRgb(130, 45, 180)
                          : amount >= 25 ? Color.FromRgb(30, 165, 70)
                          : amount >= 10 ? Color.FromRgb(30, 110, 210)
                          : Color.FromRgb(200, 40, 40);

            int layers = Math.Clamp(amount / 25 + 1, 1, 7);
            double chipW = 42, chipH = 13;
            double totalH = chipH + (layers - 1) * 4 + 16;

            var canvas = new Canvas
            {
                Width = chipW + 10,
                Height = totalH,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Draw chips bottom → top
            for (int i = layers - 1; i >= 0; i--)
            {
                double y = (layers - 1 - i) * 4;
                // Shadow
                var shadow = new Ellipse
                {
                    Width = chipW,
                    Height = chipH,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0))
                };
                Canvas.SetLeft(shadow, 5 + 1); Canvas.SetTop(shadow, y + 2);
                canvas.Children.Add(shadow);
                // Body
                var body = new Ellipse
                {
                    Width = chipW,
                    Height = chipH,
                    Fill = new SolidColorBrush(chipColor)
                };
                Canvas.SetLeft(body, 5); Canvas.SetTop(body, y);
                canvas.Children.Add(body);
                // Inner ring
                var ring = new Ellipse
                {
                    Width = chipW - 8,
                    Height = chipH - 4,
                    Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    StrokeThickness = 1,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(ring, 9); Canvas.SetTop(ring, y + 2);
                canvas.Children.Add(ring);
            }

            // Amount label on top chip
            var lbl = new TextBlock
            {
                Text = $"${amount}",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var lblBorder = new Border
            {
                Child = lbl,
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0)
            };
            Canvas.SetLeft(lblBorder, 3); Canvas.SetTop(lblBorder, totalH - 15);
            canvas.Children.Add(lblBorder);

            return new Border
            {
                Child = canvas,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        // ── Scores panel ─────────────────────────────────────────────────────────

        private void RefreshScores()
        {
            ScoresPanel.Children.Clear();
            foreach (var p in _lobby.Players)
            {
                var name = _lobby.PlayerDisplayNames.GetValueOrDefault(p, p);
                var wins = _lobby.Scores.GetValueOrDefault(p, 0);
                var bal = _lobby.Balances.GetValueOrDefault(p, 1000);
                ScoresPanel.Children.Add(new TextBlock
                {
                    Text = $"  {name}: ${bal} ({wins}W)",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(p == _myUsername ? Color.FromRgb(120, 255, 140) : Color.FromRgb(160, 190, 160)),
                    FontWeight = p == _myUsername ? FontWeights.Bold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        // ── Footer state machine ──────────────────────────────────────────────────

        private void UpdateFooter()
        {
            _isHost = _lobby.Host == _myUsername;
            bool gameOn = _lobby.GameStarted;

            LobbyPanel.Visibility = (!gameOn && !_isBetting) ? Visibility.Visible : Visibility.Collapsed;
            BettingPanel.Visibility = _isBetting ? Visibility.Visible : Visibility.Collapsed;
            ActionPanel.Visibility = (gameOn && !_isBetting) ? Visibility.Visible : Visibility.Collapsed;

            StartBtn.Visibility = (_isHost && !gameOn && !_isBetting) ? Visibility.Visible : Visibility.Collapsed;
            NextRoundBtn.Visibility = (!gameOn && !_isBetting && _roundOver && _isHost) ? Visibility.Visible : Visibility.Collapsed;

            LobbyStatusText.Text = _isHost
                ? $"You're the host — {_lobby.Players.Count} at the table. Press Start when ready!"
                : "Waiting for host to start...";

            if (gameOn)
            {
                bool myTurn = _lobby.CurrentPlayer == _myUsername;
                HitBtn.IsEnabled = myTurn;
                StandBtn.IsEnabled = myTurn;
                TurnText.Text = myTurn ? "🎯 Your turn!"
                    : (!string.IsNullOrEmpty(_lobby.CurrentPlayer)
                        ? $"⏳ {_lobby.PlayerDisplayNames.GetValueOrDefault(_lobby.CurrentPlayer, _lobby.CurrentPlayer)}'s turn..."
                        : "");
                TurnText.Foreground = myTurn
                    ? new SolidColorBrush(Color.FromRgb(255, 230, 50))
                    : new SolidColorBrush(Color.FromRgb(120, 200, 120));
            }

            TitleText.Text = $"🃏 {_lobby.LobbyName}";
            StatusText.Text = gameOn
                ? $"Pot: ${_lobby.Pot}  •  Round in progress"
                : (_isBetting ? "Place your bet then click Deal Me In" : $"{_lobby.Players.Count}/{_lobby.MaxPlayers} players");
        }

        private bool _isBetting;
        private bool _roundOver;

        // ── Network ───────────────────────────────────────────────────────────────

        private void OnPacket(Packet pkt)
        {
            if (pkt.Type != PacketType.Blackjack) return;
            var bp = pkt.GetData<BlackjackPacket>();
            if (bp == null || bp.LobbyId != _lobby.LobbyId) return;

            Dispatcher.Invoke(() =>
            {
                switch (bp.Msg)
                {
                    case BlackjackMsgType.LobbyState:
                        _lobby = bp;
                        if (bp.GameStarted)
                        {
                            // Game has started — clear betting state, show the table
                            _isBetting = false;
                            _roundOver = false;
                            BuildCanvas();
                        }
                        else if (_isBetting)
                        {
                            // Mid-betting update: another player placed a bet — refresh chips but stay in betting
                            BuildCanvas();
                            int waiting = bp.Players.Count - bp.Bets.Count;
                            LobbyStatusText.Text = waiting > 0
                                ? $"Waiting for {waiting} more player(s) to bet..."
                                : "All bets placed! Dealing cards...";
                        }
                        else
                        {
                            // Lobby state (pre-game, not betting)
                            _roundOver = false;
                            BuildCanvas();
                        }
                        break;

                    case BlackjackMsgType.BettingPhase:
                        _isBetting = true;
                        _roundOver = false;
                        _currentBet = 0;
                        _placedChips.Clear();
                        BetAmountText.Text = "$0";
                        DealMeInBtn.IsEnabled = true;
                        UpdateChipPreview();
                        // ── Clear last round's cards + results ──
                        _lobby.Hands.Clear();
                        _lobby.DealerHand.Clear();
                        if (_lobby.Results != null) _lobby.Results.Clear();
                        _lobby.Bets.Clear();
                        // Update from server
                        foreach (var kv in bp.Balances) _lobby.Balances[kv.Key] = kv.Value;
                        _lobby.Players.Clear();
                        foreach (var p in bp.Players) _lobby.Players.Add(p);
                        _lobby.PlayerDisplayNames = bp.PlayerDisplayNames;
                        BuildCanvas();
                        break;

                    case BlackjackMsgType.HandUpdate:
                        foreach (var kv in bp.Hands) _lobby.Hands[kv.Key] = kv.Value;
                        _lobby.CurrentPlayer = bp.CurrentPlayer;
                        if (_cardAreas.TryGetValue(bp.From, out var area) && _lobby.Hands.TryGetValue(bp.From, out var hand))
                        {
                            area.Children.Clear();
                            foreach (var c in hand) AddCardAnimated(area, c, -1, false);
                            if (area.Children.Count > 0)
                                AnimateCardIn((FrameworkElement)area.Children[^1]);
                        }
                        // Update hand total display
                        if (_handTotals.TryGetValue(bp.From, out var htLbl2) && _lobby.Hands.TryGetValue(bp.From, out var htHand))
                            UpdateHandTotal(htLbl2, htHand);
                        UpdateFooter();
                        break;

                    case BlackjackMsgType.DealerTurn:
                        _lobby.DealerHand = bp.DealerHand;
                        PopulateDealerCards(animate: true);
                        StatusText.Text = $"Dealer: {HandValue(_lobby.DealerHand)}";
                        break;

                    case BlackjackMsgType.RoundResult:
                        _lobby.Results = bp.Results;
                        _lobby.Scores = bp.Scores;
                        _lobby.Balances = bp.Balances;
                        _lobby.GameStarted = false;
                        _lobby.DealerHand = bp.DealerHand;
                        _isBetting = false;
                        _roundOver = true;
                        BuildCanvas();
                        break;
                }
            });
        }

        private static void AnimateCardIn(FrameworkElement card)
        {
            var st = new ScaleTransform(0, 1);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = st;
            var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        }

        private static int HandValue(List<BjCard> hand)
        {
            int total = 0, aces = 0;
            foreach (var c in hand.Where(c => !c.Hidden))
            {
                if (c.Rank == "A") { total += 11; aces++; }
                else if (c.Rank is "J" or "Q" or "K") total += 10;
                else if (int.TryParse(c.Rank, out var v)) total += v;
            }
            while (total > 21 && aces > 0) { total -= 10; aces--; }
            return total;
        }

        // ── Chip betting ──────────────────────────────────────────────────────────

        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var chip))
            {
                var myBalance = _lobby.Balances.GetValueOrDefault(_myUsername, 1000);
                if (_placedChips.Sum() + chip <= myBalance)
                    _placedChips.Add(chip);
                // (silently ignore when over balance)
                _currentBet = _placedChips.Sum();
                BetAmountText.Text = $"${_currentBet}";
                UpdateChipPreview();
            }
        }

        private void BetClear_Click(object sender, RoutedEventArgs e)
        {
            _placedChips.Clear();
            _currentBet = 0;
            BetAmountText.Text = "$0";
            UpdateChipPreview();
        }

        // ── Chip/hand helpers ────────────────────────────────────────────────────

        private void UpdateChipPreview()
        {
            ChipPreviewPanel.Children.Clear();
            foreach (var chip in _placedChips)
            {
                var color = GetChipColor(chip);
                var cv = new Canvas { Width = 24, Height = 24, Margin = new Thickness(1, 0, 1, 0) };
                cv.Children.Add(new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(color) });
                cv.Children.Add(new Ellipse { Width = 17, Height = 17, Fill = Brushes.Transparent, Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), StrokeThickness = 1, Margin = new Thickness(3.5) });
                ChipPreviewPanel.Children.Add(cv);
            }
        }

        private static void UpdateHandTotal(TextBlock lbl, List<BjCard> hand)
        {
            int total = HandValue(hand);
            bool bust = total > 21;
            lbl.Text = bust ? "💥 BUST!" : $"Total: {total}";
            lbl.Foreground = bust ? new SolidColorBrush(Color.FromRgb(255, 70, 70))
                : total == 21 ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(160, 255, 170));
        }

        private void AddBettingCircle(string username, Point seatPos)
        {
            // Place the circle between seat and table center (40% toward center)
            double t = 0.40;
            double bx = seatPos.X + (_tableCenter.X - seatPos.X) * t;
            double by = seatPos.Y + (_tableCenter.Y - seatPos.Y) * t;
            const double r = 28;

            var bet = _lobby.Bets.GetValueOrDefault(username, 0);
            bool hasBet = bet > 0;

            // Dashed betting zone circle
            var zone = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Stroke = new SolidColorBrush(hasBet
                    ? Color.FromRgb(220, 200, 80)
                    : Color.FromArgb(110, 180, 175, 100)),
                StrokeThickness = hasBet ? 2.5 : 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                Fill = hasBet
                    ? new SolidColorBrush(Color.FromArgb(55, 200, 170, 0))
                    : Brushes.Transparent
            };
            Canvas.SetLeft(zone, bx - r); Canvas.SetTop(zone, by - r);
            TableCanvas.Children.Add(zone);

            if (!hasBet) return;

            // One colored chip disc inside the circle
            var disc = new Ellipse
            {
                Width = 32,
                Height = 32,
                Fill = new SolidColorBrush(GetChipColor(bet)),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                StrokeThickness = 2
            };
            Canvas.SetLeft(disc, bx - 16); Canvas.SetTop(disc, by - 16);
            TableCanvas.Children.Add(disc);

            var inner = new Ellipse { Width = 23, Height = 23, Fill = Brushes.Transparent, Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), StrokeThickness = 1 };
            Canvas.SetLeft(inner, bx - 11.5); Canvas.SetTop(inner, by - 11.5);
            TableCanvas.Children.Add(inner);

            // Amount label
            var amtLbl = new TextBlock { Text = $"${bet}", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            amtLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(amtLbl, bx - amtLbl.DesiredSize.Width / 2);
            Canvas.SetTop(amtLbl, by - 7);
            TableCanvas.Children.Add(amtLbl);
        }

        private static Color GetChipColor(int amount) =>
              amount >= 100 ? Color.FromRgb(228, 120, 30)
            : amount >= 50 ? Color.FromRgb(130, 45, 180)
            : amount >= 25 ? Color.FromRgb(30, 165, 70)
            : amount >= 10 ? Color.FromRgb(30, 110, 210)
            : Color.FromRgb(200, 40, 40);

        private async void DealMeIn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentBet < 5) _currentBet = 5; // minimum bet
            DealMeInBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            {
                Msg = BlackjackMsgType.PlaceBet,
                LobbyId = _lobby.LobbyId,
                BetAmount = _currentBet
            }));
        }

        // ── Game actions ──────────────────────────────────────────────────────────

        private async void Hit_Click(object sender, RoutedEventArgs e)
        {
            HitBtn.IsEnabled = false; StandBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            { Msg = BlackjackMsgType.PlayerAction, LobbyId = _lobby.LobbyId, Action = BjAction.Hit }));
        }

        private async void Stand_Click(object sender, RoutedEventArgs e)
        {
            HitBtn.IsEnabled = false; StandBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            { Msg = BlackjackMsgType.PlayerAction, LobbyId = _lobby.LobbyId, Action = BjAction.Stand }));
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            StartBtn.IsEnabled = false;
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            { Msg = BlackjackMsgType.StartGame, LobbyId = _lobby.LobbyId }));
        }

        private async void NextRound_Click(object sender, RoutedEventArgs e)
        {
            NextRoundBtn.Visibility = Visibility.Collapsed;
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            { Msg = BlackjackMsgType.NextRound, LobbyId = _lobby.LobbyId }));
        }

        private async void Leave_Click(object sender, RoutedEventArgs e)
        {
            await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
            { Msg = BlackjackMsgType.LeaveLobby, LobbyId = _lobby.LobbyId }));
            Close();
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _state.Net.PacketReceived -= OnPacket;
            try
            {
                await _state.Net.SendAsync(Packet.Create(PacketType.Blackjack, new BlackjackPacket
                { Msg = BlackjackMsgType.LeaveLobby, LobbyId = _lobby.LobbyId }));
            }
            catch { }
        }
    }
}
