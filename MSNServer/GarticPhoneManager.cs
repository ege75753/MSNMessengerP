using MSNShared;

namespace MSNServer
{
    public class GarticPhoneManager
    {
        private readonly Func<string, ConnectedClient?> _getClient;
        private readonly Dictionary<string, GarticPhoneLobby> _lobbies = new();
        private readonly Dictionary<string, string> _playerLobby = new();
        private readonly object _lock = new();

        public GarticPhoneManager(Func<string, ConnectedClient?> getClient)
        {
            _getClient = getClient;
        }

        public async Task HandleAsync(ConnectedClient client, GarticPhonePacket pkt)
        {
            switch (pkt.Msg)
            {
                case GarticPhoneMsgType.CreateLobby:
                    await HandleCreateLobby(client, pkt);
                    break;
                case GarticPhoneMsgType.JoinLobby:
                    await HandleJoinLobby(client, pkt);
                    break;
                case GarticPhoneMsgType.LeaveLobby:
                    await HandleLeaveLobby(client);
                    break;
                case GarticPhoneMsgType.StartGame:
                    await HandleStartGame(client);
                    break;
                case GarticPhoneMsgType.SubmitPhrase:
                    await HandleSubmitPhrase(client, pkt);
                    break;
                case GarticPhoneMsgType.SubmitDrawing:
                    await HandleSubmitDrawing(client, pkt);
                    break;
                case GarticPhoneMsgType.SubmitDescription:
                    await HandleSubmitDescription(client, pkt);
                    break;
                case GarticPhoneMsgType.NextChain:
                    await HandleNextChain(client);
                    break;
            }
        }

        public List<GarticPhoneLobbyInfo> GetLobbies()
        {
            lock (_lock)
            {
                return _lobbies.Values.Select(l => new GarticPhoneLobbyInfo
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

        // ─── Create ───
        private async Task HandleCreateLobby(ConnectedClient client, GarticPhonePacket pkt)
        {
            GarticPhoneLobby lobby;
            lock (_lock)
            {
                if (_playerLobby.ContainsKey(client.Username!)) return;

                lobby = new GarticPhoneLobby(
                    Guid.NewGuid().ToString("N")[..10],
                    string.IsNullOrWhiteSpace(pkt.LobbyName) ? $"{client.DisplayName}'s Phone Game" : pkt.LobbyName,
                    client.Username!,
                    client.DisplayName,
                    Math.Clamp(pkt.MaxPlayers, 2, 12),
                    Math.Clamp(pkt.DrawTimeSeconds, 15, 120),
                    Math.Clamp(pkt.DescribeTimeSeconds, 10, 60),
                    string.IsNullOrWhiteSpace(pkt.Language) ? "en" : pkt.Language
                );

                _lobbies[lobby.LobbyId] = lobby;
                _playerLobby[client.Username!] = lobby.LobbyId;
            }

            await BroadcastLobbyState(lobby);
        }

        // ─── Join ───
        private async Task HandleJoinLobby(ConnectedClient client, GarticPhonePacket pkt)
        {
            GarticPhoneLobby lobby;
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

        // ─── Leave ───
        private async Task HandleLeaveLobby(ConnectedClient client)
        {
            await HandleLeaveLobbyInternal(client.Username!);
        }

        private async Task HandleLeaveLobbyInternal(string username)
        {
            GarticPhoneLobby? lobby = null;
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
                    lobby.CancelTimer();
                    _lobbies.Remove(lid);
                    removedLobby = true;
                }
                else if (lobby.Host == username)
                {
                    lobby.Host = lobby.Players[0];
                }
            }

            if (!removedLobby && lobby != null)
                await BroadcastLobbyState(lobby);
        }

        // ─── Start Game ───
        // Game flow:
        //   Phase 0 = "write" — everyone writes a phrase
        //   Phase 1 = "draw"  — draw someone else's phrase
        //   Phase 2 = "describe" — describe someone else's drawing
        //   Phase 3 = "draw"  — draw someone else's description
        //   Then → reveal all chains
        private async Task HandleStartGame(ConnectedClient client)
        {
            GarticPhoneLobby lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby!)) return;
                if (lobby.Host != client.Username || lobby.GameStarted) return;
                if (lobby.Players.Count < 2) return;

                lobby.GameStarted = true;
                lobby.CurrentPhase = 0;
                lobby.TotalPhases = 4; // write, draw, describe, draw
                lobby.InitializeForWritePhase();
            }

            await BroadcastLobbyState(lobby);

            // Send write phase to all players
            await SendWritePhase(lobby);
        }

        // ─── Phase 0: Write ───
        private async Task SendWritePhase(GarticPhoneLobby lobby)
        {
            foreach (var player in lobby.Players.ToList())
            {
                var c = _getClient(player);
                if (c == null) continue;

                await c.SendAsync(Packet.Create(PacketType.GarticPhone, new GarticPhonePacket
                {
                    Msg = GarticPhoneMsgType.PhaseState,
                    LobbyId = lobby.LobbyId,
                    PhaseType = "write",
                    PhaseIndex = 0,
                    TotalPhases = lobby.TotalPhases,
                    TimeLeft = lobby.DescribeTimeSeconds // use describe time for writing
                }));
            }

            StartPhaseTimer(lobby, lobby.DescribeTimeSeconds, "write");
        }

        private async Task HandleSubmitPhrase(ConnectedClient client, GarticPhonePacket pkt)
        {
            GarticPhoneLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted || lobby.CurrentPhase != 0) return;
                if (lobby.Submissions.Contains(client.Username!)) return;

                var phrase = string.IsNullOrWhiteSpace(pkt.Description) ? "something funny" : pkt.Description.Trim();

                // Create this player's chain with their phrase
                lobby.Chains.Add(new Chain
                {
                    Owner = client.Username!,
                    OwnerDisplay = client.DisplayName,
                    InitialWord = phrase
                });
                lobby.Submissions.Add(client.Username!);
            }

            await CheckPhaseComplete(lobby);
        }

        // ─── Phase 1 & 3: Draw ───
        private async Task HandleSubmitDrawing(ConnectedClient client, GarticPhonePacket pkt)
        {
            GarticPhoneLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted) return;

                var chainIdx = lobby.GetCurrentChainIndex(client.Username!);
                if (chainIdx < 0) return;
                if (lobby.Submissions.Contains(client.Username!)) return;

                lobby.Chains[chainIdx].Steps.Add(new GarticPhoneChainStep
                {
                    Player = client.Username!,
                    PlayerDisplay = client.DisplayName,
                    Type = "drawing",
                    Content = pkt.DrawingBase64
                });
                lobby.Submissions.Add(client.Username!);
            }

            await CheckPhaseComplete(lobby);
        }

        // ─── Phase 2: Describe ───
        private async Task HandleSubmitDescription(ConnectedClient client, GarticPhonePacket pkt)
        {
            GarticPhoneLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (!lobby.GameStarted) return;

                var chainIdx = lobby.GetCurrentChainIndex(client.Username!);
                if (chainIdx < 0) return;
                if (lobby.Submissions.Contains(client.Username!)) return;

                lobby.Chains[chainIdx].Steps.Add(new GarticPhoneChainStep
                {
                    Player = client.Username!,
                    PlayerDisplay = client.DisplayName,
                    Type = "description",
                    Content = pkt.Description
                });
                lobby.Submissions.Add(client.Username!);
            }

            await CheckPhaseComplete(lobby);
        }

        // ─── Phase Advancement ───
        private async Task CheckPhaseComplete(GarticPhoneLobby lobby)
        {
            bool allDone;
            lock (_lock)
            {
                allDone = lobby.Submissions.Count >= lobby.Players.Count;
            }

            if (allDone)
            {
                lobby.CancelTimer();
                await AdvanceToNextPhase(lobby);
            }
        }

        private async Task AdvanceToNextPhase(GarticPhoneLobby lobby)
        {
            int nextPhase;
            lock (_lock)
            {
                lobby.CurrentPhase++;
                lobby.Submissions.Clear();
                nextPhase = lobby.CurrentPhase;

                // After write phase (0), set up chain assignments
                if (nextPhase == 1)
                {
                    lobby.InitializeChainAssignments();
                }
                else if (nextPhase <= 3)
                {
                    lobby.RotateAssignments();
                }
            }

            if (nextPhase > 3)
            {
                // All 4 phases done → reveal
                await RevealChains(lobby);
                return;
            }

            // Determine phase type: 1=draw, 2=describe, 3=draw
            string phaseType = nextPhase == 2 ? "describe" : "draw";
            int timeLimit = phaseType == "draw" ? lobby.DrawTimeSeconds : lobby.DescribeTimeSeconds;

            // Send each player their content
            foreach (var player in lobby.Players.ToList())
            {
                var c = _getClient(player);
                if (c == null) continue;

                var chainIdx = lobby.GetCurrentChainIndex(player);
                if (chainIdx < 0) continue;
                var chain = lobby.Chains[chainIdx];

                var phasePacket = new GarticPhonePacket
                {
                    Msg = GarticPhoneMsgType.PhaseState,
                    LobbyId = lobby.LobbyId,
                    PhaseType = phaseType,
                    PhaseIndex = nextPhase,
                    TotalPhases = lobby.TotalPhases,
                    TimeLeft = timeLimit
                };

                if (phaseType == "draw")
                {
                    // Phase 1: draw the initial phrase; Phase 3: draw the description
                    var lastStep = chain.Steps.LastOrDefault();
                    phasePacket.Prompt = nextPhase == 1
                        ? chain.InitialWord              // draw the original phrase
                        : lastStep?.Content ?? "";        // draw the description from phase 2
                }
                else
                {
                    // Phase 2: describe — show the drawing from phase 1
                    var lastStep = chain.Steps.LastOrDefault();
                    phasePacket.DrawingBase64 = lastStep?.Content ?? "";
                }

                await c.SendAsync(Packet.Create(PacketType.GarticPhone, phasePacket));
            }

            StartPhaseTimer(lobby, timeLimit, phaseType);
        }

        // ─── Timer ───
        private void StartPhaseTimer(GarticPhoneLobby lobby, int seconds, string phaseType)
        {
            var cts = new CancellationTokenSource();
            lobby.TimerCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(seconds * 1000, cts.Token);

                    // Time's up — auto-submit for players who haven't
                    lock (_lock)
                    {
                        foreach (var player in lobby.Players.ToList())
                        {
                            if (lobby.Submissions.Contains(player)) continue;

                            if (lobby.CurrentPhase == 0)
                            {
                                // Write phase: auto-submit a default phrase
                                lobby.Chains.Add(new Chain
                                {
                                    Owner = player,
                                    OwnerDisplay = lobby.PlayerDisplayNames.GetValueOrDefault(player, player),
                                    InitialWord = "(no phrase entered)"
                                });
                            }
                            else
                            {
                                var ci = lobby.GetCurrentChainIndex(player);
                                if (ci >= 0)
                                {
                                    var displayName = lobby.PlayerDisplayNames.GetValueOrDefault(player, player);
                                    lobby.Chains[ci].Steps.Add(new GarticPhoneChainStep
                                    {
                                        Player = player,
                                        PlayerDisplay = displayName,
                                        Type = phaseType == "draw" ? "drawing" : "description",
                                        Content = phaseType == "draw" ? "" : "(no description)"
                                    });
                                }
                            }
                            lobby.Submissions.Add(player);
                        }
                    }

                    await AdvanceToNextPhase(lobby);
                }
                catch (TaskCanceledException) { }
            });
        }

        // ─── Reveal (host-controlled) ───
        private async Task RevealChains(GarticPhoneLobby lobby)
        {
            lock (_lock)
            {
                lobby.RevealChainIndex = 0;
                lobby.IsRevealing = true;
            }

            await SendCurrentChain(lobby);
        }

        private async Task HandleNextChain(ConnectedClient client)
        {
            GarticPhoneLobby? lobby;
            lock (_lock)
            {
                if (!_playerLobby.TryGetValue(client.Username!, out var lid)) return;
                if (!_lobbies.TryGetValue(lid, out lobby)) return;
                if (lobby.Host != client.Username || !lobby.IsRevealing) return;

                lobby.RevealChainIndex++;
            }

            if (lobby.RevealChainIndex >= lobby.Chains.Count)
            {
                // All chains revealed → game over
                lock (_lock)
                {
                    lobby.IsRevealing = false;
                }

                await BroadcastToLobby(lobby, new GarticPhonePacket
                {
                    Msg = GarticPhoneMsgType.GameOver,
                    LobbyId = lobby.LobbyId,
                    Message = "Game Over! Thanks for playing!"
                });

                // Reset lobby for new game
                lock (_lock)
                {
                    lobby.GameStarted = false;
                    lobby.CurrentPhase = 0;
                    lobby.Chains.Clear();
                    lobby.Submissions.Clear();
                    lobby.ChainAssignments.Clear();
                }
            }
            else
            {
                await SendCurrentChain(lobby);
            }
        }

        private async Task SendCurrentChain(GarticPhoneLobby lobby)
        {
            Chain chain;
            int index, total;
            lock (_lock)
            {
                index = lobby.RevealChainIndex;
                total = lobby.Chains.Count;
                chain = lobby.Chains[index];
            }

            var revealPkt = new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.ChainResult,
                LobbyId = lobby.LobbyId,
                ChainOwner = chain.Owner,
                ChainOwnerDisplay = chain.OwnerDisplay,
                ChainIndex = index,
                TotalChains = total,
                ChainSteps = new List<GarticPhoneChainStep>(chain.Steps),
                Players = new List<string>(lobby.Players),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames),
                Host = lobby.Host
            };

            // Prepend the initial phrase as the first step
            revealPkt.ChainSteps.Insert(0, new GarticPhoneChainStep
            {
                Player = chain.Owner,
                PlayerDisplay = chain.OwnerDisplay,
                Type = "phrase",
                Content = chain.InitialWord
            });

            await BroadcastToLobby(lobby, revealPkt);
        }

        // ─── Broadcast Helpers ───
        private async Task BroadcastLobbyState(GarticPhoneLobby lobby)
        {
            var data = new GarticPhonePacket
            {
                Msg = GarticPhoneMsgType.LobbyState,
                LobbyId = lobby.LobbyId,
                LobbyName = lobby.LobbyName,
                Host = lobby.Host,
                Players = new List<string>(lobby.Players),
                PlayerDisplayNames = new Dictionary<string, string>(lobby.PlayerDisplayNames),
                MaxPlayers = lobby.MaxPlayers,
                GameStarted = lobby.GameStarted,
                DrawTimeSeconds = lobby.DrawTimeSeconds,
                DescribeTimeSeconds = lobby.DescribeTimeSeconds,
                Language = lobby.Language
            };

            await BroadcastToLobby(lobby, data);
        }

        private async Task BroadcastToLobby(GarticPhoneLobby lobby, GarticPhonePacket data)
        {
            var pkt = Packet.Create(PacketType.GarticPhone, data);
            foreach (var player in lobby.Players.ToList())
            {
                var c = _getClient(player);
                if (c != null)
                    await c.SendAsync(pkt);
            }
        }
    }

    // ─── Lobby State ───
    public class GarticPhoneLobby
    {
        public string LobbyId { get; }
        public string LobbyName { get; }
        public string Host { get; set; }
        public int MaxPlayers { get; }
        public int DrawTimeSeconds { get; }
        public int DescribeTimeSeconds { get; }
        public string Language { get; }
        public List<string> Players { get; } = new();
        public Dictionary<string, string> PlayerDisplayNames { get; } = new();
        public bool GameStarted { get; set; }

        // Game state
        public int CurrentPhase { get; set; } = 0;
        public int TotalPhases { get; set; } = 4;
        public List<Chain> Chains { get; } = new();
        public HashSet<string> Submissions { get; } = new();
        public Dictionary<string, int> ChainAssignments { get; } = new();
        public CancellationTokenSource? TimerCts { get; set; }
        public int RevealChainIndex { get; set; } = 0;
        public bool IsRevealing { get; set; } = false;

        public GarticPhoneLobby(string id, string name, string host, string hostDisplay,
            int maxPlayers, int drawTime, int describeTime, string language)
        {
            LobbyId = id;
            LobbyName = name;
            Host = host;
            MaxPlayers = maxPlayers;
            DrawTimeSeconds = drawTime;
            DescribeTimeSeconds = describeTime;
            Language = language;

            Players.Add(host);
            PlayerDisplayNames[host] = hostDisplay;
        }

        public void CancelTimer()
        {
            TimerCts?.Cancel();
            TimerCts = null;
        }

        /// <summary>
        /// Called when game starts. Clears state for the write phase.
        /// Chains will be built as players submit their phrases.
        /// </summary>
        public void InitializeForWritePhase()
        {
            Chains.Clear();
            ChainAssignments.Clear();
            Submissions.Clear();
        }

        /// <summary>
        /// Called after write phase: assign each player to a different player's chain.
        /// Player i gets chain (i+1) % count — so nobody gets their own phrase.
        /// </summary>
        public void InitializeChainAssignments()
        {
            ChainAssignments.Clear();
            // Order chains by order in the Players list for consistency
            var orderedChains = new List<Chain>();
            foreach (var player in Players)
            {
                var chain = Chains.FirstOrDefault(c => c.Owner == player);
                if (chain != null) orderedChains.Add(chain);
            }
            Chains.Clear();
            Chains.AddRange(orderedChains);

            for (int i = 0; i < Players.Count; i++)
            {
                // Player i draws chain (i+1)%n — they get someone else's phrase
                ChainAssignments[Players[i]] = (i + 1) % Chains.Count;
            }
        }

        /// <summary>
        /// Rotate assignments so each player moves to the next chain.
        /// </summary>
        public void RotateAssignments()
        {
            var newAssignments = new Dictionary<string, int>();
            for (int i = 0; i < Players.Count; i++)
            {
                var player = Players[i];
                var currentChain = ChainAssignments[player];
                newAssignments[player] = (currentChain + 1) % Chains.Count;
            }
            ChainAssignments.Clear();
            foreach (var kv in newAssignments)
                ChainAssignments[kv.Key] = kv.Value;
        }

        public int GetCurrentChainIndex(string player)
        {
            return ChainAssignments.TryGetValue(player, out var idx) ? idx : -1;
        }
    }

    public class Chain
    {
        public string Owner { get; set; } = "";
        public string OwnerDisplay { get; set; } = "";
        public string InitialWord { get; set; } = "";
        public List<GarticPhoneChainStep> Steps { get; } = new();
    }
}
