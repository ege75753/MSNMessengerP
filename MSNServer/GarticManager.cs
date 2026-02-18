using MSNShared;

namespace MSNServer
{
    /// <summary>
    /// Manages Gartic drawing-game lobbies and active games.
    /// </summary>
    public class GarticManager
    {
        private readonly Dictionary<string, GarticLobby> _lobbies = new();
        private readonly Dictionary<string, string> _playerLobby = new(); // username → lobbyId
        private readonly object _lock = new();

        private readonly Func<string, ConnectedClient?> _getClient;

        public GarticManager(Func<string, ConnectedClient?> getClient)
        {
            _getClient = getClient;
        }

        private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🎨 {msg}");

        public async Task HandleAsync(ConnectedClient client, GarticPacket pkt)
        {
            pkt.From = client.Username!;

            switch (pkt.Msg)
            {
                case GarticMsgType.CreateLobby: await HandleCreateLobby(client, pkt); break;
                case GarticMsgType.JoinLobby: await HandleJoinLobby(client, pkt); break;
                case GarticMsgType.LeaveLobby: await HandleLeaveLobby(client, pkt); break;
                case GarticMsgType.StartGame: await HandleStartGame(client, pkt); break;
                case GarticMsgType.DrawData: await HandleDrawData(client, pkt); break;
                case GarticMsgType.ClearCanvas: await HandleClearCanvas(client, pkt); break;
                case GarticMsgType.ChatGuess: await HandleChatGuess(client, pkt); break;
            }
        }

        // ── Lobby ──────────────────────────────────────────────────────────────

        private async Task HandleCreateLobby(ConnectedClient client, GarticPacket pkt)
        {
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return; // already in a lobby
            }

            var lobby = new GarticLobby(
                Guid.NewGuid().ToString("N")[..10],
                string.IsNullOrWhiteSpace(pkt.LobbyName) ? $"{client.DisplayName}'s Game" : pkt.LobbyName,
                client.Username!,
                client.DisplayName,
                Math.Clamp(pkt.MaxPlayers, 2, 12),
                Math.Clamp(pkt.RoundCount, 1, 10),
                Math.Clamp(pkt.RoundTimeSeconds, 15, 120)
            );

            lock (_lock)
            {
                _lobbies[lobby.LobbyId] = lobby;
                _playerLobby[client.Username!] = lobby.LobbyId;
            }

            Log($"Lobby created: '{lobby.LobbyName}' by {client.Username} (id={lobby.LobbyId})");
            await BroadcastLobbyState(lobby);
        }

        private async Task HandleJoinLobby(ConnectedClient client, GarticPacket pkt)
        {
            GarticLobby? lobby;
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return;
                if (!_lobbies.TryGetValue(pkt.LobbyId, out lobby)) return;
                if (lobby.GameStarted || lobby.Players.Count >= lobby.MaxPlayers) return;

                lobby.Players.Add(client.Username!);
                lobby.PlayerDisplayNames[client.Username!] = client.DisplayName;
                lobby.Scores[client.Username!] = 0;
                _playerLobby[client.Username!] = lobby.LobbyId;
            }

            Log($"{client.Username} joined lobby '{lobby.LobbyName}'");
            // Send system message
            await BroadcastChat(lobby, "System", $"{client.DisplayName} joined the lobby!");
            await BroadcastLobbyState(lobby);
        }

        private async Task HandleLeaveLobby(ConnectedClient client, GarticPacket pkt)
        {
            await RemovePlayerFromLobby(client.Username!);
        }

        private async Task HandleStartGame(ConnectedClient client, GarticPacket pkt)
        {
            GarticLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (lobby.Host != client.Username! || lobby.GameStarted) return;
                if (lobby.Players.Count < 2) return;
                lobby.GameStarted = true;
            }

            Log($"Game started in lobby '{lobby.LobbyName}' with {lobby.Players.Count} players");
            await BroadcastChat(lobby, "System", "🎮 The game has started!");
            await StartNextRound(lobby);
        }

        // ── Drawing ────────────────────────────────────────────────────────────

        private async Task HandleDrawData(ConnectedClient client, GarticPacket pkt)
        {
            GarticLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (lobby.CurrentDrawer != client.Username!) return;
            }

            // Relay draw data to all players except the drawer
            var relay = new GarticPacket
            {
                Msg = GarticMsgType.DrawData,
                LobbyId = lobby.LobbyId,
                From = client.Username!,
                DrawDataJson = pkt.DrawDataJson
            };
            await BroadcastToLobby(lobby, relay, except: client.Username!);
        }

        private async Task HandleClearCanvas(ConnectedClient client, GarticPacket pkt)
        {
            GarticLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (lobby.CurrentDrawer != client.Username!) return;
            }

            await BroadcastToLobby(lobby, new GarticPacket
            {
                Msg = GarticMsgType.ClearCanvas,
                LobbyId = lobby.LobbyId,
                From = client.Username!
            }, except: client.Username!);
        }

        // ── Chat / Guess ───────────────────────────────────────────────────────

        private async Task HandleChatGuess(ConnectedClient client, GarticPacket pkt)
        {
            GarticLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
            }

            var guess = pkt.Message.Trim();
            if (string.IsNullOrEmpty(guess)) return;

            // Drawer can't guess; already-correct players can't guess again
            if (lobby.CurrentDrawer == client.Username!) return;
            if (lobby.CorrectGuessers.Contains(client.Username!))
            {
                // Already guessed — just show as regular chat to other correct guessers
                return;
            }

            // Check if guess is correct
            if (!string.IsNullOrEmpty(lobby.CurrentWord) &&
                string.Equals(guess, lobby.CurrentWord, StringComparison.OrdinalIgnoreCase))
            {
                lobby.CorrectGuessers.Add(client.Username!);

                // Score: guesser gets points based on time left; drawer gets points too
                var timeBonus = Math.Max(10, (lobby.TimeLeft * 100) / lobby.RoundTimeSeconds);
                lock (_lock)
                {
                    lobby.Scores[client.Username!] = lobby.Scores.GetValueOrDefault(client.Username!) + timeBonus;
                    lobby.Scores[lobby.CurrentDrawer] = lobby.Scores.GetValueOrDefault(lobby.CurrentDrawer) + 25;
                }

                Log($"{client.Username} guessed correctly in '{lobby.LobbyName}' (+{timeBonus} pts)");

                // Broadcast correct guess notification
                await BroadcastToLobby(lobby, new GarticPacket
                {
                    Msg = GarticMsgType.CorrectGuess,
                    LobbyId = lobby.LobbyId,
                    Guesser = client.Username!,
                    DisplayName = client.DisplayName,
                    Scores = new Dictionary<string, int>(lobby.Scores)
                });

                // Check if all non-drawers have guessed
                var nonDrawers = lobby.Players.Where(p => p != lobby.CurrentDrawer).ToList();
                if (lobby.CorrectGuessers.Count >= nonDrawers.Count)
                {
                    // Everyone guessed — end round early
                    lobby.CancelRoundTimer();
                    await EndRound(lobby);
                }
            }
            else
            {
                // Wrong guess — broadcast as chat message to everyone
                await BroadcastToLobby(lobby, new GarticPacket
                {
                    Msg = GarticMsgType.ChatGuess,
                    LobbyId = lobby.LobbyId,
                    From = client.Username!,
                    DisplayName = client.DisplayName,
                    Message = guess
                });
            }
        }

        // ── Round management ───────────────────────────────────────────────────

        private async Task StartNextRound(GarticLobby lobby)
        {
            lock (_lock)
            {
                lobby.CurrentDrawerIndex++;
                if (lobby.CurrentDrawerIndex >= lobby.Players.Count)
                {
                    lobby.CurrentDrawerIndex = 0;
                    lobby.CurrentRound++;
                }

                if (lobby.CurrentRound > lobby.RoundCount)
                {
                    // Game over
                    _ = Task.Run(() => EndGame(lobby));
                    return;
                }

                lobby.CurrentDrawer = lobby.Players[lobby.CurrentDrawerIndex];
                lobby.CurrentWord = GarticWordList.GetRandomWord();
                lobby.CorrectGuessers.Clear();
                lobby.TimeLeft = lobby.RoundTimeSeconds;
            }

            Log($"Round {lobby.CurrentRound}/{lobby.RoundCount}: {lobby.CurrentDrawer} draws '{lobby.CurrentWord}'");

            // Send round state to all — drawer gets the word, others get hint
            var hint = new string(lobby.CurrentWord.Select(c => c == ' ' ? ' ' : '_').ToArray());

            foreach (var player in lobby.Players.ToList())
            {
                var c = _getClient(player);
                if (c == null) continue;

                var rsPkt = new GarticPacket
                {
                    Msg = GarticMsgType.RoundState,
                    LobbyId = lobby.LobbyId,
                    CurrentDrawer = lobby.CurrentDrawer,
                    Round = lobby.CurrentRound,
                    TotalRounds = lobby.RoundCount,
                    TimeLeft = lobby.RoundTimeSeconds,
                    WordHint = hint,
                    Word = (player == lobby.CurrentDrawer) ? lobby.CurrentWord : "",
                    Scores = new Dictionary<string, int>(lobby.Scores),
                    Players = new List<string>(lobby.Players),
                    PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames)
                };

                await c.SendAsync(MakePkt(rsPkt));
            }

            await BroadcastChat(lobby, "System",
                $"🎨 {lobby.PlayerDisplayNames.GetValueOrDefault(lobby.CurrentDrawer, lobby.CurrentDrawer)} is drawing! (Round {lobby.CurrentRound}/{lobby.RoundCount})");

            // Start round timer
            lobby.RoundCts = new CancellationTokenSource();
            var cts = lobby.RoundCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (lobby.TimeLeft > 0 && !cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, cts.Token);
                        lobby.TimeLeft--;

                        // Send time update every 5 seconds (and at 10, 5, 3, 2, 1)
                        if (lobby.TimeLeft % 10 == 0 || lobby.TimeLeft <= 5)
                        {
                            // Give a hint at halftime
                            if (lobby.TimeLeft == lobby.RoundTimeSeconds / 2 && lobby.CurrentWord.Length > 2)
                            {
                                var partialHint = hint.ToCharArray();
                                var rng = new Random();
                                var revealCount = Math.Max(1, lobby.CurrentWord.Length / 3);
                                var indices = Enumerable.Range(0, lobby.CurrentWord.Length)
                                    .Where(i => lobby.CurrentWord[i] != ' ')
                                    .OrderBy(_ => rng.Next())
                                    .Take(revealCount).ToList();
                                foreach (var idx in indices)
                                    partialHint[idx] = lobby.CurrentWord[idx];
                                hint = new string(partialHint);
                            }

                            await BroadcastToLobby(lobby, new GarticPacket
                            {
                                Msg = GarticMsgType.RoundState,
                                LobbyId = lobby.LobbyId,
                                CurrentDrawer = lobby.CurrentDrawer,
                                Round = lobby.CurrentRound,
                                TotalRounds = lobby.RoundCount,
                                TimeLeft = lobby.TimeLeft,
                                WordHint = hint,
                                Scores = new Dictionary<string, int>(lobby.Scores),
                                Players = new List<string>(lobby.Players),
                                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames)
                            });
                        }
                    }

                    if (!cts.Token.IsCancellationRequested)
                        await EndRound(lobby);
                }
                catch (OperationCanceledException) { }
            });
        }

        private async Task EndRound(GarticLobby lobby)
        {
            // Reveal the word
            await BroadcastToLobby(lobby, new GarticPacket
            {
                Msg = GarticMsgType.WordReveal,
                LobbyId = lobby.LobbyId,
                Word = lobby.CurrentWord,
                Scores = new Dictionary<string, int>(lobby.Scores),
                Players = new List<string>(lobby.Players),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames)
            });

            await BroadcastChat(lobby, "System", $"⏰ The word was: {lobby.CurrentWord}");

            // Wait a moment, then start next round
            await Task.Delay(3000);
            await StartNextRound(lobby);
        }

        private async Task EndGame(GarticLobby lobby)
        {
            Log($"Game over in lobby '{lobby.LobbyName}'");

            await BroadcastToLobby(lobby, new GarticPacket
            {
                Msg = GarticMsgType.GameOver,
                LobbyId = lobby.LobbyId,
                Scores = new Dictionary<string, int>(lobby.Scores),
                Players = new List<string>(lobby.Players),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames)
            });

            await BroadcastChat(lobby, "System", "🏆 Game Over! Check the final scores!");

            // Reset lobby to allow replaying
            lock (_lock)
            {
                lobby.GameStarted = false;
                lobby.CurrentRound = 0;
                lobby.CurrentDrawerIndex = -1;
                lobby.CurrentDrawer = "";
                lobby.CurrentWord = "";
                lobby.CorrectGuessers.Clear();
                foreach (var p in lobby.Players)
                    lobby.Scores[p] = 0;
            }

            await BroadcastLobbyState(lobby);
        }

        // ── Disconnect ─────────────────────────────────────────────────────────

        public async Task OnDisconnect(string username)
        {
            await RemovePlayerFromLobby(username);
        }

        private async Task RemovePlayerFromLobby(string username)
        {
            GarticLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(username, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;

                lobby.Players.Remove(username);
                lobby.PlayerDisplayNames.Remove(username);
                lobby.Scores.Remove(username);
                lobby.CorrectGuessers.Remove(username);
                _playerLobby.Remove(username);
            }

            var displayName = lobby.PlayerDisplayNames.GetValueOrDefault(username, username);
            Log($"{username} left lobby '{lobby.LobbyName}'");

            // If no players left, remove lobby
            if (lobby.Players.Count == 0)
            {
                lobby.CancelRoundTimer();
                lock (_lock) _lobbies.Remove(lobby.LobbyId);
                Log($"Lobby '{lobby.LobbyName}' removed (empty)");
                return;
            }

            // If host left, assign new host
            if (lobby.Host == username)
            {
                lobby.Host = lobby.Players[0];
                await BroadcastChat(lobby, "System", $"{lobby.PlayerDisplayNames.GetValueOrDefault(lobby.Host, lobby.Host)} is now the host.");
            }

            // If current drawer left mid-game, skip to next round
            if (lobby.GameStarted && lobby.CurrentDrawer == username)
            {
                lobby.CancelRoundTimer();
                await BroadcastChat(lobby, "System", $"{displayName} (drawer) left! Skipping to next round...");
                // Adjust index since we removed a player
                if (lobby.CurrentDrawerIndex >= lobby.Players.Count)
                    lobby.CurrentDrawerIndex = -1;
                else
                    lobby.CurrentDrawerIndex--; // will be incremented in StartNextRound
                await StartNextRound(lobby);
            }
            else
            {
                await BroadcastChat(lobby, "System", $"{displayName} left the lobby.");
                await BroadcastLobbyState(lobby);
            }

            // If only 1 player left during a game, end it
            if (lobby.GameStarted && lobby.Players.Count < 2)
            {
                lobby.CancelRoundTimer();
                await EndGame(lobby);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        public List<GarticLobbyInfo> GetLobbies()
        {
            lock (_lock)
            {
                return _lobbies.Values.Select(l => new GarticLobbyInfo
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

        private async Task BroadcastLobbyState(GarticLobby lobby)
        {
            await BroadcastToLobby(lobby, new GarticPacket
            {
                Msg = GarticMsgType.LobbyState,
                LobbyId = lobby.LobbyId,
                LobbyName = lobby.LobbyName,
                Host = lobby.Host,
                Players = new List<string>(lobby.Players),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames),
                Scores = new Dictionary<string, int>(lobby.Scores),
                MaxPlayers = lobby.MaxPlayers,
                RoundCount = lobby.RoundCount,
                RoundTimeSeconds = lobby.RoundTimeSeconds,
                GameStarted = lobby.GameStarted
            });
        }

        private async Task BroadcastChat(GarticLobby lobby, string from, string message)
        {
            await BroadcastToLobby(lobby, new GarticPacket
            {
                Msg = GarticMsgType.ChatMessage,
                LobbyId = lobby.LobbyId,
                From = from,
                Message = message
            });
        }

        private async Task BroadcastToLobby(GarticLobby lobby, GarticPacket data, string? except = null)
        {
            var pkt = MakePkt(data);
            foreach (var player in lobby.Players.ToList())
            {
                if (player == except) continue;
                var c = _getClient(player);
                if (c != null) await c.SendAsync(pkt);
            }
        }

        private static Packet MakePkt(GarticPacket data) =>
            Packet.Create(PacketType.Gartic, data);
    }

    // ── Lobby state ─────────────────────────────────────────────────────────

    public class GarticLobby
    {
        public string LobbyId { get; }
        public string LobbyName { get; }
        public string Host { get; set; }
        public int MaxPlayers { get; }
        public int RoundCount { get; }
        public int RoundTimeSeconds { get; }
        public List<string> Players { get; } = new();
        public Dictionary<string, string> PlayerDisplayNames { get; } = new();
        public Dictionary<string, int> Scores { get; } = new();
        public bool GameStarted { get; set; }

        // Round state
        public int CurrentRound { get; set; } = 0;
        public int CurrentDrawerIndex { get; set; } = -1;
        public string CurrentDrawer { get; set; } = "";
        public string CurrentWord { get; set; } = "";
        public HashSet<string> CorrectGuessers { get; } = new();
        public int TimeLeft { get; set; }
        public CancellationTokenSource? RoundCts { get; set; }

        public GarticLobby(string id, string name, string host, string hostDisplay, int maxPlayers, int roundCount, int roundTime)
        {
            LobbyId = id;
            LobbyName = name;
            Host = host;
            MaxPlayers = maxPlayers;
            RoundCount = roundCount;
            RoundTimeSeconds = roundTime;

            Players.Add(host);
            PlayerDisplayNames[host] = hostDisplay;
            Scores[host] = 0;
        }

        public void CancelRoundTimer()
        {
            RoundCts?.Cancel();
            RoundCts = null;
        }
    }

    // ── Word list ───────────────────────────────────────────────────────────

    public static class GarticWordList
    {
        private static readonly Random _rng = new();

        private static readonly string[] Words =
        {
            "cat", "dog", "house", "tree", "car", "sun", "moon", "star", "fish", "bird",
            "apple", "banana", "pizza", "guitar", "piano", "rocket", "airplane", "bicycle",
            "umbrella", "rainbow", "castle", "dragon", "robot", "flower", "butterfly",
            "mountain", "ocean", "island", "bridge", "train", "elephant", "penguin",
            "snowman", "candle", "camera", "diamond", "crown", "sword", "shield", "whale",
            "tornado", "volcano", "waterfall", "telescope", "mushroom", "cactus", "anchor",
            "balloon", "compass", "feather", "glasses", "hammer", "ladder", "lighthouse",
            "parachute", "sandwich", "treasure", "unicorn", "windmill", "zombie", "pirate",
            "ninja", "cowboy", "astronaut", "mermaid", "vampire", "witch", "ghost",
            "pumpkin", "snowflake", "thunder", "fire", "ice", "cloud", "rain", "heart",
            "skull", "trophy", "medal", "key", "lock", "book", "pencil", "scissors",
            "clock", "phone", "computer", "mouse", "keyboard", "headphones", "microphone",
            "television", "popcorn", "donut", "cupcake", "cookie", "chocolate", "cheese",
            "hamburger", "hotdog", "spaghetti", "taco", "sushi", "french fries",
            "ice cream", "birthday cake", "campfire", "tent", "fishing rod", "palm tree"
        };

        public static string GetRandomWord() => Words[_rng.Next(Words.Length)];
    }
}
