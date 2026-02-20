using MSNShared;
using System.Collections.Concurrent;

namespace MSNServer
{
    public class PaintIoManager
    {
        private const int WIDTH = 50;
        private const int HEIGHT = 50;
        private const int TICK_MS = 150; // Update roughly 6-7 times per second

        private readonly string?[,] _map = new string?[WIDTH, HEIGHT]; // null = neutral
        private readonly ConcurrentDictionary<string, PaintIoPlayerState> _players = new();
        private readonly ConcurrentQueue<(string username, Direction dir)> _inputQueue = new();
        private readonly ConcurrentQueue<PaintIoMapUpdate> _pendingMapUpdates = new();
        private readonly Func<string, ConnectedClient?> _getClient;

        private bool _running;
        private Task? _gameLoop;
        private readonly object _stateLock = new();

        public PaintIoManager(Func<string, ConnectedClient?> getClient)
        {
            _getClient = getClient;
        }

        public async Task HandleAsync(ConnectedClient client, PaintIoPacket pkt)
        {
            switch (pkt.Msg)
            {
                case PaintIoMsgType.Join:
                    SpawnPlayer(client.Username!);
                    // Send initial state (map dimensions)
                    await client.SendAsync(Packet.Create(PacketType.PaintIo, new PaintIoPacket
                    {
                        Msg = PaintIoMsgType.GameInfo,
                        MapWidth = WIDTH,
                        MapHeight = HEIGHT
                    }));

                    // Send current map state to the new player
                    await SendInitialStateTo(client);

                    EnsureLoopRunning();
                    break;
                case PaintIoMsgType.Leave:
                    RemovePlayer(client.Username!);
                    break;
                case PaintIoMsgType.Input:
                    _inputQueue.Enqueue((client.Username!, pkt.Dir));
                    break;
            }
        }

        public async Task OnDisconnect(string username)
        {
            RemovePlayer(username);
            await Task.CompletedTask;
        }

        private void SpawnPlayer(string username)
        {
            // Remove existing player state if rejoining
            if (_players.TryRemove(username, out _))
            {
                lock (_stateLock)
                {
                    for (int x = 0; x < WIDTH; x++)
                        for (int y = 0; y < HEIGHT; y++)
                            if (_map[x, y] == username) _map[x, y] = null;
                }
            }

            var rnd = new Random();
            int sx, sy;
            // Find safe spawn (try up to 20 times to find an uncrowded spot)
            sx = rnd.Next(5, WIDTH - 5);
            sy = rnd.Next(5, HEIGHT - 5);

            // Use a distinct vibrant color
            var colors = new[] {
                "#E74C3C", "#3498DB", "#2ECC71", "#F39C12",
                "#9B59B6", "#1ABC9C", "#E67E22", "#E91E63",
                "#00BCD4", "#CDDC39", "#FF5722", "#607D8B"
            };
            var usedColors = _players.Values.Select(p => p.Color).ToHashSet();
            var color = colors.FirstOrDefault(c => !usedColors.Contains(c))
                        ?? $"#{rnd.Next(0x1000000):X6}";

            var p = new PaintIoPlayerState
            {
                Username = username,
                X = sx,
                Y = sy,
                Color = color,
                Dir = (Direction)rnd.Next(4)
            };

            // Give initial 3x3 territory and enqueue map updates so existing clients see it
            lock (_stateLock)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (IsValid(sx + dx, sy + dy))
                        {
                            _map[sx + dx, sy + dy] = username;
                            _pendingMapUpdates.Enqueue(new PaintIoMapUpdate { X = sx + dx, Y = sy + dy, Owner = username });
                        }
                    }
                _players[username] = p;
            }
        }

        private void RemovePlayer(string username)
        {
            if (_players.TryRemove(username, out _))
            {
                // Clear territory and broadcast the cleared cells to all clients
                lock (_stateLock)
                {
                    for (int x = 0; x < WIDTH; x++)
                        for (int y = 0; y < HEIGHT; y++)
                        {
                            if (_map[x, y] == username)
                            {
                                _map[x, y] = null;
                                _pendingMapUpdates.Enqueue(new PaintIoMapUpdate { X = x, Y = y, Owner = "" });
                            }
                        }
                }
            }
        }

        private void EnsureLoopRunning()
        {
            if (_running) return;
            _running = true;
            _gameLoop = Task.Run(GameLoop);
        }

        private async Task GameLoop()
        {
            while (_running && !_players.IsEmpty)
            {
                List<string> deadPlayers;
                lock (_stateLock)
                {
                    deadPlayers = new List<string>();
                    // Drain pending updates (from spawns/disconnects) into this tick's list
                    _lastMapUpdates = new List<PaintIoMapUpdate>();
                    while (_pendingMapUpdates.TryDequeue(out var pending))
                        _lastMapUpdates.Add(pending);
                    ProcessInputs();
                    UpdateState(deadPlayers);
                }

                // Notify dead players before removing them
                foreach (var dead in deadPlayers)
                {
                    var c = _getClient(dead);
                    if (c != null)
                    {
                        try { await c.SendAsync(Packet.Create(PacketType.PaintIo, new PaintIoPacket { Msg = PaintIoMsgType.Death })); } catch { }
                    }
                    RemovePlayer(dead);
                }

                await BroadcastState();
                await Task.Delay(TICK_MS);
            }
            _running = false;
        }

        private void ProcessInputs()
        {
            while (_inputQueue.TryDequeue(out var input))
            {
                if (_players.TryGetValue(input.username, out var p))
                {
                    // Prevent 180 turns
                    if (IsOpposite(p.Dir, input.dir)) continue;
                    p.NextDir = input.dir;
                }
            }
        }

        private bool IsOpposite(Direction d1, Direction d2)
        {
            return (d1 == Direction.Up && d2 == Direction.Down) ||
                   (d1 == Direction.Down && d2 == Direction.Up) ||
                   (d1 == Direction.Left && d2 == Direction.Right) ||
                   (d1 == Direction.Right && d2 == Direction.Left);
        }

        private void UpdateState(List<string> deadPlayers)
        {
            // Already inside lock (_stateLock) from GameLoop

            var mapUpdates = _lastMapUpdates;

            // Collect head positions for head-on collision detection
            var nextPositions = new Dictionary<string, (int nx, int ny)>();

            foreach (var p in _players.Values)
            {
                if (!p.IsAlive) continue;

                // Apply direction
                if (p.NextDir.HasValue)
                {
                    p.Dir = p.NextDir.Value;
                    p.NextDir = null;
                }

                int nx = p.X, ny = p.Y;
                switch (p.Dir)
                {
                    case Direction.Up: ny--; break;
                    case Direction.Down: ny++; break;
                    case Direction.Left: nx--; break;
                    case Direction.Right: nx++; break;
                }

                // Bounds check - Die if hit wall
                if (!IsValid(nx, ny))
                {
                    KillPlayer(p, deadPlayers);
                    continue;
                }

                nextPositions[p.Username] = (nx, ny);
            }

            // Head-on collision detection (two players moving to same cell)
            var positionGroups = nextPositions.GroupBy(kv => kv.Value);
            foreach (var group in positionGroups)
            {
                var players = group.ToList();
                if (players.Count > 1)
                {
                    // All players moving to same cell die
                    foreach (var kv in players)
                    {
                        if (_players.TryGetValue(kv.Key, out var pp) && pp.IsAlive)
                            KillPlayer(pp, deadPlayers);
                    }
                }
            }

            foreach (var p in _players.Values)
            {
                if (!p.IsAlive) continue;
                if (!nextPositions.TryGetValue(p.Username, out var next)) continue;

                int nx = next.nx, ny = next.ny;

                // 1. Hit own tail -> Die
                if (ContainsPoint(p.Trail, nx, ny))
                {
                    KillPlayer(p, deadPlayers);
                    continue;
                }

                // 2. Hit other player's tail -> That player dies, killer gets their territory
                string? tailOwner = FindTailOwner(nx, ny);
                if (tailOwner != null && tailOwner != p.Username)
                {
                    if (_players.TryGetValue(tailOwner, out var victim) && victim.IsAlive)
                    {
                        KillPlayer(victim, deadPlayers);
                        // Transfer victim's entire territory + trail to the killer
                        TransferTerritory(tailOwner, p.Username, mapUpdates);
                    }
                    // Current player survives - don't skip
                }

                // 3. Update Position
                p.X = nx;
                p.Y = ny;

                // Territory Logic
                string? currentOwner = _map[nx, ny];

                if (currentOwner == p.Username)
                {
                    // Inside own territory
                    if (p.Trail.Count > 0)
                    {
                        // Just returned! Claim territory.
                        ClaimTerritory(p, mapUpdates);
                    }
                }
                else
                {
                    // Outside -> Add to trail
                    p.Trail.Add(new[] { nx, ny });
                }
            }
        }

        private List<PaintIoMapUpdate> _lastMapUpdates = new();

        private void KillPlayer(PaintIoPlayerState p, List<string> dead)
        {
            if (!p.IsAlive) return;
            p.IsAlive = false;
            if (!dead.Contains(p.Username))
                dead.Add(p.Username);
        }

        /// <summary>
        /// Transfers all map cells owned by <paramref name="from"/> to <paramref name="to"/>.
        /// Also steals the victim's active trail. Must be called inside _stateLock.
        /// </summary>
        private void TransferTerritory(string from, string to, List<PaintIoMapUpdate> updates)
        {
            for (int x = 0; x < WIDTH; x++)
                for (int y = 0; y < HEIGHT; y++)
                    if (_map[x, y] == from)
                    {
                        _map[x, y] = to;
                        updates.Add(new PaintIoMapUpdate { X = x, Y = y, Owner = to });
                    }

            // Also absorb victim's active trail into the killer's territory immediately
            if (_players.TryGetValue(from, out var victim))
            {
                foreach (var t in victim.Trail)
                {
                    if (_map[t[0], t[1]] != to)
                    {
                        _map[t[0], t[1]] = to;
                        updates.Add(new PaintIoMapUpdate { X = t[0], Y = t[1], Owner = to });
                    }
                }
                victim.Trail.Clear();
            }
        }

        private string? FindTailOwner(int x, int y)
        {
            foreach (var p in _players.Values)
            {
                if (ContainsPoint(p.Trail, x, y)) return p.Username;
            }
            return null;
        }

        private bool ContainsPoint(List<int[]> points, int x, int y)
        {
            foreach (var pt in points) if (pt[0] == x && pt[1] == y) return true;
            return false;
        }

        private bool IsValid(int x, int y) => x >= 0 && x < WIDTH && y >= 0 && y < HEIGHT;

        private void ClaimTerritory(PaintIoPlayerState p, List<PaintIoMapUpdate> updates)
        {
            // Flood fill from map edges; anything unreachable (surrounded by player territory+trail) is captured.

            bool[,] solid = new bool[WIDTH, HEIGHT];
            bool[,] reachable = new bool[WIDTH, HEIGHT];

            // Mark solid: player's own territory AND their trail form walls
            for (int x = 0; x < WIDTH; x++)
                for (int y = 0; y < HEIGHT; y++)
                {
                    if (_map[x, y] == p.Username) solid[x, y] = true;
                }
            foreach (var t in p.Trail) solid[t[0], t[1]] = true;

            // BFS from edges
            var q = new Queue<(int x, int y)>();

            for (int x = 0; x < WIDTH; x++)
            {
                if (!solid[x, 0] && !reachable[x, 0]) { reachable[x, 0] = true; q.Enqueue((x, 0)); }
                if (!solid[x, HEIGHT - 1] && !reachable[x, HEIGHT - 1]) { reachable[x, HEIGHT - 1] = true; q.Enqueue((x, HEIGHT - 1)); }
            }
            for (int y = 0; y < HEIGHT; y++)
            {
                if (!solid[0, y] && !reachable[0, y]) { reachable[0, y] = true; q.Enqueue((0, y)); }
                if (!solid[WIDTH - 1, y] && !reachable[WIDTH - 1, y]) { reachable[WIDTH - 1, y] = true; q.Enqueue((WIDTH - 1, y)); }
            }

            int[] dx = { 0, 0, 1, -1 };
            int[] dy = { 1, -1, 0, 0 };

            while (q.Count > 0)
            {
                var (cx, cy) = q.Dequeue();
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];
                    if (IsValid(nx, ny) && !solid[nx, ny] && !reachable[nx, ny])
                    {
                        reachable[nx, ny] = true;
                        q.Enqueue((nx, ny));
                    }
                }
            }

            // Claim unreachable non-solid cells
            for (int x = 0; x < WIDTH; x++)
                for (int y = 0; y < HEIGHT; y++)
                {
                    if (!reachable[x, y] && !solid[x, y])
                    {
                        if (_map[x, y] != p.Username)
                        {
                            _map[x, y] = p.Username;
                            updates.Add(new PaintIoMapUpdate { X = x, Y = y, Owner = p.Username });
                        }
                    }
                }

            // Claim trail cells
            foreach (var t in p.Trail)
            {
                if (_map[t[0], t[1]] != p.Username)
                {
                    _map[t[0], t[1]] = p.Username;
                    updates.Add(new PaintIoMapUpdate { X = t[0], Y = t[1], Owner = p.Username });
                }
            }

            p.Trail.Clear();
        }

        private int CountTerritory(string username)
        {
            int count = 0;
            for (int x = 0; x < WIDTH; x++)
                for (int y = 0; y < HEIGHT; y++)
                    if (_map[x, y] == username) count++;
            return count;
        }

        private async Task BroadcastState()
        {
            List<PaintIoPlayer> players;
            List<PaintIoMapUpdate> mapUpdates;

            lock (_stateLock)
            {
                players = _players.Values.Select(p => new PaintIoPlayer
                {
                    Username = p.Username,
                    Color = p.Color,
                    X = p.X,
                    Y = p.Y,
                    Trail = p.Trail.ToList(),
                    Score = CountTerritory(p.Username)
                }).ToList();
                mapUpdates = _lastMapUpdates.ToList();
                _lastMapUpdates = new List<PaintIoMapUpdate>(); // clear after broadcasting
            }

            var pkt = new PaintIoPacket
            {
                Msg = PaintIoMsgType.State,
                Players = players,
                MapUpdates = mapUpdates
            };

            var data = Packet.Create(PacketType.PaintIo, pkt);

            var tasks = players.Select(p =>
            {
                var c = _getClient(p.Username);
                return c?.SendAsync(data) ?? Task.CompletedTask;
            });
            await Task.WhenAll(tasks);
        }

        private async Task SendInitialStateTo(ConnectedClient client)
        {
            var mapUpdates = new List<PaintIoMapUpdate>();
            List<PaintIoPlayer> players;

            lock (_stateLock)
            {
                for (int x = 0; x < WIDTH; x++)
                {
                    for (int y = 0; y < HEIGHT; y++)
                    {
                        if (_map[x, y] != null)
                        {
                            mapUpdates.Add(new PaintIoMapUpdate { X = x, Y = y, Owner = _map[x, y]! });
                        }
                    }
                }

                players = _players.Values.Select(p => new PaintIoPlayer
                {
                    Username = p.Username,
                    Color = p.Color,
                    X = p.X,
                    Y = p.Y,
                    Trail = p.Trail.ToList(),
                    Score = CountTerritory(p.Username)
                }).ToList();
            }

            var pkt = new PaintIoPacket
            {
                Msg = PaintIoMsgType.State,
                Players = players,
                MapUpdates = mapUpdates
            };

            await client.SendAsync(Packet.Create(PacketType.PaintIo, pkt));
        }
    }

    class PaintIoPlayerState
    {
        public string Username { get; set; } = "";
        public string Color { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public Direction Dir { get; set; }
        public Direction? NextDir { get; set; }
        public bool IsAlive { get; set; } = true;
        public List<int[]> Trail { get; set; } = new();
    }
}
