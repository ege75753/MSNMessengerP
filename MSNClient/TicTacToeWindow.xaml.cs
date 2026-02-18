using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MSNShared;

namespace MSNClient
{
    /// <summary>
    /// Tic-Tac-Toe window. Works for both active players and spectators.
    /// IsSpectator = true means the board is read-only.
    /// </summary>
    public partial class TicTacToeWindow : Window
    {
        private readonly ClientState _state = App.State;
        private string _gameId = "";
        private string _playerX = "";   // username
        private string _playerO = "";
        private bool _isXTurn = true;
        private bool _isSpectator;
        private bool _gameOver;
        private Button[] _cells = null!;

        // ── Factory helpers ───────────────────────────────────────────────────
        /// <summary>Open as a player (after InviteAccept received).</summary>
        public TicTacToeWindow(TttPacket pkt)
        {
            InitializeComponent();
            _cells = new[] { Cell0, Cell1, Cell2, Cell3, Cell4, Cell5, Cell6, Cell7, Cell8 };
            _isSpectator = false;
            ApplyState(pkt);
            App.State.Net.PacketReceived += OnPacket;
            Closed += (_, _) => App.State.Net.PacketReceived -= OnPacket;
        }

        /// <summary>Open as spectator (after SpectateJoin received).</summary>
        public TicTacToeWindow(TttGameInfo info, bool spectator = true)
        {
            InitializeComponent();
            _cells = new[] { Cell0, Cell1, Cell2, Cell3, Cell4, Cell5, Cell6, Cell7, Cell8 };
            _isSpectator = spectator;
            _gameId = info.GameId;
            _playerX = info.PlayerX;
            _playerO = info.PlayerO;
            PlayerXName.Text = info.PlayerXDisplay + " (✕)";
            PlayerOName.Text = info.PlayerODisplay + " (○)";
            ApplyBoard(info.Board, info.IsXTurn, null, 0, info.Spectators);
            App.State.Net.PacketReceived += OnPacket;
            Closed += (_, _) =>
            {
                App.State.Net.PacketReceived -= OnPacket;
                if (_isSpectator)
                    _ = _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe,
                        new TttPacket { Msg = TttMsgType.SpectateLeave, GameId = _gameId }));
                _state.OpenTttGames.Remove(_gameId);
            };
        }

        // ── Packet handler ────────────────────────────────────────────────────
        private void OnPacket(Packet pkt)
        {
            if (pkt.Type != PacketType.TicTacToe) return;
            var data = pkt.GetData<TttPacket>();
            if (data == null || data.GameId != _gameId) return;
            Dispatcher.Invoke(() => HandleTttPacket(data));
        }

        private void HandleTttPacket(TttPacket pkt)
        {
            switch (pkt.Msg)
            {
                case TttMsgType.Move:
                    ApplyBoard(pkt.Board!, pkt.IsXTurn, null, 0, pkt.Spectators);
                    break;

                case TttMsgType.GameOver:
                    ApplyBoard(pkt.Board!, pkt.IsXTurn, pkt.WinLine, pkt.Winner, pkt.Spectators);
                    ShowGameOver(pkt.Winner, pkt.From);
                    _gameOver = true;
                    DisableAllCells();
                    ResignBtn.IsEnabled = false;
                    break;

                case TttMsgType.SpectateJoin:
                    // Someone new joined as spectator — update spectator list
                    UpdateSpectators(pkt.Spectators);
                    break;
            }
        }

        private void ApplyState(TttPacket pkt)
        {
            _gameId = pkt.GameId;
            _playerX = pkt.From; // inviter is always X
            _playerO = pkt.To;

            // Figure out who we are
            var me = _state.MyUsername;
            var xDisplay = _state.GetContact(_playerX)?.DisplayName ?? _playerX;
            var oDisplay = _state.GetContact(_playerO)?.DisplayName ?? _playerO;
            if (_playerX == me) xDisplay = _state.MyDisplayName;
            if (_playerO == me) oDisplay = _state.MyDisplayName;

            PlayerXName.Text = xDisplay + " (✕)";
            PlayerOName.Text = oDisplay + " (○)";

            ApplyBoard(pkt.Board ?? new int[9], pkt.IsXTurn, null, 0, pkt.Spectators);
        }

        private void ApplyBoard(int[] board, bool isXTurn, int[]? winLine, int winner, List<string> spectators)
        {
            _isXTurn = isXTurn;

            for (int i = 0; i < 9; i++)
            {
                _cells[i].Content = board[i] switch { 1 => "✕", 2 => "○", _ => "" };
                _cells[i].Foreground = board[i] == 1
                    ? new SolidColorBrush(Color.FromRgb(220, 60, 60))
                    : new SolidColorBrush(Color.FromRgb(30, 130, 220));
                _cells[i].Background = Brushes.White;
                _cells[i].IsEnabled = !_isSpectator && board[i] == 0 && winner == 0 && !_gameOver;
            }

            // Highlight winning cells
            if (winLine != null)
            {
                foreach (var idx in winLine)
                    _cells[idx].Background = new SolidColorBrush(Color.FromRgb(200, 255, 180));
            }

            // Lock cells not belonging to current player's turn
            if (!_isSpectator && winner == 0)
            {
                var amX = _state.MyUsername == _playerX;
                var isMyTurn = (amX && isXTurn) || (!amX && !isXTurn);
                foreach (var c in _cells)
                    if (c.IsEnabled) c.IsEnabled = isMyTurn;
            }

            UpdateStatusText(isXTurn, winner);
            UpdateSpectators(spectators);
        }

        private void UpdateStatusText(bool isXTurn, int winner)
        {
            if (winner == 3)
            {
                StatusText.Text = "🤝 It's a draw!";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80));
                return;
            }
            if (winner != 0)
            {
                var winnerName = winner == 1
                    ? (PlayerXName.Text.Replace(" (✕)", ""))
                    : (PlayerOName.Text.Replace(" (○)", ""));
                StatusText.Text = $"🏆 {winnerName} wins!";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 140, 0));
                return;
            }

            var turnName = isXTurn
                ? PlayerXName.Text.Replace(" (✕)", "")
                : PlayerOName.Text.Replace(" (○)", "");
            var isMe = (isXTurn && _state.MyUsername == _playerX) ||
                       (!isXTurn && _state.MyUsername == _playerO);

            StatusText.Text = _isSpectator
                ? $"⏳ {turnName}'s turn"
                : isMe ? "✅ Your turn!" : $"⏳ Waiting for {turnName}...";
            StatusText.Foreground = isMe && !_isSpectator
                ? new SolidColorBrush(Color.FromRgb(0, 100, 0))
                : new SolidColorBrush(Color.FromRgb(0, 50, 150));
        }

        private void UpdateSpectators(List<string> spectators)
        {
            if (spectators == null || spectators.Count == 0)
            {
                SpectatorList.Text = "None";
                return;
            }
            var names = spectators.Select(u =>
            {
                if (u == _state.MyUsername) return _state.MyDisplayName;
                return _state.GetContact(u)?.DisplayName ?? u;
            });
            SpectatorList.Text = string.Join(", ", names);
        }

        private void ShowGameOver(int winner, string winnerUsername)
        {
            string msg;
            if (winner == 3)
                msg = "It's a draw! Well played both.";
            else
            {
                var winnerDisplay = winner == 1
                    ? PlayerXName.Text.Replace(" (✕)", "")
                    : PlayerOName.Text.Replace(" (○)", "");
                var isMe = winnerUsername == _state.MyUsername;
                msg = isMe ? $"🏆 You win! Congratulations!" : $"😞 {winnerDisplay} wins. Better luck next time!";
            }
            MessageBox.Show(msg, "Game Over", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DisableAllCells()
        {
            foreach (var c in _cells) c.IsEnabled = false;
        }

        // ── Cell click ────────────────────────────────────────────────────────
        private async void Cell_Click(object sender, RoutedEventArgs e)
        {
            if (_isSpectator || _gameOver) return;
            var btn = (Button)sender;
            var cell = int.Parse((string)btn.Tag);
            if ((string?)btn.Content != "") return; // already filled

            // Optimistically disable all cells until we get server confirmation
            DisableAllCells();

            await _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe, new TttPacket
            {
                Msg = TttMsgType.Move,
                GameId = _gameId,
                Cell = cell,
                From = _state.MyUsername
            }));
        }

        // ── Resign / Close ────────────────────────────────────────────────────
        private async void Resign_Click(object sender, RoutedEventArgs e)
        {
            if (_gameOver) return;
            var result = MessageBox.Show("Are you sure you want to resign?", "Resign",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            await _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe, new TttPacket
            {
                Msg = TttMsgType.Abandon,
                GameId = _gameId,
                From = _state.MyUsername
            }));
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
