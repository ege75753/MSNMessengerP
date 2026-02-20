using MSNShared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MSNClient.Windows
{
    public partial class PaintIoWindow : Window
    {
        private readonly ClientState _state = App.State;
        private int _mapWidth;
        private int _mapHeight;
        private Rectangle[,] _grid = new Rectangle[0, 0];

        // Per-player data
        private Dictionary<string, PaintIoPlayer> _players = new();
        private Dictionary<string, Brush> _playerBrushes = new();
        private Dictionary<string, Brush> _trailBrushes = new();
        private Dictionary<string, FrameworkElement> _playerElements = new();

        // My own last known score (for death screen)
        private int _myLastScore = 0;
        private string _myUsername => _state.MyUsername;

        // Brushes
        private readonly Brush _neutralBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));

        public PaintIoWindow()
        {
            InitializeComponent();
            _state.Net.PacketReceived += OnPacket;
            Closed += (s, e) =>
            {
                _state.Net.PacketReceived -= OnPacket;
                _ = _state.Net.SendAsync(Packet.Create(PacketType.PaintIo,
                    new PaintIoPacket { Msg = PaintIoMsgType.Leave }));
            };

            // Join Request
            _ = _state.Net.SendAsync(Packet.Create(PacketType.PaintIo,
                new PaintIoPacket { Msg = PaintIoMsgType.Join }));

            KeyDown += OnKeyDown;
        }

        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ignore key input if overlays are visible (died / not yet started)
            if (DeathOverlay.Visibility == Visibility.Visible) return;
            if (Overlay.Visibility == Visibility.Visible) return;

            Direction? dir = e.Key switch
            {
                Key.Up or Key.W => Direction.Up,
                Key.Down or Key.S => Direction.Down,
                Key.Left or Key.A => Direction.Left,
                Key.Right or Key.D => Direction.Right,
                _ => null
            };

            if (dir.HasValue)
            {
                await _state.Net.SendAsync(Packet.Create(PacketType.PaintIo, new PaintIoPacket
                {
                    Msg = PaintIoMsgType.Input,
                    Dir = dir.Value
                }));
            }
        }

        private void OnPacket(Packet pkt)
        {
            if (pkt.Type != PacketType.PaintIo) return;
            var data = pkt.GetData<PaintIoPacket>();
            if (data == null) return;

            Dispatcher.Invoke(() => HandlePacket(data));
        }

        private void HandlePacket(PaintIoPacket pkt)
        {
            switch (pkt.Msg)
            {
                case PaintIoMsgType.GameInfo:
                    InitMap(pkt.MapWidth, pkt.MapHeight);
                    Overlay.Visibility = Visibility.Collapsed;
                    break;
                case PaintIoMsgType.State:
                    UpdateState(pkt);
                    break;
                case PaintIoMsgType.Death:
                    ShowDeathScreen();
                    break;
            }
        }

        // ── Map Initialization ────────────────────────────────────────────────

        private void InitMap(int w, int h)
        {
            _mapWidth = w;
            _mapHeight = h;
            _grid = new Rectangle[w, h];
            GameCanvas.Width = w * 10;
            GameCanvas.Height = h * 10;
            GameCanvas.Children.Clear();
            _playerElements.Clear();
            _players.Clear();
            _playerBrushes.Clear();
            _trailBrushes.Clear();

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    var r = new Rectangle
                    {
                        Width = 10,
                        Height = 10,
                        Fill = _neutralBrush
                    };
                    Canvas.SetLeft(r, x * 10);
                    Canvas.SetTop(r, y * 10);
                    GameCanvas.Children.Add(r);
                    _grid[x, y] = r;
                }
        }

        // ── State Update ──────────────────────────────────────────────────────

        private void UpdateState(PaintIoPacket pkt)
        {
            // 1. Apply map tile ownership changes
            if (pkt.MapUpdates != null)
            {
                foreach (var u in pkt.MapUpdates)
                {
                    if (u.X < 0 || u.X >= _mapWidth || u.Y < 0 || u.Y >= _mapHeight) continue;
                    var rect = _grid[u.X, u.Y];
                    if (string.IsNullOrEmpty(u.Owner))
                    {
                        rect.Fill = _neutralBrush;
                    }
                    else
                    {
                        // Only use brushes if the color is already known; otherwise we'll
                        // revisit once the player state arrives in the same packet.
                        rect.Fill = GetPlayerBrushByName(u.Owner);
                    }
                }
            }

            // 2. Remove old trail rectangles
            var trailToRemove = GameCanvas.Children.OfType<UIElement>()
                .Where(e => e is Rectangle r && r.Tag?.ToString() == "Trail")
                .ToList();
            foreach (var e in trailToRemove) GameCanvas.Children.Remove(e);

            // 3. Update players
            if (pkt.Players != null)
            {
                var activePlayers = new HashSet<string>();

                foreach (var p in pkt.Players)
                {
                    _players[p.Username] = p;
                    activePlayers.Add(p.Username);

                    // Ensure brush exists with correct color
                    EnsurePlayerBrush(p.Username, p.Color);
                    var brush = _playerBrushes[p.Username];

                    // Track my score
                    if (p.Username == _myUsername)
                        _myLastScore = p.Score;

                    // Animate or create player head
                    if (!_playerElements.TryGetValue(p.Username, out var playerUI))
                    {
                        playerUI = CreatePlayerUI(p, brush);
                        _playerElements[p.Username] = playerUI;
                    }
                    else
                    {
                        // Animate to new position
                        double targetX = p.X * 10;
                        double targetY = p.Y * 10;
                        double currentX = Canvas.GetLeft(playerUI);
                        double currentY = Canvas.GetTop(playerUI);

                        if (Math.Abs(currentX - targetX) > 0.1 || Math.Abs(currentY - targetY) > 0.1)
                        {
                            playerUI.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(120)));
                            playerUI.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(120)));
                        }

                        // Update head border color in case it changed
                        if (playerUI is Canvas container && container.Children.Count > 0
                            && container.Children[0] is Border head)
                        {
                            head.BorderBrush = brush;
                            head.Background = brush;
                        }
                    }

                    // Draw trail for this player
                    if (p.Trail != null)
                    {
                        var trailBrush = _trailBrushes.TryGetValue(p.Username, out var tb) ? tb : brush;
                        foreach (var t in p.Trail)
                        {
                            var tr = new Rectangle
                            {
                                Width = 10,
                                Height = 10,
                                Fill = trailBrush,
                                Tag = "Trail",
                                Opacity = 0.75
                            };
                            Canvas.SetLeft(tr, t[0] * 10);
                            Canvas.SetTop(tr, t[1] * 10);
                            GameCanvas.Children.Add(tr);
                            Panel.SetZIndex(tr, 50);
                        }
                    }
                }

                // Remove departed players
                var toRemovePlayers = _playerElements.Keys.Except(activePlayers).ToList();
                foreach (var rp in toRemovePlayers)
                {
                    if (_playerElements.TryGetValue(rp, out var el))
                    {
                        GameCanvas.Children.Remove(el);
                        _playerElements.Remove(rp);
                    }
                    _players.Remove(rp);
                }

                // Refresh map cells for players whose color we now know
                // (handles case where territory was drawn before we knew player's color)
                if (pkt.MapUpdates != null)
                {
                    foreach (var u in pkt.MapUpdates)
                    {
                        if (u.X < 0 || u.X >= _mapWidth || u.Y < 0 || u.Y >= _mapHeight) continue;
                        if (!string.IsNullOrEmpty(u.Owner))
                            _grid[u.X, u.Y].Fill = GetPlayerBrushByName(u.Owner);
                    }
                }

                // Update leaderboard
                UpdateLeaderboard(pkt.Players);
            }
        }

        // ── Leaderboard ──────────────────────────────────────────────────────

        private void UpdateLeaderboard(List<PaintIoPlayer> players)
        {
            ScorePanel.Children.Clear();

            // Header
            var header = new TextBlock
            {
                Text = "🏆 Leaderboard",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };
            ScorePanel.Children.Add(header);

            // Sort by score desc
            var sorted = players.OrderByDescending(p => p.Score).ToList();
            int rank = 1;
            foreach (var p in sorted)
            {
                var brush = GetPlayerBrushByName(p.Username);
                var isMe = p.Username == _myUsername;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

                var rankDot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = brush,
                    Margin = new Thickness(0, 3, 5, 0)
                };

                var nameText = new TextBlock
                {
                    Text = $"{p.Username}",
                    Foreground = isMe ? Brushes.Yellow : Brushes.White,
                    FontWeight = isMe ? FontWeights.Bold : FontWeights.Normal,
                    FontSize = 11
                };

                var scoreText = new TextBlock
                {
                    Text = $"  {p.Score}",
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    FontSize = 11
                };

                row.Children.Add(rankDot);
                row.Children.Add(nameText);
                row.Children.Add(scoreText);
                ScorePanel.Children.Add(row);
                rank++;
            }
        }

        // ── Player UI Creation ────────────────────────────────────────────────

        private FrameworkElement CreatePlayerUI(PaintIoPlayer p, Brush brush)
        {
            var container = new Canvas
            {
                Width = 10,
                Height = 10,
                IsHitTestVisible = false
            };

            // Solid colored head
            var head = new Border
            {
                Width = 10,
                Height = 10,
                Background = brush,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(2)
            };

            // Name tag above head
            var nameTag = new TextBlock
            {
                Text = p.Username,
                Foreground = Brushes.White,
                FontSize = 7,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0)
            };
            nameTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetTop(nameTag, -11);
            Canvas.SetLeft(nameTag, (10 - nameTag.DesiredSize.Width) / 2);

            container.Children.Add(head);
            container.Children.Add(nameTag);

            Canvas.SetLeft(container, p.X * 10);
            Canvas.SetTop(container, p.Y * 10);

            GameCanvas.Children.Add(container);
            Panel.SetZIndex(container, 100);
            return container;
        }

        // ── Death / Restart ───────────────────────────────────────────────────

        private void ShowDeathScreen()
        {
            DeathScoreText.Text = $"Territory: {_myLastScore} cells";
            DeathOverlay.Visibility = Visibility.Visible;
        }

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            // Hide overlay, reset state, re-join
            DeathOverlay.Visibility = Visibility.Collapsed;
            Overlay.Visibility = Visibility.Visible;

            // Clear local state
            _players.Clear();
            _playerBrushes.Clear();
            _trailBrushes.Clear();
            if (_grid.Length > 0)
            {
                GameCanvas.Children.Clear();
                _playerElements.Clear();
                InitMap(_mapWidth, _mapHeight);
            }

            // Re-request join
            _ = _state.Net.SendAsync(Packet.Create(PacketType.PaintIo,
                new PaintIoPacket { Msg = PaintIoMsgType.Join }));
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ── Brush Helpers ─────────────────────────────────────────────────────

        /// <summary>Creates or updates the color brush for a player.</summary>
        private void EnsurePlayerBrush(string username, string colorHex)
        {
            if (_playerBrushes.ContainsKey(username)) return; // already set

            Color c = Colors.Gray;
            if (!string.IsNullOrEmpty(colorHex))
            {
                try { c = (Color)ColorConverter.ConvertFromString(colorHex); } catch { }
            }

            var solid = new SolidColorBrush(c);
            solid.Freeze();
            _playerBrushes[username] = solid;

            // Trail brush: same color at 60% opacity
            var trail = new SolidColorBrush(Color.FromArgb(160, c.R, c.G, c.B));
            trail.Freeze();
            _trailBrushes[username] = trail;
        }

        /// <summary>Returns the player's brush, falling back to gray if unknown.</summary>
        private Brush GetPlayerBrushByName(string username)
        {
            if (_playerBrushes.TryGetValue(username, out var b)) return b;
            // Color not yet known — return a neutral placeholder (will be corrected next tick)
            return Brushes.DimGray;
        }
    }
}
