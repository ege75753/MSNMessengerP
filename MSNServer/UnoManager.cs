using MSNShared;

namespace MSNServer
{
    public class UnoLobby
    {
        public string LobbyId { get; }
        public string LobbyName { get; }
        public string Host { get; set; }
        public int MaxPlayers { get; }
        public List<string> Players { get; } = new();
        public Dictionary<string, string> PlayerDisplayNames { get; } = new();
        public bool GameStarted { get; set; }

        public List<UnoCard> Deck { get; set; } = new();
        public List<UnoCard> DiscardPile { get; set; } = new();
        public Dictionary<string, List<UnoCard>> Hands { get; } = new();

        public int CurrentTurnIndex { get; set; }
        public bool IsClockwise { get; set; } = true;
        public UnoColor CurrentColor { get; set; }

        public bool PendingColorChoice { get; set; } // true when a player plays a wild and must choose color

        public UnoLobby(string id, string name, string host, string hostDisplay, int maxPlayers)
        {
            LobbyId = id;
            LobbyName = name;
            Host = host;
            MaxPlayers = maxPlayers;

            Players.Add(host);
            PlayerDisplayNames[host] = hostDisplay;
        }

        public void InitializeDeck()
        {
            Deck.Clear();
            DiscardPile.Clear();
            Hands.Clear();
            foreach (var p in Players) Hands[p] = new List<UnoCard>();

            var colors = new[] { UnoColor.Red, UnoColor.Yellow, UnoColor.Green, UnoColor.Blue };
            foreach (var color in colors)
            {
                Deck.Add(new UnoCard { Color = color, Value = UnoValue.Zero });
                for (int i = 1; i <= 9; i++)
                {
                    Deck.Add(new UnoCard { Color = color, Value = (UnoValue)i });
                    Deck.Add(new UnoCard { Color = color, Value = (UnoValue)i });
                }
                for (int i = 0; i < 2; i++)
                {
                    Deck.Add(new UnoCard { Color = color, Value = UnoValue.Skip });
                    Deck.Add(new UnoCard { Color = color, Value = UnoValue.Reverse });
                    Deck.Add(new UnoCard { Color = color, Value = UnoValue.DrawTwo });
                }
            }
            for (int i = 0; i < 4; i++)
            {
                Deck.Add(new UnoCard { Color = UnoColor.None, Value = UnoValue.Wild });
                Deck.Add(new UnoCard { Color = UnoColor.None, Value = UnoValue.WildDrawFour });
            }

            ShuffleDeck();
        }

        public void ShuffleDeck()
        {
            var rng = new Random();
            int n = Deck.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                var value = Deck[k];
                Deck[k] = Deck[n];
                Deck[n] = value;
            }
        }

        public void DrawCards(string player, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (Deck.Count == 0)
                {
                    if (DiscardPile.Count > 1)
                    {
                        var top = DiscardPile.Last();
                        DiscardPile.RemoveAt(DiscardPile.Count - 1);
                        Deck.AddRange(DiscardPile);
                        DiscardPile.Clear();
                        DiscardPile.Add(top);
                        // reset wild colors
                        foreach (var c in Deck)
                        {
                            if (c.Value == UnoValue.Wild || c.Value == UnoValue.WildDrawFour)
                                c.Color = UnoColor.None;
                        }
                        ShuffleDeck();
                    }
                    else
                    {
                        break; // No cards left to shuffle
                    }
                }

                if (Deck.Count > 0)
                {
                    var card = Deck.Last();
                    Deck.RemoveAt(Deck.Count - 1);
                    Hands[player].Add(card);
                }
            }
        }

        public void NextTurn()
        {
            if (IsClockwise)
            {
                CurrentTurnIndex = (CurrentTurnIndex + 1) % Players.Count;
            }
            else
            {
                CurrentTurnIndex = (CurrentTurnIndex - 1 + Players.Count) % Players.Count;
            }
        }
    }

    public class UnoManager
    {
        private readonly Func<string, ConnectedClient?> _getClient;
        private readonly Dictionary<string, UnoLobby> _lobbies = new();
        private readonly Dictionary<string, string> _playerLobby = new();
        private readonly object _lock = new();

        public UnoManager(Func<string, ConnectedClient?> getClient)
        {
            _getClient = getClient;
        }

        public async Task HandleAsync(ConnectedClient client, UnoPacket pkt)
        {
            switch (pkt.Msg)
            {
                case UnoMsgType.CreateLobby:
                    await HandleCreateLobby(client, pkt);
                    break;
                case UnoMsgType.JoinLobby:
                    await HandleJoinLobby(client, pkt);
                    break;
                case UnoMsgType.LeaveLobby:
                    await HandleLeaveLobby(client);
                    break;
                case UnoMsgType.StartGame:
                    await HandleStartGame(client);
                    break;
                case UnoMsgType.PlayCard:
                    await HandlePlayCard(client, pkt);
                    break;
                case UnoMsgType.DrawCard:
                    await HandleDrawCard(client);
                    break;
                case UnoMsgType.ChooseColor:
                    await HandleChooseColor(client, pkt);
                    break;
            }
        }

        public List<UnoLobbyInfo> GetLobbies()
        {
            lock (_lock)
            {
                return _lobbies.Values.Select(l => new UnoLobbyInfo
                {
                    LobbyId = l.LobbyId,
                    LobbyName = l.LobbyName,
                    Host = l.Host,
                    HostDisplayName = l.PlayerDisplayNames.GetValueOrDefault(l.Host, l.Host),
                    PlayerCount = l.Players.Count,
                    MaxPlayers = l.MaxPlayers,
                    GameStarted = l.GameStarted
                }).ToList();
            }
        }

        public async Task OnDisconnect(string username)
        {
            await HandleLeaveLobbyInternal(username);
        }

        private async Task HandleCreateLobby(ConnectedClient client, UnoPacket pkt)
        {
            UnoLobby lobby;
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return;

                lobby = new UnoLobby(
                    Guid.NewGuid().ToString("N")[..10],
                    string.IsNullOrWhiteSpace(pkt.LobbyName) ? $"{client.DisplayName}'s Uno Game" : pkt.LobbyName,
                    client.Username!,
                    client.DisplayName,
                    Math.Clamp(pkt.MaxPlayers, 2, 10)
                );

                _lobbies[lobby.LobbyId] = lobby;
                _playerLobby[client.Username!] = lobby.LobbyId;
            }

            await BroadcastLobbyState(lobby);
        }

        private async Task HandleJoinLobby(ConnectedClient client, UnoPacket pkt)
        {
            UnoLobby lobby;
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return;
                if (!_lobbies.TryGetValue(pkt.LobbyId, out lobby!)) return;
                if (lobby.GameStarted || lobby.Players.Count >= lobby.MaxPlayers) return;

                lobby.Players.Add(client.Username!);
                lobby.PlayerDisplayNames[client.Username!] = client.DisplayName;
                _playerLobby[client.Username!] = lobby.LobbyId;
            }

            await BroadcastLobbyState(lobby);
        }

        private async Task HandleLeaveLobby(ConnectedClient client)
        {
            await HandleLeaveLobbyInternal(client.Username!);
        }

        private async Task HandleLeaveLobbyInternal(string username)
        {
            UnoLobby? lobby = null;
            bool removedLobby = false;

            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(username, out var lid)) return;
                _playerLobby.Remove(username);
                if (!_lobbies.TryGetValue(lid, out lobby)) return;

                lobby.Players.Remove(username);
                lobby.PlayerDisplayNames.Remove(username);

                if (lobby.Players.Count == 0)
                {
                    _lobbies.Remove(lid);
                    removedLobby = true;
                }
                else if (lobby.Host == username)
                {
                    lobby.Host = lobby.Players[0];
                }

                if (lobby.GameStarted && !removedLobby)
                {
                    // For MVP: end game if someone leaves mid-game.
                    lobby.GameStarted = false;
                    _ = Task.Run(() => BroadcastToLobby(lobby, new UnoPacket
                    {
                        Msg = UnoMsgType.GameOver,
                        LobbyId = lobby.LobbyId,
                        Message = $"{username} left the game. Game Over."
                    }));
                }
            }

            if (!removedLobby && lobby != null && !lobby.GameStarted)
                await BroadcastLobbyState(lobby);
        }

        private async Task HandleStartGame(ConnectedClient client)
        {
            UnoLobby lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby!)) return;
                if (lobby.Host != client.Username || lobby.GameStarted) return;
                if (lobby.Players.Count < 1) return; // Allow 1 player for easier local testing

                lobby.GameStarted = true;
                lobby.InitializeDeck();

                // Deal 7 cards to each player
                foreach (var p in lobby.Players)
                {
                    lobby.DrawCards(p, 7);
                }

                // Initial card
                UnoCard top;
                do
                {
                    top = lobby.Deck.Last();
                    lobby.Deck.RemoveAt(lobby.Deck.Count - 1);
                    lobby.DiscardPile.Add(top);
                    if (top.Value == UnoValue.WildDrawFour)
                    {
                        lobby.DiscardPile.RemoveAt(lobby.DiscardPile.Count - 1);
                        lobby.Deck.Insert(0, top);
                        // Try again
                    }
                    else
                    {
                        break;
                    }
                } while (true);

                lobby.CurrentColor = top.Color;
                if (top.Color == UnoColor.None)
                {
                    // It's a Wild card (not +4), host can choose color... or we randomly pick to be faster
                    lobby.CurrentColor = (UnoColor)new Random().Next(1, 4);
                    top.Color = lobby.CurrentColor; // display it as chosen
                }

                lobby.CurrentTurnIndex = 0;
                lobby.IsClockwise = true;

                if (top.Value == UnoValue.Reverse) lobby.IsClockwise = false;
                if (top.Value == UnoValue.Skip) lobby.NextTurn();
                if (top.Value == UnoValue.DrawTwo)
                {
                    lobby.DrawCards(lobby.Players[lobby.CurrentTurnIndex], 2);
                    lobby.NextTurn(); // First player gets skipped since they draw
                }
            }

            await BroadcastGameState(lobby);
        }

        private async Task HandlePlayCard(ConnectedClient client, UnoPacket pkt)
        {
            UnoLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted) return;

                if (lobby.Players[lobby.CurrentTurnIndex] != client.Username) return;
                if (lobby.PendingColorChoice) return;

                // Validate card
                var playingCardId = pkt.PlayedCard?.InstanceId;
                if (string.IsNullOrEmpty(playingCardId)) return;

                var hand = lobby.Hands[client.Username!];
                var cardIndex = hand.FindIndex(c => c.InstanceId == playingCardId);
                if (cardIndex < 0) return;

                var card = hand[cardIndex];
                var top = lobby.DiscardPile.Last();

                bool valid = card.Color == UnoColor.None ||
                             card.Color == lobby.CurrentColor ||
                             card.Value == top.Value;

                if (!valid) return;

                // Play the card
                hand.RemoveAt(cardIndex);
                lobby.DiscardPile.Add(card);

                if (card.Color != UnoColor.None)
                    lobby.CurrentColor = card.Color;

                // effects
                if (card.Value == UnoValue.Wild || card.Value == UnoValue.WildDrawFour)
                {
                    lobby.PendingColorChoice = true;
                    // We must broadcast to tell them asking for color
                    // we'll break to broadcast, not next turning yet.
                }
                else
                {
                    ApplyCardEffects(lobby, card);
                }

                if (hand.Count == 0)
                {
                    // WINNER
                    lobby.GameStarted = false;
                    _ = Task.Run(() => BroadcastToLobby(lobby, new UnoPacket
                    {
                        Msg = UnoMsgType.GameOver,
                        LobbyId = lobby.LobbyId,
                        Winner = client.Username!,
                        Message = $"{client.DisplayName} won the game!"
                    }));
                    return;
                }
            }

            await BroadcastGameState(lobby, pkt.PlayedCard);
        }

        private async Task HandleChooseColor(ConnectedClient client, UnoPacket pkt)
        {
            UnoLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted) return;

                if (lobby.Players[lobby.CurrentTurnIndex] != client.Username) return;
                if (!lobby.PendingColorChoice) return;

                lobby.CurrentColor = pkt.ChosenColor;
                lobby.PendingColorChoice = false;

                var card = lobby.DiscardPile.Last();
                card.Color = pkt.ChosenColor; // update pile visual

                ApplyCardEffects(lobby, card);
            }

            // We must broadcast the new state
            await BroadcastGameState(lobby);
        }

        private void ApplyCardEffects(UnoLobby lobby, UnoCard card)
        {
            if (card.Value == UnoValue.Reverse)
            {
                if (lobby.Players.Count == 2)
                    lobby.NextTurn(); // reverse in 2p acts like skip
                else
                    lobby.IsClockwise = !lobby.IsClockwise;
                lobby.NextTurn();
            }
            else if (card.Value == UnoValue.Skip)
            {
                lobby.NextTurn(); // skip them
                lobby.NextTurn(); // next person
            }
            else if (card.Value == UnoValue.DrawTwo)
            {
                lobby.NextTurn();
                lobby.DrawCards(lobby.Players[lobby.CurrentTurnIndex], 2);
                lobby.NextTurn(); // they miss their turn
            }
            else if (card.Value == UnoValue.WildDrawFour)
            {
                lobby.NextTurn();
                lobby.DrawCards(lobby.Players[lobby.CurrentTurnIndex], 4);
                lobby.NextTurn();
            }
            else
            {
                lobby.NextTurn();
            }
        }

        private async Task HandleDrawCard(ConnectedClient client)
        {
            UnoLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted) return;
                if (lobby.Players[lobby.CurrentTurnIndex] != client.Username) return;
                if (lobby.PendingColorChoice) return;

                lobby.DrawCards(client.Username!, 1);

                // Usually standard Uno requires you to either play the card or pass.
                // For simplicity, drawing ends your turn immediately.
                lobby.NextTurn();
            }

            await BroadcastGameState(lobby);
        }

        private async Task BroadcastLobbyState(UnoLobby lobby)
        {
            var data = new UnoPacket
            {
                Msg = UnoMsgType.LobbyState,
                LobbyId = lobby.LobbyId,
                LobbyName = lobby.LobbyName,
                Host = lobby.Host,
                Players = new List<string>(lobby.Players),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames),
                MaxPlayers = lobby.MaxPlayers,
                GameStarted = lobby.GameStarted,
            };

            await BroadcastToLobby(lobby, data);
        }

        private async Task BroadcastGameState(UnoLobby lobby, UnoCard? playedCard = null)
        {
            var playersInfo = new List<UnoPlayer>();
            string currentTurn = "";
            UnoCard? top = null;
            UnoColor curColor = UnoColor.None;
            bool cw = true;

            lock (_lock)
            {
                foreach (var p in lobby.Players)
                {
                    playersInfo.Add(new UnoPlayer
                    {
                        Username = p,
                        DisplayName = lobby.PlayerDisplayNames.GetValueOrDefault(p, p),
                        CardCount = lobby.Hands[p].Count
                    });
                }
                currentTurn = lobby.Players[lobby.CurrentTurnIndex];
                top = lobby.DiscardPile.LastOrDefault();
                curColor = lobby.CurrentColor;
                cw = lobby.IsClockwise;
            }

            // we have to send personalized state so players only see their own hands
            foreach (var p in lobby.Players.ToList())
            {
                var c = _getClient(p);
                if (c == null) continue;

                var personalPlayers = playersInfo.Select(pi => new UnoPlayer
                {
                    Username = pi.Username,
                    DisplayName = pi.DisplayName,
                    CardCount = pi.CardCount,
                    Hand = pi.Username == p ? lobby.Hands[p].ToList() : null
                }).ToList();

                var pkt = Packet.Create(PacketType.Uno, new UnoPacket
                {
                    Msg = UnoMsgType.GameState,
                    LobbyId = lobby.LobbyId,
                    LobbyName = lobby.LobbyName,
                    Host = lobby.Host,
                    GameStarted = lobby.GameStarted,
                    MaxPlayers = lobby.MaxPlayers,
                    Players = new List<string>(lobby.Players),
                    PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames),
                    GamePlayers = personalPlayers,
                    TopCard = top,
                    CurrentColor = curColor,
                    CurrentTurn = currentTurn,
                    IsClockwise = cw,
                    PlayedCard = playedCard
                });

                await c.SendAsync(pkt);
            }
        }

        private async Task BroadcastToLobby(UnoLobby lobby, UnoPacket data)
        {
            var pkt = Packet.Create(PacketType.Uno, data);
            foreach (var player in lobby.Players.ToList())
            {
                var c = _getClient(player);
                if (c != null)
                    await c.SendAsync(pkt);
            }
        }
    }
}
