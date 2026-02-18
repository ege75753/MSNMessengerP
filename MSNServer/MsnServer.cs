using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BCrypt.Net;
using MSNShared;

namespace MSNServer
{
    public class MsnServer
    {
        private readonly int _port;
        private readonly int _discoveryPort;
        private readonly string _serverName;
        private readonly DataStore _store;
        private readonly FileStore _fileStore;
        private readonly TttManager _ttt;
        private readonly GarticManager _gartic;
        private TcpListener? _listener;
        private UdpClient? _discoveryUdp;
        private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
        private bool _running;

        public MsnServer(int port = 443, int discoveryPort = 443, string serverName = "MSN Messenger Server", string dataDir = "data")
        {
            _port = port;
            _discoveryPort = discoveryPort;
            _serverName = serverName;
            _store = new DataStore(dataDir);
            _fileStore = new FileStore(dataDir);
            _ttt = new TttManager(
                username => _clients.TryGetValue(username, out var c) ? c : null,
                username => { if (_clients.TryGetValue(username, out var c)) return BroadcastPresenceAsync(c, c.Status); return Task.CompletedTask; }
            );
            _gartic = new GarticManager(
                username => _clients.TryGetValue(username, out var c) ? c : null
            );
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();

            Log($"🟢 MSN Server started on port {_port}");
            Log($"🔍 LAN Discovery on UDP port {_discoveryPort}");
            Log($"📁 Data dir: {Path.GetFullPath("data")}");
            Log($"───────────────────────────────────");

            // Start UDP discovery responder
            _ = Task.Run(() => RunDiscoveryAsync(ct), ct);

            // Start ping loop
            _ = Task.Run(() => PingLoopAsync(ct), ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(ct);
                    tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    tcpClient.NoDelay = true;
                    var client = new ConnectedClient(tcpClient);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"❌ Listener error: {ex.Message}"); }

            _running = false;
            _listener.Stop();
        }

        private async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
        {
            var remote = client.TcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Log($"📥 New connection from {remote}");

            var buffer = new StringBuilder();

            try
            {
                var buf = new byte[65536];  // 64KB chunks for file transfer support
                while (!ct.IsCancellationRequested && client.TcpClient.Connected)
                {
                    int read;
                    try { read = await client.Stream.ReadAsync(buf, ct); }
                    catch { break; }

                    if (read == 0) break;

                    buffer.Append(Encoding.UTF8.GetString(buf, 0, read));

                    // Process all complete lines (newline-delimited JSON)
                    string data = buffer.ToString();
                    int newline;
                    while ((newline = data.IndexOf('\n')) >= 0)
                    {
                        var line = data[..newline].Trim();
                        data = data[(newline + 1)..];
                        if (!string.IsNullOrEmpty(line))
                        {
                            var packet = Packet.Deserialize(line);
                            if (packet != null)
                                await HandlePacketAsync(client, packet);
                        }
                    }
                    buffer.Clear();
                    buffer.Append(data);
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️  Client error [{client.Username ?? remote}]: {ex.Message}");
            }
            finally
            {
                await DisconnectClientAsync(client);
            }
        }

        private async Task HandlePacketAsync(ConnectedClient client, Packet packet)
        {
            switch (packet.Type)
            {
                case PacketType.Ping:
                    await client.SendAsync(Packet.Create(PacketType.Pong, new { }));
                    break;

                case PacketType.Register:
                    await HandleRegisterAsync(client, packet);
                    break;

                case PacketType.Login:
                    await HandleLoginAsync(client, packet);
                    break;

                case PacketType.Logout:
                    await DisconnectClientAsync(client);
                    break;

                case PacketType.PresenceUpdate when client.IsAuthenticated:
                    await HandlePresenceUpdateAsync(client, packet);
                    break;

                case PacketType.ChatMessage when client.IsAuthenticated:
                    await HandleChatMessageAsync(client, packet);
                    break;

                case PacketType.ChatTyping when client.IsAuthenticated:
                    await HandleTypingAsync(client, packet);
                    break;

                case PacketType.Nudge when client.IsAuthenticated:
                    await HandleNudgeAsync(client, packet);
                    break;

                case PacketType.CreateGroup when client.IsAuthenticated:
                    await HandleCreateGroupAsync(client, packet);
                    break;

                case PacketType.InviteToGroup when client.IsAuthenticated:
                    await HandleInviteToGroupAsync(client, packet);
                    break;

                case PacketType.JoinGroup when client.IsAuthenticated:
                    await HandleJoinGroupAsync(client, packet);
                    break;

                case PacketType.LeaveGroup when client.IsAuthenticated:
                    await HandleLeaveGroupAsync(client, packet);
                    break;

                case PacketType.GroupMessage when client.IsAuthenticated:
                    await HandleGroupMessageAsync(client, packet);
                    break;

                case PacketType.AddContact when client.IsAuthenticated:
                    await HandleAddContactAsync(client, packet);
                    break;

                case PacketType.RemoveContact when client.IsAuthenticated:
                    await HandleRemoveContactAsync(client, packet);
                    break;

                case PacketType.FileSend when client.IsAuthenticated:
                    await HandleFileSendAsync(client, packet);
                    break;

                case PacketType.FileRequest when client.IsAuthenticated:
                    await HandleFileRequestAsync(client, packet);
                    break;

                case PacketType.ProfilePictureUpdate when client.IsAuthenticated:
                    await HandleProfilePictureUpdateAsync(client, packet);
                    break;

                case PacketType.RequestProfilePic when client.IsAuthenticated:
                    await HandleRequestProfilePicAsync(client, packet);
                    break;

                case PacketType.TicTacToe when client.IsAuthenticated:
                    var tttPkt = packet.GetData<TttPacket>();
                    if (tttPkt != null) await _ttt.HandleAsync(client, tttPkt);
                    break;

                case PacketType.TttListGames when client.IsAuthenticated:
                    var games = _ttt.GetActiveGames();
                    await client.SendAsync(Packet.Create(PacketType.TttGameList, games));
                    break;

                case PacketType.Gartic when client.IsAuthenticated:
                    var garticPkt = packet.GetData<GarticPacket>();
                    if (garticPkt != null) await _gartic.HandleAsync(client, garticPkt);
                    break;

                case PacketType.GarticLobbyList when client.IsAuthenticated:
                    var lobbies = _gartic.GetLobbies();
                    await client.SendAsync(Packet.Create(PacketType.GarticLobbies, lobbies));
                    break;

                default:
                    if (!client.IsAuthenticated)
                        await client.SendAsync(Packet.Create(PacketType.Error,
                            new ErrorData { Code = "AUTH_REQUIRED", Message = "Please log in first." }));
                    break;
            }
        }

        private async Task HandleRegisterAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<RegisterData>();
            if (data is null) return;

            data.Username = data.Username.ToLower().Trim();

            if (string.IsNullOrWhiteSpace(data.Username) || data.Username.Length < 3)
            {
                await client.SendAsync(Packet.Create(PacketType.RegisterAck,
                    new RegisterAckData { Success = false, Message = "Username must be at least 3 characters." }));
                return;
            }

            if (string.IsNullOrWhiteSpace(data.Password) || data.Password.Length < 4)
            {
                await client.SendAsync(Packet.Create(PacketType.RegisterAck,
                    new RegisterAckData { Success = false, Message = "Password must be at least 4 characters." }));
                return;
            }

            var hash = BCrypt.Net.BCrypt.HashPassword(data.Password);
            var ok = _store.RegisterUser(data.Username, hash,
                string.IsNullOrWhiteSpace(data.DisplayName) ? data.Username : data.DisplayName,
                data.Email);


            if (ok)
            {
                Log($"✅ Registered: {data.Username}");
                await client.SendAsync(Packet.Create(PacketType.RegisterAck,
                    new RegisterAckData { Success = true, Message = "Account created! You can now log in." }));
            }
            else
            {
                await client.SendAsync(Packet.Create(PacketType.RegisterAck,
                    new RegisterAckData { Success = false, Message = "Username already taken." }));
            }
        }

        private async Task HandleLoginAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<LoginData>();
            if (data is null) return;

            data.Username = data.Username.ToLower().Trim();
            var stored = _store.GetUser(data.Username);

            if (stored is null || !BCrypt.Net.BCrypt.Verify(data.Password, stored.PasswordHash))
            {
                await client.SendAsync(Packet.Create(PacketType.LoginAck,
                    new LoginAckData { Success = false, Message = "Invalid username or password." }));
                return;
            }

            // Kick existing session
            if (_clients.TryGetValue(data.Username, out var existing))
            {
                await existing.SendAsync(Packet.Create(PacketType.Error,
                    new ErrorData { Code = "KICKED", Message = "You signed in from another location." }));
                existing.Close();
                _clients.TryRemove(data.Username, out _);
            }

            client.Username = data.Username;
            client.SessionId = Guid.NewGuid().ToString("N");
            client.DisplayName = string.IsNullOrWhiteSpace(data.DisplayName) ? stored.DisplayName : data.DisplayName;
            client.Status = data.Status;
            client.PersonalMessage = data.PersonalMessage;

            _clients[data.Username] = client;

            Log($"🔑 Login: {client.Username} ({client.DisplayName}) from {client.TcpClient.Client.RemoteEndPoint}");

            // Send full user/group list
            var userInfos = _clients.Values
                .Where(c => c.IsAuthenticated)
                .Select(c => c.ToUserInfo(_store))
                .ToList();

            // Also include offline contacts of this user
            foreach (var contactName in stored.Contacts)
            {
                if (!userInfos.Any(u => u.Username == contactName))
                {
                    var s = _store.GetUser(contactName);
                    if (s != null)
                        userInfos.Add(new UserInfo
                        {
                            Username = s.Username,
                            DisplayName = s.DisplayName,
                            Email = s.Email,
                            Status = UserStatus.Offline,
                            AvatarEmoji = s.AvatarEmoji,
                            Contacts = s.Contacts,
                            Groups = s.Groups
                        });
                }
            }

            var groups = stored.Groups
                .Where(gid => _store.Groups.ContainsKey(gid))
                .Select(gid => _store.Groups[gid])
                .ToList();

            await client.SendAsync(Packet.Create(PacketType.LoginAck, new LoginAckData
            {
                Success = true,
                Message = "Welcome!",
                SessionId = client.SessionId,
                User = client.ToUserInfo(_store)
            }));

            await client.SendAsync(Packet.Create(PacketType.UserList, new UserListData
            {
                Users = userInfos,
                Groups = groups
            }));

            // Broadcast presence to others
            await BroadcastPresenceAsync(client, UserStatus.Online);
        }

        private async Task HandlePresenceUpdateAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<PresenceData>();
            if (data is null) return;

            client.Status = data.Status;
            client.PersonalMessage = data.PersonalMessage;
            if (!string.IsNullOrEmpty(data.DisplayName)) client.DisplayName = data.DisplayName;
            if (!string.IsNullOrEmpty(data.AvatarEmoji)) client.AvatarEmoji = data.AvatarEmoji;

            await BroadcastPresenceAsync(client, data.Status);
        }

        private async Task BroadcastPresenceAsync(ConnectedClient client, UserStatus status)
        {
            var storedUser = _store.GetUser(client.Username!);
            // Show game status in personal message while in a game
            var gameOpponent = _ttt.GetGameStatus(client.Username!);
            var personalMsg = gameOpponent != null
                ? $"🎮 Playing Tic-Tac-Toe with {gameOpponent}"
                : client.PersonalMessage;
            var inGame = gameOpponent != null;

            var presence = Packet.Create(PacketType.PresenceBroadcast, new PresenceData
            {
                Username = client.Username!,
                Status = status,
                PersonalMessage = personalMsg,
                DisplayName = client.DisplayName,
                AvatarEmoji = client.AvatarEmoji,
                HasProfilePicture = !string.IsNullOrEmpty(storedUser?.ProfilePicFileId),
                ProfilePicFileId = storedUser?.ProfilePicFileId ?? "",
                IsInGame = inGame,
                GameId = _ttt.GetGameId(client.Username!) ?? ""
            });

            await BroadcastToAllAsync(presence, except: null);
        }

        private async Task HandleChatMessageAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<ChatMessageData>();
            if (data is null) return;

            data.From = client.Username!;

            if (!_clients.TryGetValue(data.To, out var target))
            {
                await client.SendAsync(Packet.Create(PacketType.Error,
                    new ErrorData { Code = "USER_OFFLINE", Message = $"{data.To} is offline." }));
                return;
            }

            await target.SendAsync(Packet.Create(PacketType.ChatMessage, data));
            await client.SendAsync(Packet.Create(PacketType.ChatMessageDelivered, new { id = packet.Id }));
        }

        private async Task HandleTypingAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<TypingData>();
            if (data is null) return;
            data.From = client.Username!;

            if (data.IsGroup)
            {
                if (!_store.Groups.TryGetValue(data.To, out var group)) return;
                foreach (var member in group.Members.Where(m => m != client.Username))
                {
                    if (_clients.TryGetValue(member, out var mc))
                        await mc.SendAsync(Packet.Create(PacketType.ChatTyping, data));
                }
            }
            else
            {
                if (_clients.TryGetValue(data.To, out var target))
                    await target.SendAsync(Packet.Create(PacketType.ChatTyping, data));
            }
        }

        private async Task HandleNudgeAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<NudgeData>();
            if (data is null) return;
            data.From = client.Username!;
            if (_clients.TryGetValue(data.To, out var target))
                await target.SendAsync(Packet.Create(PacketType.Nudge, data));
        }

        private async Task HandleCreateGroupAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<CreateGroupData>();
            if (data is null) return;

            var members = data.InitialMembers.Select(m => m.ToLower()).ToList();
            var group = _store.CreateGroup(data.Name, data.Description, client.Username!, members);

            Log($"👥 Group created: '{group.Name}' by {client.Username} ({group.Members.Count} members)");

            // Notify all members
            foreach (var member in group.Members)
            {
                if (_clients.TryGetValue(member, out var mc))
                {
                    if (member == client.Username)
                    {
                        await mc.SendAsync(Packet.Create(PacketType.CreateGroupAck, group));
                    }
                    else
                    {
                        await mc.SendAsync(Packet.Create(PacketType.GroupInviteReceived, new GroupInviteData
                        {
                            GroupId = group.Id,
                            GroupName = group.Name,
                            InvitedBy = client.Username!
                        }));
                        // Also send full group info
                        await mc.SendAsync(Packet.Create(PacketType.CreateGroupAck, group));
                    }
                }
            }
        }

        private async Task HandleInviteToGroupAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<InviteToGroupData>();
            if (data is null) return;

            if (!_store.Groups.TryGetValue(data.GroupId, out var group)) return;
            if (group.Owner != client.Username) return; // Only owner can invite

            var target = data.Username.ToLower();
            _store.AddMemberToGroup(data.GroupId, target);

            if (_clients.TryGetValue(target, out var tc))
            {
                await tc.SendAsync(Packet.Create(PacketType.GroupInviteReceived, new GroupInviteData
                {
                    GroupId = group.Id,
                    GroupName = group.Name,
                    InvitedBy = client.Username!
                }));
                await tc.SendAsync(Packet.Create(PacketType.CreateGroupAck, group));
            }

            // Notify group of new member
            await BroadcastToGroupAsync(data.GroupId, Packet.Create(PacketType.GroupMemberUpdate,
                new JoinLeaveGroupData { GroupId = data.GroupId, Username = target, Joined = true }));
        }

        private async Task HandleJoinGroupAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<JoinLeaveGroupData>();
            if (data is null) return;

            _store.AddMemberToGroup(data.GroupId, client.Username!);
            await BroadcastToGroupAsync(data.GroupId, Packet.Create(PacketType.GroupMemberUpdate,
                new JoinLeaveGroupData { GroupId = data.GroupId, Username = client.Username!, Joined = true }));
        }

        private async Task HandleLeaveGroupAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<JoinLeaveGroupData>();
            if (data is null) return;

            _store.RemoveMemberFromGroup(data.GroupId, client.Username!);
            await BroadcastToGroupAsync(data.GroupId, Packet.Create(PacketType.GroupMemberUpdate,
                new JoinLeaveGroupData { GroupId = data.GroupId, Username = client.Username!, Joined = false }));
            await client.SendAsync(Packet.Create(PacketType.GroupMemberUpdate,
                new JoinLeaveGroupData { GroupId = data.GroupId, Username = client.Username!, Joined = false }));
        }

        private async Task HandleGroupMessageAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<GroupMessageData>();
            if (data is null) return;
            data.From = client.Username!;

            if (!_store.Groups.TryGetValue(data.GroupId, out var group)) return;
            data.GroupName = group.Name;

            // Send to all online members except sender
            foreach (var member in group.Members.Where(m => m != client.Username))
            {
                if (_clients.TryGetValue(member, out var mc))
                    await mc.SendAsync(Packet.Create(PacketType.GroupMessage, data));
            }
        }

        private async Task HandleAddContactAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<ContactRequestData>();
            if (data is null) return;

            var targetName = data.From; // reusing From field as target here
            var target = _store.GetUser(targetName.ToLower());
            if (target is null)
            {
                await client.SendAsync(Packet.Create(PacketType.Error,
                    new ErrorData { Code = "USER_NOT_FOUND", Message = $"User '{targetName}' not found." }));
                return;
            }

            _store.AddContact(client.Username!, targetName);

            // Notify target if online
            if (_clients.TryGetValue(targetName.ToLower(), out var tc))
            {
                var storedSelf = _store.GetUser(client.Username!);
                await tc.SendAsync(Packet.Create(PacketType.ContactRequest, new ContactRequestData
                {
                    From = client.Username!,
                    FromDisplayName = client.DisplayName
                }));
            }

            // Send updated user info back
            var userInfo = new UserInfo
            {
                Username = target.Username,
                DisplayName = target.DisplayName,
                Email = target.Email,
                Status = _clients.TryGetValue(target.Username, out var oc) ? oc.Status : UserStatus.Offline,
                PersonalMessage = _clients.TryGetValue(target.Username, out var oc2) ? oc2.PersonalMessage : "",
                AvatarEmoji = target.AvatarEmoji
            };
            await client.SendAsync(Packet.Create(PacketType.PresenceBroadcast, new PresenceData
            {
                Username = userInfo.Username,
                DisplayName = userInfo.DisplayName,
                Status = userInfo.Status,
                PersonalMessage = userInfo.PersonalMessage,
                AvatarEmoji = userInfo.AvatarEmoji
            }));
        }

        private async Task HandleRemoveContactAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<ContactRequestData>();
            if (data is null) return;
            _store.RemoveContact(client.Username!, data.From);
        }

        private async Task HandleFileSendAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<FileSendData>();
            if (data is null) return;

            // Decode and validate
            byte[] fileBytes;
            try { fileBytes = Convert.FromBase64String(data.DataBase64); }
            catch
            {
                await client.SendAsync(Packet.Create(PacketType.FileSendAck,
                    new FileSendAckData { Success = false, Message = "Invalid file data." }));
                return;
            }

            if (fileBytes.Length > FileStore.MaxFileSizeBytes)
            {
                await client.SendAsync(Packet.Create(PacketType.FileSendAck,
                    new FileSendAckData { Success = false, Message = $"File too large. Max {FileStore.MaxFileSizeBytes / 1024 / 1024} MB." }));
                return;
            }

            var fileId = Guid.NewGuid().ToString("N")[..16];
            var mime = string.IsNullOrEmpty(data.MimeType) ? MSNShared.MimeTypes.FromFileName(data.FileName) : data.MimeType;

            var stored = await _fileStore.StoreAsync(fileId, data.FileName, mime, fileBytes, client.Username!);
            if (stored is null)
            {
                await client.SendAsync(Packet.Create(PacketType.FileSendAck,
                    new FileSendAckData { Success = false, Message = "Failed to store file on server." }));
                return;
            }

            Log($"📁 File stored: '{data.FileName}' ({MSNShared.MimeTypes.FriendlySize(fileBytes.Length)}) id={fileId} from {client.Username}");

            await client.SendAsync(Packet.Create(PacketType.FileSendAck,
                new FileSendAckData { Success = true, FileId = fileId }));

            // Build notification for recipients — inline data for small images
            var inline = (MSNShared.MimeTypes.IsImage(mime) && fileBytes.Length <= FileStore.InlineThresholdBytes)
                ? data.DataBase64 : null;

            var fromUser = _store.GetUser(client.Username!);

            var receivePayload = new FileReceiveData
            {
                From = client.Username!,
                FromDisplayName = client.DisplayName,
                FileId = fileId,
                FileName = data.FileName,
                FileSize = fileBytes.Length,
                MimeType = mime,
                IsGroup = data.IsGroup,
                GroupId = data.IsGroup ? data.To : "",
                InlineDataBase64 = inline
            };

            if (data.IsGroup)
            {
                // Broadcast to all group members except sender
                if (!_store.Groups.TryGetValue(data.To, out var group)) return;
                receivePayload.GroupName = group.Name;
                foreach (var member in group.Members.Where(m => m != client.Username))
                {
                    if (_clients.TryGetValue(member, out var mc))
                        await mc.SendAsync(Packet.Create(PacketType.FileReceive, receivePayload));
                }
            }
            else
            {
                if (!_clients.TryGetValue(data.To, out var target))
                {
                    // Recipient offline — file is still stored, they can fetch later (future feature)
                    return;
                }
                await target.SendAsync(Packet.Create(PacketType.FileReceive, receivePayload));
            }
        }

        private async Task HandleFileRequestAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<FileRequestData>();
            if (data is null) return;

            var meta = _fileStore.GetMeta(data.FileId);
            if (meta is null)
            {
                await client.SendAsync(Packet.Create(PacketType.FileData, new FileDataResponse
                {
                    FileId = data.FileId,
                    Found = false
                }));
                return;
            }

            var bytes = await _fileStore.ReadAsync(data.FileId);
            await client.SendAsync(Packet.Create(PacketType.FileData, new FileDataResponse
            {
                FileId = data.FileId,
                FileName = meta.FileName,
                MimeType = meta.MimeType,
                DataBase64 = bytes != null ? Convert.ToBase64String(bytes) : "",
                Found = bytes != null
            }));
        }

        private async Task HandleProfilePictureUpdateAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<ProfilePictureUpdateData>();
            if (data is null) return;

            byte[] imageBytes;
            try { imageBytes = Convert.FromBase64String(data.DataBase64); }
            catch
            {
                await client.SendAsync(Packet.Create(PacketType.ProfilePictureAck,
                    new ProfilePictureAckData { Success = false, Message = "Invalid image data." }));
                return;
            }

            // Max 5MB for profile pictures
            if (imageBytes.Length > 5 * 1024 * 1024)
            {
                await client.SendAsync(Packet.Create(PacketType.ProfilePictureAck,
                    new ProfilePictureAckData { Success = false, Message = "Profile picture too large (max 5 MB)." }));
                return;
            }

            // Delete old profile picture if exists
            var storedUser = _store.GetUser(client.Username!);
            if (storedUser != null && !string.IsNullOrEmpty(storedUser.ProfilePicFileId))
                _fileStore.Delete(storedUser.ProfilePicFileId);

            var fileId = $"pfp_{client.Username}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var mime = string.IsNullOrEmpty(data.MimeType) ? "image/png" : data.MimeType;
            var stored = await _fileStore.StoreAsync(fileId, $"pfp_{client.Username}.png", mime, imageBytes, client.Username!);

            if (stored is null)
            {
                await client.SendAsync(Packet.Create(PacketType.ProfilePictureAck,
                    new ProfilePictureAckData { Success = false, Message = "Failed to store profile picture." }));
                return;
            }

            // Update user record
            _store.SetProfilePicture(client.Username!, fileId);

            Log($"🖼️  Profile picture updated for {client.Username} ({MSNShared.MimeTypes.FriendlySize(imageBytes.Length)})");

            await client.SendAsync(Packet.Create(PacketType.ProfilePictureAck,
                new ProfilePictureAckData { Success = true, FileId = fileId }));

            // Broadcast updated presence so everyone knows there's now a profile pic
            await BroadcastPresenceAsync(client, client.Status);
        }

        private async Task HandleRequestProfilePicAsync(ConnectedClient client, Packet packet)
        {
            var data = packet.GetData<RequestProfilePicData>();
            if (data is null) return;

            var user = _store.GetUser(data.Username);
            if (user is null || string.IsNullOrEmpty(user.ProfilePicFileId))
            {
                await client.SendAsync(Packet.Create(PacketType.ProfilePicData, new ProfilePicDataResponse
                {
                    Username = data.Username,
                    Found = false
                }));
                return;
            }

            var meta = _fileStore.GetMeta(user.ProfilePicFileId);
            var bytes = await _fileStore.ReadAsync(user.ProfilePicFileId);

            await client.SendAsync(Packet.Create(PacketType.ProfilePicData, new ProfilePicDataResponse
            {
                Username = data.Username,
                FileId = user.ProfilePicFileId,
                MimeType = meta?.MimeType ?? "image/png",
                DataBase64 = bytes != null ? Convert.ToBase64String(bytes) : "",
                Found = bytes != null
            }));
        }

        private async Task DisconnectClientAsync(ConnectedClient client)
        {
            if (client.Username != null)
            {
                _clients.TryRemove(client.Username, out _);
                Log($"🔴 Disconnected: {client.Username}");

                // Handle any ongoing game
                await _ttt.OnDisconnect(client.Username);
                await _gartic.OnDisconnect(client.Username);

                // Broadcast offline
                await BroadcastToAllAsync(Packet.Create(PacketType.PresenceBroadcast, new PresenceData
                {
                    Username = client.Username,
                    Status = UserStatus.Offline,
                    DisplayName = client.DisplayName
                }));
            }
            client.Close();
        }

        private async Task BroadcastToAllAsync(Packet packet, string? except = null)
        {
            var tasks = _clients.Values
                .Where(c => c.IsAuthenticated && c.Username != except)
                .Select(c => c.SendAsync(packet));
            await Task.WhenAll(tasks);
        }

        private async Task BroadcastToGroupAsync(string groupId, Packet packet)
        {
            if (!_store.Groups.TryGetValue(groupId, out var group)) return;
            var tasks = group.Members
                .Where(m => _clients.ContainsKey(m))
                .Select(m => _clients[m].SendAsync(packet));
            await Task.WhenAll(tasks);
        }

        private async Task RunDiscoveryAsync(CancellationToken ct)
        {
            try
            {
                _discoveryUdp = new UdpClient(_discoveryPort);
                _discoveryUdp.EnableBroadcast = true;

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _discoveryUdp.ReceiveAsync(ct);
                        var msg = System.Text.Encoding.UTF8.GetString(result.Buffer);
                        if (msg.Trim() == "MSN_DISCOVER")
                        {
                            var response = System.Text.Json.JsonSerializer.Serialize(new ServerAnnounceData
                            {
                                ServerName = _serverName,
                                Port = _port,
                                UserCount = _clients.Count
                            });
                            var bytes = System.Text.Encoding.UTF8.GetBytes(response);
                            await _discoveryUdp.SendAsync(bytes, bytes.Length,
                                result.RemoteEndPoint.Address.ToString(), _discoveryPort);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }
            catch { }
        }

        private async Task PingLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(30000, ct);
                var ping = Packet.Create(PacketType.Ping, new { });
                var dead = new List<ConnectedClient>();

                foreach (var c in _clients.Values)
                {
                    try { await c.SendAsync(ping); }
                    catch { dead.Add(c); }
                }

                foreach (var c in dead) await DisconnectClientAsync(c);
            }
        }

        private static void Log(string msg) =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }
}
