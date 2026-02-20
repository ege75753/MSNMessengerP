using MSNShared;

namespace MSNServer
{
    public class BlackjackManager
    {
        private readonly Dictionary<string, BjLobby> _lobbies = new();
        private readonly Dictionary<string, string> _playerLobby = new();
        private readonly object _lock = new();
        private readonly Func<string, ConnectedClient?> _getClient;

        public BlackjackManager(Func<string, ConnectedClient?> getClient) => _getClient = getClient;
        private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🃏 {msg}");

        public async Task HandleAsync(ConnectedClient client, BlackjackPacket pkt)
        {
            pkt.From = client.Username!;
            switch (pkt.Msg)
            {
                case BlackjackMsgType.CreateLobby: await HandleCreateLobby(client, pkt); break;
                case BlackjackMsgType.JoinLobby: await HandleJoinLobby(client, pkt); break;
                case BlackjackMsgType.LeaveLobby: await HandleLeave(client.Username!); break;
                case BlackjackMsgType.StartGame: await HandleStartGame(client, pkt); break;
                case BlackjackMsgType.PlaceBet: await HandlePlaceBet(client, pkt); break;
                case BlackjackMsgType.PlayerAction: await HandlePlayerAction(client, pkt); break;
                case BlackjackMsgType.NextRound: await HandleNextRound(client, pkt); break;
            }
        }

        public List<BlackjackLobbyInfo> GetLobbies()
        {
            lock (_lock)
                return _lobbies.Values.Select(l => new BlackjackLobbyInfo
                {
                    LobbyId = l.LobbyId,
                    LobbyName = l.LobbyName,
                    Host = l.Host,
                    HostDisplayName = _getClient(l.Host)?.DisplayName ?? l.Host,
                    PlayerCount = l.Players.Count,
                    MaxPlayers = l.MaxPlayers,
                    GameStarted = l.GameStarted
                }).ToList();
        }

        public async Task OnDisconnect(string username)
        {
            await HandleLeave(username);
        }

        // ── Handlers ────────────────────────────────────────────────────────────

        private async Task HandleCreateLobby(ConnectedClient client, BlackjackPacket pkt)
        {
            BjLobby lobby;
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return;
                var lid = Guid.NewGuid().ToString("N")[..10];
                var name = pkt.LobbyName.Trim().Length > 0 ? pkt.LobbyName : $"{client.DisplayName}'s Table";
                lobby = new BjLobby(lid, name, client.Username!, Math.Clamp(pkt.MaxPlayers, 2, 7));
                lobby.AddPlayer(client.Username!, client.DisplayName);
                _lobbies[lid] = lobby;
                _playerLobby[client.Username!] = lid;
                Log($"Lobby created: {name} by {client.Username}");
            }
            await BroadcastLobbyState(lobby);
        }

        private async Task HandleJoinLobby(ConnectedClient client, BlackjackPacket pkt)
        {
            BjLobby? lobby;
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return;
                if (!_lobbies.TryGetValue(pkt.LobbyId, out lobby)) return;
                if (lobby.GameStarted || lobby.Players.Count >= lobby.MaxPlayers) return;
                lobby.AddPlayer(client.Username!, client.DisplayName);
                _playerLobby[client.Username!] = pkt.LobbyId;
            }
            await BroadcastLobbyState(lobby);
        }

        private async Task HandleLeave(string username)
        {
            BjLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(username, out var lid)) return;
                _playerLobby.Remove(username);
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                lobby.RemovePlayer(username);
                if (lobby.Players.Count == 0) { _lobbies.Remove(lid); return; }
                if (lobby.Host == username) lobby.Host = lobby.Players[0];
                if (lobby.GameStarted && lobby.CurrentPlayerIndex >= lobby.Players.Count)
                    lobby.CurrentPlayerIndex = lobby.Players.Count - 1;
            }
            await BroadcastLobbyState(lobby);
            if (lobby.GameStarted) await CheckAndAdvanceTurn(lobby);
        }

        private async Task HandleStartGame(ConnectedClient client, BlackjackPacket pkt)
        {
            BjLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (lobby.Host != client.Username || lobby.GameStarted) return;
            }
            // Enter betting phase
            lock (_lock) { lobby.InBettingPhase = true; lobby.PlayersWhoHaveBet.Clear(); lobby.Bets.Clear(); }
            var bp = MakePkt(new BlackjackPacket
            {
                Msg = BlackjackMsgType.BettingPhase,
                LobbyId = lobby.LobbyId,
                Balances = new Dictionary<string, int>(lobby.Balances),
                Players = lobby.Players.ToList(),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.DisplayNames)
            });
            await BroadcastToLobby(lobby, bp);
        }

        private async Task HandlePlaceBet(ConnectedClient client, BlackjackPacket pkt)
        {
            BjLobby? lobby;
            bool allBet = false;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.InBettingPhase) return;
                if (lobby.PlayersWhoHaveBet.Contains(client.Username!)) return;

                var balance = lobby.Balances.GetValueOrDefault(client.Username!, 1000);
                var bet = Math.Clamp(pkt.BetAmount, 5, balance);
                lobby.Bets[client.Username!] = bet;
                lobby.Balances[client.Username!] = balance - bet;
                lobby.PlayersWhoHaveBet.Add(client.Username!);
                allBet = lobby.PlayersWhoHaveBet.Count >= lobby.Players.Count;
                if (allBet) lobby.InBettingPhase = false;
            }

            // Broadcast updated bet state
            var update = MakePkt(new BlackjackPacket
            {
                Msg = BlackjackMsgType.LobbyState,
                LobbyId = lobby.LobbyId,
                LobbyName = lobby.LobbyName,
                Host = lobby.Host,
                Players = lobby.Players.ToList(),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.DisplayNames),
                Scores = new Dictionary<string, int>(lobby.Scores),
                Balances = new Dictionary<string, int>(lobby.Balances),
                Bets = new Dictionary<string, int>(lobby.Bets),
                Pot = lobby.Bets.Values.Sum()
            });
            await BroadcastToLobby(lobby, update);

            if (allBet) await StartRound(lobby);
        }

        private async Task HandlePlayerAction(ConnectedClient client, BlackjackPacket pkt)
        {
            BjLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted || lobby.CurrentPlayer != client.Username) return;
            }
            var game = lobby.Game!;
            if (pkt.Action == BjAction.Hit)
            {
                BjCard card;
                lock (_lock) { card = game.DealCard(); game.PlayerHands[client.Username!].Add(card); }
                await BroadcastHandUpdate(lobby, client.Username!);
                if (HandValue(game.PlayerHands[client.Username!]) >= 21)
                    await AdvanceTurn(lobby);
                else
                    await BroadcastLobbyState(lobby);
            }
            else { await AdvanceTurn(lobby); }
        }

        private async Task HandleNextRound(ConnectedClient client, BlackjackPacket pkt)
        {
            BjLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (lobby.Host != client.Username) return;
            }
            // Betting phase again
            lock (_lock) { lobby.InBettingPhase = true; lobby.PlayersWhoHaveBet.Clear(); lobby.Bets.Clear(); lobby.GameStarted = false; }
            var bp = MakePkt(new BlackjackPacket
            {
                Msg = BlackjackMsgType.BettingPhase,
                LobbyId = lobby.LobbyId,
                Balances = new Dictionary<string, int>(lobby.Balances),
                Players = lobby.Players.ToList(),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.DisplayNames)
            });
            await BroadcastToLobby(lobby, bp);
        }

        // ── Game flow ────────────────────────────────────────────────────────────

        private async Task StartRound(BjLobby lobby)
        {
            BjGame game;
            lock (_lock)
            {
                game = new BjGame(lobby.Players.ToList());
                lobby.Game = game;
                lobby.GameStarted = true;
                lobby.CurrentPlayerIndex = 0;
            }
            Log($"Round started in {lobby.LobbyName} ({lobby.Players.Count} players)");
            await BroadcastLobbyState(lobby);
        }

        private async Task AdvanceTurn(BjLobby lobby)
        {
            bool allDone;
            lock (_lock) { lobby.CurrentPlayerIndex++; allDone = lobby.CurrentPlayerIndex >= lobby.Players.Count; }
            if (allDone) await RunDealerTurn(lobby);
            else await BroadcastLobbyState(lobby);
        }

        private async Task CheckAndAdvanceTurn(BjLobby lobby)
        {
            bool allDone;
            lock (_lock) { allDone = lobby.CurrentPlayerIndex >= lobby.Players.Count; }
            if (allDone) await RunDealerTurn(lobby);
            else await BroadcastLobbyState(lobby);
        }

        private async Task RunDealerTurn(BjLobby lobby)
        {
            var game = lobby.Game!;
            lock (_lock) { if (game.DealerHand.Count > 0) game.DealerHand[0].Hidden = false; }
            while (HandValue(game.DealerHand) < 17)
            {
                BjCard card;
                lock (_lock) { card = game.DealCard(); game.DealerHand.Add(card); }
            }
            var dealerTotal = HandValue(game.DealerHand);
            await BroadcastToLobby(lobby, MakePkt(new BlackjackPacket
            {
                Msg = BlackjackMsgType.DealerTurn,
                LobbyId = lobby.LobbyId,
                DealerHand = game.DealerHand.ToList()
            }));

            var results = new Dictionary<string, string>();
            lock (_lock)
            {
                foreach (var player in lobby.Players)
                {
                    var hand = game.PlayerHands[player];
                    var total = HandValue(hand);
                    int bet = lobby.Bets.GetValueOrDefault(player, 0);
                    string outcome;
                    if (total > 21) { outcome = "Bust"; }
                    else if (total == 21 && hand.Count == 2 && !(dealerTotal == 21 && game.DealerHand.Count == 2))
                    { outcome = "Blackjack"; lobby.Balances[player] = lobby.Balances.GetValueOrDefault(player) + (int)(bet * 2.5); }
                    else if (dealerTotal > 21 || total > dealerTotal)
                    { outcome = "Win"; lobby.Balances[player] = lobby.Balances.GetValueOrDefault(player) + bet * 2; }
                    else if (total == dealerTotal)
                    { outcome = "Push"; lobby.Balances[player] = lobby.Balances.GetValueOrDefault(player) + bet; }
                    else { outcome = "Lose"; }
                    results[player] = outcome;
                    lobby.Scores[player] = lobby.Scores.GetValueOrDefault(player) + (outcome is "Win" or "Blackjack" ? 1 : 0);
                }
                lobby.GameStarted = false;
            }
            await BroadcastToLobby(lobby, MakePkt(new BlackjackPacket
            {
                Msg = BlackjackMsgType.RoundResult,
                LobbyId = lobby.LobbyId,
                Results = results,
                Scores = new Dictionary<string, int>(lobby.Scores),
                Balances = new Dictionary<string, int>(lobby.Balances),
                Host = lobby.Host,
                DealerHand = game.DealerHand.ToList()
            }));
        }

        // ── Broadcast helpers ────────────────────────────────────────────────────

        private async Task BroadcastLobbyState(BjLobby lobby)
        {
            BlackjackPacket pkt;
            lock (_lock)
            {
                pkt = new BlackjackPacket
                {
                    Msg = BlackjackMsgType.LobbyState,
                    LobbyId = lobby.LobbyId,
                    LobbyName = lobby.LobbyName,
                    Host = lobby.Host,
                    Players = lobby.Players.ToList(),
                    PlayerDisplayNames = new Dictionary<string, string>(lobby.DisplayNames),
                    Scores = new Dictionary<string, int>(lobby.Scores),
                    Balances = new Dictionary<string, int>(lobby.Balances),
                    Bets = new Dictionary<string, int>(lobby.Bets),
                    Pot = lobby.Bets.Values.Sum(),
                    GameStarted = lobby.GameStarted,
                    MaxPlayers = lobby.MaxPlayers,
                    CurrentPlayer = lobby.CurrentPlayer
                };
                if (lobby.GameStarted && lobby.Game != null)
                {
                    pkt.Hands = lobby.Game.PlayerHands.ToDictionary(k => k.Key, v => v.Value.ToList());
                    pkt.DealerHand = lobby.Game.DealerHand.ToList();
                }
            }
            await BroadcastToLobby(lobby, MakePkt(pkt));
        }

        private async Task BroadcastHandUpdate(BjLobby lobby, string username)
        {
            BlackjackPacket pkt;
            lock (_lock)
            {
                pkt = new BlackjackPacket
                {
                    Msg = BlackjackMsgType.HandUpdate,
                    LobbyId = lobby.LobbyId,
                    From = username,
                    Hands = new Dictionary<string, List<BjCard>> { [username] = lobby.Game!.PlayerHands[username].ToList() },
                    CurrentPlayer = lobby.CurrentPlayer
                };
            }
            await BroadcastToLobby(lobby, MakePkt(pkt));
        }

        private async Task BroadcastToLobby(BjLobby lobby, Packet pkt)
        {
            List<string> players;
            lock (_lock) { players = lobby.Players.ToList(); }
            foreach (var p in players) { var c = _getClient(p); if (c != null) await c.SendAsync(pkt); }
        }

        private static Packet MakePkt(BlackjackPacket data) => Packet.Create(PacketType.Blackjack, data);

        public static int HandValue(List<BjCard> hand)
        {
            int total = 0, aces = 0;
            foreach (var c in hand.Where(c => !c.Hidden))
            {
                if (c.Rank == "A") { total += 11; aces++; }
                else if (c.Rank is "J" or "Q" or "K") total += 10;
                else total += int.Parse(c.Rank);
            }
            while (total > 21 && aces > 0) { total -= 10; aces--; }
            return total;
        }
    }

    public class BjLobby
    {
        public string LobbyId { get; }
        public string LobbyName { get; }
        public string Host { get; set; }
        public int MaxPlayers { get; }
        public bool GameStarted { get; set; }
        public bool InBettingPhase { get; set; }
        public List<string> Players { get; } = new();
        public Dictionary<string, string> DisplayNames { get; } = new();
        public Dictionary<string, int> Scores { get; } = new();
        public Dictionary<string, int> Balances { get; } = new();
        public Dictionary<string, int> Bets { get; } = new();
        public HashSet<string> PlayersWhoHaveBet { get; } = new();
        public int CurrentPlayerIndex { get; set; }
        public string CurrentPlayer => GameStarted && CurrentPlayerIndex < Players.Count ? Players[CurrentPlayerIndex] : "";
        public BjGame? Game { get; set; }

        public BjLobby(string id, string name, string host, int maxPlayers)
        { LobbyId = id; LobbyName = name; Host = host; MaxPlayers = maxPlayers; }

        public void AddPlayer(string username, string displayName)
        {
            if (!Players.Contains(username)) Players.Add(username);
            DisplayNames[username] = displayName;
            Scores.TryAdd(username, 0);
            Balances.TryAdd(username, 1000);
        }

        public void RemovePlayer(string username) => Players.Remove(username);
    }

    public class BjGame
    {
        private readonly List<BjCard> _deck;
        public List<BjCard> DealerHand { get; } = new();
        public Dictionary<string, List<BjCard>> PlayerHands { get; } = new();

        public BjGame(List<string> players)
        {
            _deck = BuildDeck();
            Shuffle(_deck);
            foreach (var p in players) PlayerHands[p] = new List<BjCard>();
            for (int i = 0; i < 2; i++)
                foreach (var p in players) PlayerHands[p].Add(DealCard());
            var hole = DealCard(); hole.Hidden = true;
            DealerHand.Add(hole);
            DealerHand.Add(DealCard());
        }

        public BjCard DealCard()
        {
            if (_deck.Count == 0) { _deck.AddRange(BuildDeck()); Shuffle(_deck); }
            var c = _deck[^1]; _deck.RemoveAt(_deck.Count - 1); return c;
        }

        private static List<BjCard> BuildDeck() =>
            [.. new[] { "A","2","3","4","5","6","7","8","9","10","J","Q","K" }
                .SelectMany(r => new[] { "♠","♥","♦","♣" }.Select(s => new BjCard { Rank = r, Suit = s }))];

        private static void Shuffle(List<BjCard> deck)
        {
            var rng = new Random();
            for (int i = deck.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (deck[i], deck[j]) = (deck[j], deck[i]); }
        }
    }
}
