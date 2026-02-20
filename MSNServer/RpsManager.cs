using MSNShared;

namespace MSNServer
{
    public class RpsManager
    {
        private readonly Dictionary<string, RpsGame> _games = new();
        private readonly Dictionary<string, string> _playerGame = new();
        private readonly Dictionary<string, (string gameId, string inviter)> _pendingInvites = new();
        private readonly object _lock = new();

        private readonly Func<string, ConnectedClient?> _getClient;

        public RpsManager(Func<string, ConnectedClient?> getClient)
        {
            _getClient = getClient;
        }

        private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✂️ {msg}");

        public async Task HandleAsync(ConnectedClient client, RpsPacket pkt)
        {
            pkt.From = client.Username!;

            switch (pkt.Msg)
            {
                case RpsMsgType.Invite: await HandleInvite(client, pkt); break;
                case RpsMsgType.InviteAccept: await HandleAccept(client, pkt); break;
                case RpsMsgType.InviteDecline: await HandleDecline(client, pkt); break;
                case RpsMsgType.Move: await HandleMove(client, pkt); break;
                case RpsMsgType.Leave: await HandleLeave(client, pkt); break;
            }
        }

        private async Task HandleInvite(ConnectedClient client, RpsPacket pkt)
        {
            var target = _getClient(pkt.To);
            if (target is null)
            {
                await client.SendAsync(MakePkt(new RpsPacket { Msg = RpsMsgType.InviteDecline, From = pkt.To, To = client.Username!, GameId = "" }));
                return;
            }

            lock (_lock)
            {
                if (_playerGame.ContainsKey(client.Username!) || _playerGame.ContainsKey(pkt.To))
                {
                    // Optionally inform sender that they or target are busy
                    return;
                }
                // Check if already invited
                if (_pendingInvites.ContainsKey(pkt.To))
                {
                    // User already has a pending invite
                    return;
                }

                var gameId = Guid.NewGuid().ToString("N")[..10];
                pkt.GameId = gameId;
                _pendingInvites[pkt.To] = (gameId, client.Username!);
            }

            // Forward invite
            pkt.From = client.Username!;
            await target.SendAsync(MakePkt(pkt));
        }

        private async Task HandleAccept(ConnectedClient client, RpsPacket pkt)
        {
            string? inviter;
            string? gameId;

            lock (_lock)
            {
                if (!_pendingInvites.TryGetValue(client.Username!, out var inv)) return;
                _pendingInvites.Remove(client.Username!);
                inviter = inv.inviter;
                gameId = inv.gameId;

                var game = new RpsGame(gameId, inviter, client.Username!);
                _games[gameId] = game;
                _playerGame[inviter] = gameId;
                _playerGame[client.Username!] = gameId;

                Log($"RPS Game started: {game.Player1} vs {game.Player2} (id={game.GameId})");
            }

            var challenger = _getClient(inviter);
            if (challenger == null) return; // Should handle this edge case (cleanup)

            var startPkt = new RpsPacket
            {
                Msg = RpsMsgType.InviteAccept,
                GameId = gameId,
                From = inviter, // P1
                To = client.Username! // P2
            };

            await challenger.SendAsync(MakePkt(startPkt));
            await client.SendAsync(MakePkt(startPkt));
        }

        private async Task HandleDecline(ConnectedClient client, RpsPacket pkt)
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
                await challenger.SendAsync(MakePkt(new RpsPacket { Msg = RpsMsgType.InviteDecline, From = client.Username!, To = inviter! }));
        }

        private async Task HandleMove(ConnectedClient client, RpsPacket pkt)
        {
            RpsGame? game;
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(client.Username!, out var gid)) return;
                if (!_games.TryGetValue(gid, out game)) return;
            }

            bool roundComplete = false;
            lock (game) // Lock individual game state
            {
                if (!game.SubmitMove(client.Username!, pkt.Move)) return;
                roundComplete = game.RoundComplete;
            }

            if (roundComplete)
            {
                // Determine result
                RpsMove p1Move, p2Move;
                string? winner;
                int p1Score, p2Score;

                lock (game)
                {
                    var res = game.ResolveRound();
                    winner = res.Winner;
                    p1Move = game.LastP1Move;
                    p2Move = game.LastP2Move;
                    p1Score = game.P1Score;
                    p2Score = game.P2Score;
                    game.NewRound();
                }

                // Send results
                var p1 = _getClient(game.Player1);
                var p2 = _getClient(game.Player2);

                var resPkt = new RpsPacket
                {
                    Msg = RpsMsgType.Result,
                    GameId = game.GameId,
                    Winner = winner ?? "",
                    P1Score = p1Score,
                    P2Score = p2Score
                };

                if (p1 != null)
                {
                    resPkt.Move = p1Move;         // P1 see their move
                    resPkt.OpponentMove = p2Move; // P1 see P2 move
                    await p1.SendAsync(MakePkt(resPkt));
                }

                if (p2 != null)
                {
                    resPkt.Move = p2Move;         // P2 see their move
                    resPkt.OpponentMove = p1Move; // P2 see P1 move
                    await p2.SendAsync(MakePkt(resPkt));
                }

                // Check Game Over (First to 3)
                if (p1Score >= 3 || p2Score >= 3)
                {
                    var gameWinner = p1Score >= 3 ? game.Player1 : game.Player2;
                    var overPkt = new RpsPacket
                    {
                        Msg = RpsMsgType.GameOver,
                        GameId = game.GameId,
                        Winner = gameWinner
                    };

                    if (p1 != null) await p1.SendAsync(MakePkt(overPkt));
                    if (p2 != null) await p2.SendAsync(MakePkt(overPkt));

                    CleanupGame(game.GameId);
                }
            }
        }

        private void CleanupGame(string gameId)
        {
            lock (_lock)
            {
                if (_games.TryGetValue(gameId, out var g))
                {
                    _playerGame.Remove(g.Player1);
                    _playerGame.Remove(g.Player2);
                    _games.Remove(gameId);
                    Log($"Game Over: {g.Player1} vs {g.Player2}");
                }
            }
        }

        private static Packet MakePkt(RpsPacket data) => Packet.Create(PacketType.RockPaperScissors, data);

        public async Task OnDisconnect(string username)
        {
            string? gameId;
            lock (_lock)
            {
                if (!_playerGame.TryGetValue(username, out gameId)) return;
            }
            if (gameId != null)
            {
                await HandleLeave(new ConnectedClient(null!) { Username = username }, new RpsPacket { GameId = gameId, From = username });
            }
            lock (_lock) _pendingInvites.Remove(username);
        }

        private async Task HandleLeave(ConnectedClient client, RpsPacket pkt)
        {
            RpsGame? game;
            lock (_lock)
            {
                if (!_games.TryGetValue(pkt.GameId, out game)) return;
            }

            // Notify opponent
            var opponentName = game.Player1 == client.Username ? game.Player2 : game.Player1;
            var opponent = _getClient(opponentName);

            if (opponent != null)
            {
                await opponent.SendAsync(MakePkt(new RpsPacket
                {
                    Msg = RpsMsgType.InviteDecline, // Reuse InviteDecline for "Opponent left" as client handles it same way
                    GameId = pkt.GameId,
                    From = client.Username!
                }));
            }

            CleanupGame(pkt.GameId);
        }
    }

    public class RpsGame
    {
        public string GameId { get; }
        public string Player1 { get; }
        public string Player2 { get; }

        public int P1Score { get; private set; }
        public int P2Score { get; private set; }

        public RpsMove CurrentP1Move { get; private set; } = RpsMove.None;
        public RpsMove CurrentP2Move { get; private set; } = RpsMove.None;

        public RpsMove LastP1Move { get; private set; }
        public RpsMove LastP2Move { get; private set; }

        public bool RoundComplete => CurrentP1Move != RpsMove.None && CurrentP2Move != RpsMove.None;

        public RpsGame(string id, string p1, string p2)
        {
            GameId = id;
            Player1 = p1;
            Player2 = p2;
        }

        public bool SubmitMove(string player, RpsMove move)
        {
            if (move == RpsMove.None) return false;

            if (player == Player1)
            {
                if (CurrentP1Move != RpsMove.None) return false;
                CurrentP1Move = move;
                return true;
            }
            if (player == Player2)
            {
                if (CurrentP2Move != RpsMove.None) return false;
                CurrentP2Move = move;
                return true;
            }
            return false;
        }

        public (string? Winner, bool Tie) ResolveRound()
        {
            LastP1Move = CurrentP1Move;
            LastP2Move = CurrentP2Move;

            // Determine winner
            string? winner = null;
            bool tie = false;

            if (CurrentP1Move == CurrentP2Move)
            {
                tie = true;
            }
            else
            {
                bool p1Wins = (CurrentP1Move == RpsMove.Rock && CurrentP2Move == RpsMove.Scissors) ||
                              (CurrentP1Move == RpsMove.Paper && CurrentP2Move == RpsMove.Rock) ||
                              (CurrentP1Move == RpsMove.Scissors && CurrentP2Move == RpsMove.Paper);

                if (p1Wins)
                {
                    P1Score++;
                    winner = Player1;
                }
                else
                {
                    P2Score++;
                    winner = Player2;
                }
            }
            return (winner, tie);
        }

        public void NewRound()
        {
            CurrentP1Move = RpsMove.None;
            CurrentP2Move = RpsMove.None;
        }
    }
}
