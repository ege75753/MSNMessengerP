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
                Math.Clamp(pkt.RoundTimeSeconds, 15, 120),
                string.IsNullOrWhiteSpace(pkt.Language) ? "en" : pkt.Language
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
                lobby.CurrentWord = GarticWordList.GetRandomWord(lobby.Language);
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
        public string Language { get; }
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

        public GarticLobby(string id, string name, string host, string hostDisplay, int maxPlayers, int roundCount, int roundTime, string language)
        {
            LobbyId = id;
            LobbyName = name;
            Host = host;
            MaxPlayers = maxPlayers;
            RoundCount = roundCount;
            RoundTimeSeconds = roundTime;
            Language = language;

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

        private static readonly string[] EnglishWords =
        {
            // Animals
            "cat","dog","fish","bird","horse","cow","pig","sheep","chicken","duck",
            "rabbit","mouse","snake","frog","turtle","dolphin","whale","shark","octopus","jellyfish",
            "crab","lobster","starfish","seahorse","penguin","eagle","owl","parrot","flamingo","peacock",
            "swan","crow","pigeon","sparrow","bat","bear","lion","tiger","elephant","giraffe",
            "zebra","monkey","gorilla","chimpanzee","panda","koala","kangaroo","crocodile","alligator","lizard",
            "chameleon","iguana","gecko","dinosaur","butterfly","bee","ant","spider","ladybug","dragonfly",
            "mosquito","fly","caterpillar","snail","worm","scorpion","beetle","grasshopper","cricket","firefly",
            "moth","wasp","centipede","squid","clam","oyster","seal","otter","walrus","moose",
            "deer","elk","reindeer","fox","wolf","coyote","raccoon","skunk","hedgehog","porcupine",
            "squirrel","chipmunk","hamster","guinea pig","ferret","goldfish","salmon","tuna","swordfish","stingray",
            // Food & Drink
            "apple","banana","orange","grape","strawberry","blueberry","raspberry","watermelon","pineapple","mango",
            "peach","pear","cherry","lemon","lime","coconut","avocado","tomato","potato","carrot",
            "broccoli","spinach","lettuce","cucumber","pepper","onion","garlic","mushroom","corn","pumpkin",
            "eggplant","celery","cabbage","cauliflower","asparagus","artichoke","radish","turnip","beet","zucchini",
            "pizza","hamburger","hotdog","sandwich","taco","burrito","sushi","pasta","spaghetti","lasagna",
            "steak","chicken","bacon","sausage","egg","pancake","waffle","toast","cereal","oatmeal",
            "soup","salad","rice","noodles","bread","bagel","croissant","muffin","donut","cupcake",
            "cake","pie","cookie","brownie","chocolate","candy","ice cream","popcorn","pretzel","chips",
            "french fries","onion rings","cheese","butter","yogurt","milk","juice","coffee","tea","water",
            "soda","lemonade","smoothie","milkshake","wine","beer","cocktail","honey","jam","peanut butter",
            // Household
            "house","apartment","building","skyscraper","castle","palace","cottage","cabin","tent","igloo",
            "door","window","wall","roof","floor","ceiling","stairs","elevator","chimney","fireplace",
            "kitchen","bedroom","bathroom","living room","dining room","garage","basement","attic","balcony","porch",
            "chair","table","desk","couch","bed","pillow","blanket","mattress","shelf","bookcase",
            "lamp","chandelier","candle","mirror","clock","alarm clock","calendar","picture frame","vase","curtain",
            "carpet","rug","broom","mop","vacuum","bucket","trash can","recycling bin","washing machine","dryer",
            "dishwasher","oven","stove","microwave","refrigerator","freezer","toaster","blender","mixer","kettle",
            "fork","knife","spoon","plate","bowl","cup","mug","glass","napkin","tablecloth",
            "soap","shampoo","toothbrush","toothpaste","towel","bathtub","shower","toilet","sink","faucet",
            "key","lock","doorbell","mailbox","fence","gate","garden","lawn","driveway","sidewalk",
            // Transportation
            "car","truck","bus","van","motorcycle","bicycle","scooter","skateboard","rollerblade","wagon",
            "taxi","ambulance","fire truck","police car","tractor","bulldozer","crane","excavator","forklift","tank",
            "airplane","helicopter","jet","rocket","spaceship","satellite","hot air balloon","parachute","glider","drone",
            "boat","ship","yacht","sailboat","canoe","kayak","raft","submarine","ferry","cruise ship",
            "train","subway","tram","monorail","cable car","gondola","sled","snowmobile","hovercraft","segway",
            // Body & Clothing
            "head","face","eye","nose","mouth","ear","hair","eyebrow","eyelash","chin",
            "neck","shoulder","arm","elbow","hand","finger","thumb","nail","chest","stomach",
            "back","hip","leg","knee","ankle","foot","toe","heel","muscle","bone",
            "heart","brain","lung","liver","kidney","blood","skin","tongue","tooth","lip",
            "shirt","pants","shorts","dress","skirt","jacket","coat","sweater","hoodie","vest",
            "suit","tie","bow tie","scarf","gloves","mittens","hat","cap","beanie","helmet",
            "shoes","boots","sandals","slippers","sneakers","socks","belt","buckle","zipper","button",
            "glasses","sunglasses","watch","bracelet","necklace","ring","earring","crown","tiara","headband",
            // Nature & Weather
            "sun","moon","star","planet","earth","sky","cloud","rain","snow","hail",
            "thunder","lightning","tornado","hurricane","earthquake","volcano","rainbow","sunrise","sunset","dawn",
            "mountain","hill","valley","cliff","canyon","cave","island","peninsula","desert","oasis",
            "ocean","sea","lake","river","stream","waterfall","pond","swamp","marsh","glacier",
            "forest","jungle","woods","meadow","field","prairie","tundra","savanna","beach","shore",
            "rock","boulder","pebble","sand","mud","dirt","soil","dust","crystal","diamond",
            "tree","flower","grass","bush","vine","leaf","branch","trunk","root","seed",
            "rose","daisy","sunflower","tulip","lily","orchid","dandelion","cactus","palm tree","bamboo",
            "pine tree","oak tree","maple tree","willow","fern","moss","mushroom","seaweed","coral","reef",
            // Objects & Tools
            "hammer","screwdriver","wrench","pliers","saw","drill","nail","screw","bolt","nut",
            "tape","glue","scissors","ruler","measuring tape","level","paintbrush","roller","ladder","stool",
            "rope","chain","wire","cable","pipe","hose","bucket","shovel","rake","wheelbarrow",
            "axe","pickaxe","chisel","file","sandpaper","clamp","vise","crowbar","jack","pulley",
            "pen","pencil","marker","crayon","eraser","sharpener","notebook","paper","envelope","stamp",
            "book","magazine","newspaper","dictionary","encyclopedia","atlas","map","globe","compass","magnifying glass",
            "camera","telescope","microscope","binoculars","thermometer","barometer","scale","calculator","computer","laptop",
            "tablet","phone","television","radio","speaker","headphones","microphone","remote control","battery","charger",
            "flashlight","lantern","lighter","match","candle","torch","spotlight","laser","prism","lens",
            "umbrella","fan","heater","air conditioner","humidifier","dehumidifier","thermostat","smoke detector","fire extinguisher","first aid kit",
            // Sports & Games
            "basketball","football","soccer","baseball","tennis","volleyball","golf","hockey","cricket","rugby",
            "bowling","boxing","wrestling","karate","fencing","archery","swimming","diving","surfing","skiing",
            "snowboarding","skateboarding","cycling","running","jogging","hiking","climbing","fishing","hunting","camping",
            "chess","checkers","dominoes","monopoly","scrabble","puzzle","crossword","sudoku","cards","dice",
            "billiards","darts","ping pong","badminton","squash","handball","polo","lacrosse","curling","bobsled",
            // Music & Art
            "guitar","piano","violin","drums","trumpet","flute","saxophone","clarinet","trombone","harmonica",
            "harp","banjo","ukulele","cello","accordion","tambourine","xylophone","triangle","maracas","cymbal",
            "painting","drawing","sculpture","pottery","origami","calligraphy","mosaic","mural","graffiti","sketch",
            "portrait","landscape","still life","abstract","watercolor","oil paint","acrylic","pastel","charcoal","canvas",
            // Professions
            "doctor","nurse","dentist","surgeon","veterinarian","pharmacist","paramedic","therapist","psychiatrist","optometrist",
            "teacher","professor","principal","librarian","tutor","coach","counselor","researcher","scientist","engineer",
            "architect","carpenter","electrician","plumber","mechanic","welder","painter","bricklayer","roofer","landscaper",
            "chef","baker","butcher","waiter","bartender","barista","sommelier","farmer","fisherman","rancher",
            "lawyer","judge","police officer","detective","firefighter","soldier","pilot","captain","astronaut","sailor",
            "actor","singer","dancer","musician","comedian","magician","clown","director","producer","photographer",
            // Fantasy & Fiction
            "dragon","unicorn","mermaid","fairy","elf","dwarf","giant","troll","goblin","ogre",
            "witch","wizard","vampire","werewolf","zombie","ghost","skeleton","mummy","pirate","ninja",
            "knight","king","queen","prince","princess","jester","peasant","monk","samurai","gladiator",
            "alien","robot","cyborg","android","spaceman","superhero","villain","monster","demon","angel",
            "phoenix","griffin","centaur","minotaur","pegasus","hydra","kraken","yeti","bigfoot","leprechaun",
            // Misc
            "birthday cake","campfire","tent","fishing rod","treasure chest","treasure map","magic wand","crystal ball","fortune cookie","genie lamp",
            "snowman","snowflake","icicle","igloo","avalanche","blizzard","fog","mist","dew","frost",
            "fire","smoke","ash","ember","flame","spark","explosion","fireworks","bonfire","lava",
            "anchor","compass","lighthouse","telescope","binoculars","magnifying glass","hourglass","sundial","pendulum","metronome",
            "trophy","medal","ribbon","badge","certificate","diploma","scroll","flag","banner","pennant",
            "balloon","kite","boomerang","frisbee","yo-yo","top","slingshot","trampoline","swing","slide",
            "wheel","gear","spring","magnet","battery","lightbulb","switch","plug","socket","wire",
            "gift","present","bow","wrapping paper","card","invitation","confetti","streamer","pinata","party hat",
            "mask","costume","cape","wand","shield","sword","armor","bow and arrow","spear","dagger",
            "map","compass","backpack","sleeping bag","lantern","binoculars","canteen","whistle","signal flare","life jacket"
        };

        private static readonly string[] TurkishWords =
        {
            // Hayvanlar
            "kedi","köpek","balık","kuş","at","inek","domuz","koyun","tavuk","ördek",
            "tavşan","fare","yılan","kurbağa","kaplumbağa","yunus","balina","köpekbalığı","ahtapot","denizanası",
            "yengeç","ıstakoz","denizyıldızı","denizatı","penguen","kartal","baykuş","papağan","flamingo","tavus kuşu",
            "kuğu","karga","güvercin","serçe","yarasa","ayı","aslan","kaplan","fil","zürafa",
            "zebra","maymun","goril","şempanze","panda","koala","kanguru","timsah","kertenkele","bukalemun",
            "iguana","dinozor","kelebek","arı","karınca","örümcek","uğur böceği","yusufçuk","sivrisinek","sinek",
            "tırtıl","salyangoz","solucan","akrep","böcek","çekirge","ateş böceği","güve","eşek arısı","kırkayak",
            "mürekkep balığı","midye","istiridye","fok","su samuru","mors","geyik","tilki","kurt","rakun",
            "kokarca","kirpi","oklu kirpi","sincap","hamster","kobay","gelincik","japon balığı","somon","kılıç balığı",
            "vatoz","deve","ceylan","antilop","bizon","gergedan","su aygırı","leopar","çita","jaguar",
            // Yiyecek ve İçecek
            "elma","muz","portakal","üzüm","çilek","karpuz","ananas","mango","şeftali","armut",
            "kiraz","limon","hindistan cevizi","avokado","domates","patates","havuç","brokoli","ıspanak","marul",
            "salatalık","biber","soğan","sarımsak","mantar","mısır","kabak","patlıcan","kereviz","lahana",
            "karnabahar","kuşkonmaz","turp","pancar","pizza","hamburger","sosisli","sandviç","taco","suşi",
            "makarna","spagetti","lazanya","biftek","tavuk","pastırma","sosis","yumurta","krep","gözleme",
            "çorba","salata","pilav","erişte","ekmek","simit","poğaça","börek","çörek","kurabiye",
            "pasta","turta","kek","browni","çikolata","şeker","dondurma","patlamış mısır","cips","peynir",
            "tereyağı","yoğurt","süt","meyve suyu","kahve","çay","su","limonata","ayran","şalgam",
            "bal","reçel","fıstık ezmesi","zeytin","zeytinyağı","sirke","hardal","ketçap","mayonez","sos",
            "baklava","künefe","kadayıf","lokum","helva","aşure","sütlaç","kazandibi","tavuk göğsü","muhallebi",
            // Ev ve Eşyalar
            "ev","apartman","bina","gökdelen","kale","saray","kulübe","çadır","kapı","pencere",
            "duvar","çatı","zemin","tavan","merdiven","asansör","baca","şömine","mutfak","yatak odası",
            "banyo","oturma odası","yemek odası","garaj","bodrum","çatı katı","balkon","veranda","sandalye","masa",
            "koltuk","yatak","yastık","battaniye","raf","kitaplık","lamba","avize","mum","ayna",
            "saat","çalar saat","takvim","çerçeve","vazo","perde","halı","kilim","süpürge","paspas",
            "elektrikli süpürge","kova","çöp kutusu","çamaşır makinesi","kurutma makinesi","bulaşık makinesi","fırın","ocak",
            "mikrodalga","buzdolabı","dondurucu","tost makinesi","blender","mikser","çaydanlık","çatal","bıçak","kaşık",
            "tabak","kase","bardak","fincan","peçete","sabun","şampuan","diş fırçası","diş macunu","havlu",
            "küvet","duş","klozet","lavabo","musluk","anahtar","kilit","kapı zili","posta kutusu","çit",
            "bahçe","çim","kaldırım","minder","sehpa","gardırop","komodin","şifonyer","vestiyer","etajer",
            // Ulaşım
            "araba","kamyon","otobüs","minibüs","motosiklet","bisiklet","scooter","kaykay","vagon","taksi",
            "ambulans","itfaiye","polis arabası","traktör","buldozer","vinç","kepçe","forklift","tank","uçak",
            "helikopter","roket","uzay gemisi","uydu","sıcak hava balonu","paraşüt","planör","drone","gemi","tekne",
            "yat","yelkenli","kano","kayık","sal","denizaltı","feribot","yolcu gemisi","tren","metro",
            "tramvay","teleferik","gondol","kızak","kar motoru","bisiklet","paten","at arabası","uçurtma","balonlu",
            // Vücut ve Giyim
            "kafa","yüz","göz","burun","ağız","kulak","saç","kaş","kirpik","çene",
            "boyun","omuz","kol","dirsek","el","parmak","başparmak","tırnak","göğüs","karın",
            "sırt","kalça","bacak","diz","ayak bileği","ayak","ayak parmağı","topuk","kas","kemik",
            "kalp","beyin","akciğer","karaciğer","böbrek","kan","deri","dil","diş","dudak",
            "gömlek","pantolon","şort","elbise","etek","ceket","mont","kazak","kapüşonlu","yelek",
            "takım elbise","kravat","papyon","atkı","eldiven","şapka","kep","bere","kask","ayakkabı",
            "çizme","sandalet","terlik","spor ayakkabı","çorap","kemer","toka","fermuar","düğme","gözlük",
            "güneş gözlüğü","kol saati","bilezik","kolye","yüzük","küpe","taç","taç","saç bandı","broş",
            // Doğa ve Hava
            "güneş","ay","yıldız","gezegen","dünya","gökyüzü","bulut","yağmur","kar","dolu",
            "gök gürültüsü","şimşek","kasırga","hortum","deprem","yanardağ","gökkuşağı","gün doğumu","gün batımı","şafak",
            "dağ","tepe","vadi","uçurum","kanyon","mağara","ada","yarımada","çöl","vaha",
            "okyanus","deniz","göl","nehir","dere","şelale","gölet","bataklık","buzul","orman",
            "tropikal orman","ağaçlık","çayır","tarla","step","tundra","kumsal","kıyı","kaya","taş",
            "çakıl","kum","çamur","toprak","toz","kristal","elmas","ağaç","çiçek","çimen",
            "çalı","sarmaşık","yaprak","dal","gövde","kök","tohum","gül","papatya","ayçiçeği",
            "lale","zambak","orkide","karahindiba","kaktüs","palmiye","bambu","çam","meşe","akçaağaç",
            "söğüt","eğrelti","yosun","deniz yosunu","mercan","kayalık","ova","yayla","bozkır","fundalık",
            // Nesneler ve Aletler
            "çekiç","tornavida","anahtar","pense","testere","matkap","çivi","vida","cıvata","somun",
            "bant","yapıştırıcı","makas","cetvel","şerit metre","su terazisi","boya fırçası","rulo","merdiven","tabure",
            "ip","zincir","tel","kablo","boru","hortum","kürek","tırmık","el arabası","balta",
            "kazma","keski","eğe","zımpara","mengene","levye","kriko","kasnak","kalem","kurşun kalem",
            "keçeli kalem","boya kalemi","silgi","kalemtıraş","defter","kağıt","zarf","pul","kitap","dergi",
            "gazete","sözlük","ansiklopedi","atlas","harita","küre","pusula","büyüteç","kamera","teleskop",
            "mikroskop","dürbün","termometre","barometre","terazi","hesap makinesi","bilgisayar","dizüstü bilgisayar",
            "tablet","telefon","televizyon","radyo","hoparlör","kulaklık","mikrofon","uzaktan kumanda","pil","şarj aleti",
            "el feneri","fener","çakmak","kibrit","mum","meşale","spot ışığı","lazer","prizma","mercek",
            "şemsiye","vantilatör","ısıtıcı","klima","nem giderici","termostat","duman dedektörü","yangın söndürücü","ilk yardım çantası","düdük",
            // Spor ve Oyunlar
            "basketbol","futbol","beyzbol","tenis","voleybol","golf","hokey","kriket","ragbi","bowling",
            "boks","güreş","karate","eskrim","okçuluk","yüzme","dalış","sörf","kayak","snowboard",
            "paten","bisiklet","koşu","yürüyüş","tırmanış","balıkçılık","kamp","satranç","dama","domino",
            "monopoly","yapboz","bulmaca","sudoku","kart","zar","bilardo","dart","masa tenisi","badminton",
            "squash","hentbol","polo","körling","halter","jimnastik","atletizm","triatlon","maraton","pentatlon",
            // Müzik ve Sanat
            "gitar","piyano","keman","davul","trompet","flüt","saksafon","klarnet","trombon","mızıka",
            "arp","ukulele","çello","akordeon","tef","ksilofon","üçgen","marakas","zil","def",
            "resim","çizim","heykel","seramik","origami","hat sanatı","mozaik","duvar resmi","grafiti","eskiz",
            "portre","manzara","natürmort","soyut","suluboya","yağlı boya","akrilik","pastel","kömür","tuval",
            // Meslekler
            "doktor","hemşire","diş hekimi","cerrah","veteriner","eczacı","paramedik","terapist","psikiyatrist","göz doktoru",
            "öğretmen","profesör","müdür","kütüphaneci","antrenör","danışman","araştırmacı","bilim insanı","mühendis","mimar",
            "marangoz","elektrikçi","tesisatçı","tamirci","kaynakçı","boyacı","duvarcı","çiftçi","balıkçı","çoban",
            "aşçı","fırıncı","kasap","garson","barmen","barista","avukat","hakim","polis","dedektif",
            "itfaiyeci","asker","pilot","kaptan","astronot","denizci","aktör","şarkıcı","dansçı","müzisyen",
            "komedyen","sihirbaz","palyaço","yönetmen","yapımcı","fotoğrafçı","gazeteci","editör","yazar","şair",
            // Fantazi ve Kurgu
            "ejderha","tek boynuzlu at","deniz kızı","peri","elf","cüce","dev","trol","goblin","cadı",
            "büyücü","vampir","kurt adam","zombi","hayalet","iskelet","mumya","korsan","ninja","şövalye",
            "kral","kraliçe","prens","prenses","soytarı","köylü","keşiş","samuray","gladyatör","uzaylı",
            "robot","süper kahraman","kötü adam","canavar","şeytan","melek","anka kuşu","grifon","sentaur",
            "minotaur","pegasus","hidra","kraken","yeti","kocaayak","cin","ifrit","devasa","titan",
            // Çeşitli
            "doğum günü pastası","kamp ateşi","olta","hazine sandığı","hazine haritası","sihirli değnek","kristal küre",
            "kardan adam","kar tanesi","buz sarkıtı","çığ","tipi","sis","çiğ","kırağı","buz",
            "ateş","duman","kül","kor","alev","kıvılcım","patlama","havai fişek","lav","volkan",
            "çapa","pusula","deniz feneri","dürbün","kum saati","güneş saati","sarkaç","metronom","bayrak","flama",
            "kupa","madalya","kurdele","rozet","sertifika","diploma","tomar","afiş","pankart","balon",
            "uçurtma","bumerang","frizbi","topaç","sapan","trambolin","salıncak","kaydırak","tahterevalli","dönme dolap",
            "tekerlek","dişli","yay","mıknatıs","ampul","anahtar","fiş","priz","hediye","paket",
            "kurdale","ambalaj kağıdı","davetiye","konfeti","süs","pinyata","parti şapkası","maske","kostüm","pelerin",
            "kalkan","kılıç","zırh","ok ve yay","mızrak","hançer","sırt çantası","uyku tulumu","matara","can yeleği",
            "nazar boncuğu","kilim","seccade","ibrik","çaydanlık","cezve","fincan","mangal","semaver","lale bahçesi"
        };

        public static string GetRandomWord(string language = "en") =>
            language == "tr"
                ? TurkishWords[_rng.Next(TurkishWords.Length)]
                : EnglishWords[_rng.Next(EnglishWords.Length)];
    }

}
