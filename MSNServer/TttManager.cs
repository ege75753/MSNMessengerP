using MSNShared;

namespace MSNServer
{
    /// <summary>
    /// Manages all active Tic-Tac-Toe games and routes packets between players/spectators.
    /// </summary>
    public class TttManager
    {
        private readonly Dictionary<string, TttGame> _games = new();
        // username -> gameId for active players
        private readonly Dictionary<string, string> _playerGame = new();
        // pending invites: invitee -> (gameId, inviter)
        private readonly Dictionary<string, (string gameId, string inviter)> _pendingInvites = new();
        private readonly object _lock = new();

        private readonly Func<string, ConnectedClient?> _getClient;
        private readonly Func<string, Task> _broadcastPresence;

        public TttManager(Func<string, ConnectedClient?> getClient, Func<string, Task> broadcastPresence)
        {
            _getClient = getClient;
            _broadcastPresence = broadcastPresence;
        }

        private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🎮 {msg}");

        public async Task HandleAsync(ConnectedClient client, TttPacket pkt)
        {
            pkt.From = client.Username!;

            switch (pkt.Msg)
            {
                case TttMsgType.Invite: await HandleInvite(client, pkt); break;
                case TttMsgType.InviteAccept: await HandleAccept(client, pkt); break;
                case TttMsgType.InviteDecline: await HandleDecline(client, pkt); break;
                case TttMsgType.Move: await HandleMove(client, pkt); break;
                case TttMsgType.SpectateRequest: await HandleSpectate(client, pkt); break;
                case TttMsgType.SpectateLeave: await HandleSpectateLeave(client, pkt); break;
                case TttMsgType.Abandon: await HandleAbandon(client, pkt); break;
            }
        }

        private async Task HandleInvite(ConnectedClient client, TttPacket pkt)
        {
            var target = _getClient(pkt.To);
            if (target is null)
            {
                await client.SendAsync(MakePkt(new TttPacket { Msg = TttMsgType.InviteDecline, From = pkt.To, To = client.Username!, GameId = "" }));
                return;
            }

            lock (_lock)
            {
                if (_playerGame.ContainsKey(client.Username!)) return; // already in game
                var gameId = Guid.NewGuid().ToString("N")[..10];
                pkt.GameId = gameId;
                _pendingInvites[pkt.To] = (gameId, client.Username!);
            }

            await target.SendAsync(MakePkt(pkt));
        }

        private async Task HandleAccept(ConnectedClient client, TttPacket pkt)
        {
            ConnectedClient? challenger;
            TttGame game;

            lock (_lock)
            {
                if (!_pendingInvites.TryGetValue(client.Username!, out var inv)) return;
                _pendingInvites.Remove(client.Username!);
                challenger = _getClient(inv.inviter);
                if (challenger is null) return;

                game = new TttGame(inv.gameId, inv.inviter, client.Username!,
                    challenger.DisplayName, client.DisplayName);
                _games[inv.gameId] = game;
                _playerGame[inv.inviter] = inv.gameId;
                _playerGame[client.Username!] = inv.gameId;
            }

            Log($"Game started: {game.PlayerX} vs {game.PlayerO} (id={game.GameId})");

            // Tell both players the game started with board state
            var stateForX = game.ToPacket(TttMsgType.InviteAccept);
            stateForX.To = game.PlayerO; // opponent
            await challenger.SendAsync(MakePkt(stateForX));

            var stateForO = game.ToPacket(TttMsgType.InviteAccept);
            stateForO.To = game.PlayerX;
            await client.SendAsync(MakePkt(stateForO));

            // Update both players' presence
            await _broadcastPresence(game.PlayerX);
            await _broadcastPresence(game.PlayerO);
        }

        private async Task HandleDecline(ConnectedClient client, TttPacket pkt)
        {
            string? inviter;
            lock (_lock)
            {
                if (!_pendingInvites.TryGetValue(client.Username!, out var inv)) return;
                _pendingInvites.Remove(client.Username!);
                inviter = inv.inviter;
            }
            var challenger = _getClient(inviter!);
            if (challenger != null)
                await challenger.SendAsync(MakePkt(new TttPacket { Msg = TttMsgType.InviteDecline, From = client.Username!, To = inviter! }));
        }

        private async Task HandleMove(ConnectedClient client, TttPacket pkt)
        {
            TttGame? game;
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(client.Username!, out var gid)) return;
                if (!_games.TryGetValue(gid, out game)) return;
            }

            if (!game.TryMove(client.Username!, pkt.Cell)) return; // invalid

            var movePacket = game.ToPacket(TttMsgType.Move);
            movePacket.Cell = pkt.Cell;

            // Send to the player who made the move (to confirm their move)
            await client.SendAsync(MakePkt(movePacket));

            // Send to opponent
            var opponentName = game.PlayerX == client.Username! ? game.PlayerO : game.PlayerX;
            var opponent = _getClient(opponentName);
            if (opponent != null) await opponent.SendAsync(MakePkt(movePacket));

            // Send to all spectators
            foreach (var spec in game.Spectators.ToList())
            {
                var sc = _getClient(spec);
                if (sc != null) await sc.SendAsync(MakePkt(movePacket));
            }

            // Check game over
            if (game.IsOver)
            {
                var overPkt = game.ToPacket(TttMsgType.GameOver);
                if (opponent != null) await opponent.SendAsync(MakePkt(overPkt));
                await client.SendAsync(MakePkt(overPkt));
                foreach (var spec in game.Spectators.ToList())
                {
                    var sc = _getClient(spec);
                    if (sc != null) await sc.SendAsync(MakePkt(overPkt));
                }

                CleanupGame(game.GameId);
                Log($"Game over: {game.PlayerX} vs {game.PlayerO} → winner={game.Winner}");
            }
        }

        private async Task HandleSpectate(ConnectedClient client, TttPacket pkt)
        {
            TttGame? game;
            lock (_lock)
            {
                if (!_games.TryGetValue(pkt.GameId, out game)) return;
                if (!game.Spectators.Contains(client.Username!))
                    game.Spectators.Add(client.Username!);
            }

            // Send current board state
            var statePkt = game.ToPacket(TttMsgType.SpectateJoin);
            await client.SendAsync(MakePkt(statePkt));
            Log($"{client.Username} is now spectating {game.PlayerX} vs {game.PlayerO}");
        }

        private async Task HandleSpectateLeave(ConnectedClient client, TttPacket pkt)
        {
            lock (_lock)
            {
                if (_games.TryGetValue(pkt.GameId, out var g))
                    g.Spectators.Remove(client.Username!);
            }
        }

        private async Task HandleAbandon(ConnectedClient client, TttPacket pkt)
        {
            TttGame? game;
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(client.Username!, out var gid)) return;
                if (!_games.TryGetValue(gid, out game)) return;
            }

            // The abandoning player loses
            game.Abandon(client.Username!);
            var overPkt = game.ToPacket(TttMsgType.GameOver);

            var opponentName = game.PlayerX == client.Username! ? game.PlayerO : game.PlayerX;
            var opponent = _getClient(opponentName);
            if (opponent != null) await opponent.SendAsync(MakePkt(overPkt));
            await client.SendAsync(MakePkt(overPkt));
            foreach (var spec in game.Spectators.ToList())
            {
                var sc = _getClient(spec);
                if (sc != null) await sc.SendAsync(MakePkt(overPkt));
            }

            CleanupGame(game.GameId);
        }

        /// <summary>Called when a player disconnects mid-game.</summary>
        public async Task OnDisconnect(string username)
        {
            string? gameId;
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(username, out gameId)) return;
            }
            if (gameId != null)
            {
                var fake = new TttPacket { GameId = gameId, From = username };
                // Simulate abandon via a stub client — just call the logic directly
                TttGame? game;
                lock (_lock) _games.TryGetValue(gameId, out game);
                if (game != null)
                {
                    game.Abandon(username);
                    var overPkt = game.ToPacket(TttMsgType.GameOver);
                    var opponentName = game.PlayerX == username ? game.PlayerO : game.PlayerX;
                    var opponent = _getClient(opponentName);
                    if (opponent != null) await opponent.SendAsync(MakePkt(overPkt));
                    foreach (var spec in game.Spectators.ToList())
                    {
                        var sc = _getClient(spec);
                        if (sc != null) await sc.SendAsync(MakePkt(overPkt));
                    }
                    CleanupGame(gameId);
                }
            }
            // Also remove any pending invites
            lock (_lock) _pendingInvites.Remove(username);
        }

        private void CleanupGame(string gameId)
        {
            string? px = null, po = null;
            lock (_lock)
            {
                if (_games.TryGetValue(gameId, out var g))
                {
                    px = g.PlayerX; po = g.PlayerO;
                    _playerGame.Remove(g.PlayerX);
                    _playerGame.Remove(g.PlayerO);
                    _games.Remove(gameId);
                }
            }
            // Refresh presence for both players — removes the game status message
            if (px != null) Task.Run(() => _broadcastPresence(px));
            if (po != null) Task.Run(() => _broadcastPresence(po));
        }

        /// <summary>Returns all active game infos (for spectate menu).</summary>
        public List<TttGameInfo> GetActiveGames()
        {
            lock (_lock)
                return _games.Values.Select(g => g.ToInfo()).ToList();
        }

        public bool IsInGame(string username)
        {
            lock (_lock) return _playerGame.ContainsKey(username);
        }

        public string? GetGameId(string username)
        {
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(username, out var gid)) return null;
                return gid;
            }
        }

        public string? GetGameStatus(string username)
        {
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(username, out var gid)) return null;
                if (!_games.TryGetValue(gid, out var g)) return null;
                return g.PlayerX == username ? g.PlayerODisplay : g.PlayerXDisplay;
            }
        }

        private static Packet MakePkt(TttPacket data) =>
            Packet.Create(PacketType.TicTacToe, data);
    }

    // ── In-memory game state ───────────────────────────────────────────────────
    public class TttGame
    {
        public string GameId { get; }
        public string PlayerX { get; }   // goes first
        public string PlayerO { get; }
        public string PlayerXDisplay { get; }
        public string PlayerODisplay { get; }
        public int[] Board { get; } = new int[9];
        public bool IsXTurn { get; private set; } = true;
        public bool IsOver { get; private set; }
        public int Winner { get; private set; } = 0;  // 0=none 1=X 2=O 3=draw
        public int[]? WinLine { get; private set; }
        public List<string> Spectators { get; } = new();

        private static readonly int[][] WinCombos = {
            new[]{0,1,2}, new[]{3,4,5}, new[]{6,7,8},
            new[]{0,3,6}, new[]{1,4,7}, new[]{2,5,8},
            new[]{0,4,8}, new[]{2,4,6}
        };

        public TttGame(string gameId, string x, string o, string xDisplay, string oDisplay)
        {
            GameId = gameId;
            PlayerX = x; PlayerO = o;
            PlayerXDisplay = xDisplay; PlayerODisplay = oDisplay;
        }

        public bool TryMove(string username, int cell)
        {
            if (IsOver) return false;
            if (cell < 0 || cell > 8) return false;
            if (Board[cell] != 0) return false;
            var expectedPlayer = IsXTurn ? PlayerX : PlayerO;
            if (username != expectedPlayer) return false;

            Board[cell] = IsXTurn ? 1 : 2;
            IsXTurn = !IsXTurn;
            CheckWin();
            return true;
        }

        public void Abandon(string username)
        {
            // Opponent wins
            Winner = (username == PlayerX) ? 2 : 1;
            IsOver = true;
        }

        private void CheckWin()
        {
            foreach (var combo in WinCombos)
            {
                var v = Board[combo[0]];
                if (v != 0 && v == Board[combo[1]] && v == Board[combo[2]])
                {
                    Winner = v;
                    WinLine = combo;
                    IsOver = true;
                    return;
                }
            }
            if (Board.All(c => c != 0)) { Winner = 3; IsOver = true; }
        }

        public TttPacket ToPacket(TttMsgType msg) => new()
        {
            Msg = msg, GameId = GameId,
            From = msg == TttMsgType.GameOver && Winner == 1 ? PlayerX :
                   msg == TttMsgType.GameOver && Winner == 2 ? PlayerO : PlayerX,
            To = PlayerO,
            Board = (int[])Board.Clone(),
            IsXTurn = IsXTurn,
            Winner = Winner,
            WinLine = WinLine,
            Spectators = new List<string>(Spectators)
        };

        public TttGameInfo ToInfo() => new()
        {
            GameId = GameId,
            PlayerX = PlayerX, PlayerO = PlayerO,
            PlayerXDisplay = PlayerXDisplay, PlayerODisplay = PlayerODisplay,
            Board = (int[])Board.Clone(),
            IsXTurn = IsXTurn,
            IsOver = IsOver,
            Spectators = new List<string>(Spectators)
        };
    }
}
