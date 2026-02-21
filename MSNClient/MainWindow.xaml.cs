using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MSNShared;

namespace MSNClient
{
    public partial class MainWindow : Window
    {
        private readonly ClientState _state = App.State;
        private string _searchFilter = "";

        public MainWindow()
        {
            InitializeComponent();


            // Wire up network events
            _state.Net.PacketReceived += OnPacket;
            _state.Net.Disconnected += OnDisconnected;

            RefreshMyProfile();
            RebuildContactList();

            // Fetch own profile picture if one exists on the server
            if (!string.IsNullOrEmpty(_state.MyProfilePicFileId))
            {
                _ = Task.Run(async () =>
                {
                    var img = await App.FileTransfer.GetProfilePictureAsync(_state.MyUsername);
                    if (img != null)
                    {
                        _state.MyProfilePicture = img;
                        Dispatcher.Invoke(RefreshMyProfile);
                    }
                });
            }

            // Handle incoming contact requests via toast-like popup
        }

        // ── Network ────────────────────────────────────────────────────────────
        private void OnPacket(Packet pkt)
        {
            Dispatcher.Invoke(() => HandlePacket(pkt));
        }

        private void HandlePacket(Packet pkt)
        {
            switch (pkt.Type)
            {
                case PacketType.UserList:
                    var ul = pkt.GetData<UserListData>();
                    if (ul is null) break;
                    _state.Contacts.Clear();
                    _state.Groups.Clear();

                    foreach (var u in ul.Users.Where(u => u.Username != _state.MyUsername))
                        _state.Contacts.Add(ContactVm.From(u));

                    foreach (var g in ul.Groups)
                        _state.Groups.Add(GroupVm.From(g));

                    // Rebuild contact set
                    _state.MyContacts.Clear();
                    foreach (var u in ul.Users.FirstOrDefault(u => u.Username == _state.MyUsername)?.Contacts ?? new())
                        _state.MyContacts.Add(u);

                    RebuildContactList();
                    break;

                case PacketType.FileReceive:
                    var fr = pkt.GetData<FileReceiveData>();
                    if (fr is null) break;
                    HandleFileReceive(fr);
                    break;

                case PacketType.ProfilePictureAck:
                    // Handled by FileTransferManager directly
                    break;

                case PacketType.ProfilePicData:
                    // Handled by FileTransferManager directly
                    break;

                case PacketType.PresenceBroadcast:
                    var pd = pkt.GetData<PresenceData>();
                    if (pd is null) break;
                    _state.UpdatePresence(pd);
                    // Add unknown users to list if not already there
                    if (!_state.Contacts.Any(c => c.Username == pd.Username) && pd.Username != _state.MyUsername)
                    {
                        _state.Contacts.Add(new ContactVm
                        {
                            Username = pd.Username,
                            DisplayName = pd.DisplayName,
                            Status = pd.Status,
                            PersonalMessage = pd.PersonalMessage,
                            AvatarEmoji = pd.AvatarEmoji,
                            HasProfilePicture = pd.HasProfilePicture,
                            ProfilePicFileId = pd.ProfilePicFileId
                        });
                    }
                    // Fetch profile picture if new/changed
                    if (pd.HasProfilePicture)
                    {
                        var cv = _state.GetContact(pd.Username);
                        if (cv != null && cv.ProfilePicture == null)
                            _ = FetchAndApplyProfilePicAsync(pd.Username);
                    }
                    RebuildContactList();
                    if (_state.OpenChats.TryGetValue(pd.Username, out var chatWin))
                        chatWin.UpdateContactStatus(pd);
                    break;

                case PacketType.ChatMessage:
                    var msg = pkt.GetData<ChatMessageData>();
                    if (msg is null) break;
                    OpenOrFocusChat(msg.From, msg);
                    break;

                case PacketType.GroupMessage:
                    var gm = pkt.GetData<GroupMessageData>();
                    if (gm is null) break;
                    OpenOrFocusGroupChat(gm.GroupId, gm);
                    break;

                case PacketType.ChatTyping:
                    var td = pkt.GetData<TypingData>();
                    if (td is null) break;
                    if (td.IsGroup)
                    {
                        if (_state.OpenGroupChats.TryGetValue(td.To, out var gcw))
                            gcw.SetTyping(td.From, td.IsTyping);
                    }
                    else
                    {
                        if (_state.OpenChats.TryGetValue(td.From, out var cw))
                            cw.SetTyping(td.IsTyping);
                        var cv = _state.GetContact(td.From);
                        if (cv != null) cv.IsTyping = td.IsTyping;
                    }
                    break;

                case PacketType.Nudge:
                    var nd = pkt.GetData<NudgeData>();
                    if (nd is null) break;
                    if (nd.IsGroup)
                    {
                        if (_state.OpenGroupChats.TryGetValue(nd.GroupId, out var gnw))
                            gnw.ReceiveNudge(nd.From);
                    }
                    else
                    {
                        if (_state.OpenChats.TryGetValue(nd.From, out var nw))
                            nw.ReceiveNudge();
                        else
                            OpenOrFocusChat(nd.From, null, nudge: true);
                    }
                    break;

                case PacketType.StickerSend:
                    var sd = pkt.GetData<StickerData>();
                    if (sd is null) break;
                    if (sd.IsGroup)
                    {
                        if (_state.OpenGroupChats.TryGetValue(sd.GroupId, out var gsw))
                            gsw.ReceiveSticker(sd);
                    }
                    else
                    {
                        if (_state.OpenChats.TryGetValue(sd.From, out var sw))
                            sw.ReceiveSticker(sd);
                        else
                            OpenOrFocusChat(sd.From, null);
                    }
                    break;

                case PacketType.CreateGroupAck:
                    var gi = pkt.GetData<GroupInfo>();
                    if (gi is null) break;
                    if (!_state.Groups.Any(g => g.Id == gi.Id))
                        _state.Groups.Add(GroupVm.From(gi));
                    else
                    {
                        var existing = _state.Groups.First(g => g.Id == gi.Id);
                        existing.Members.Clear();
                        foreach (var m in gi.Members) existing.Members.Add(m);
                    }
                    RebuildContactList();
                    break;

                case PacketType.GroupInviteReceived:
                    var inv = pkt.GetData<GroupInviteData>();
                    if (inv is null) break;
                    var res = MessageBox.Show($"You've been invited to join group '{inv.GroupName}' by {inv.InvitedBy}.\n\nJoin?",
                        "Group Invitation", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.No)
                        _ = _state.Net.SendAsync(Packet.Create(PacketType.LeaveGroup,
                            new JoinLeaveGroupData { GroupId = inv.GroupId, Username = _state.MyUsername }));
                    break;

                case PacketType.GroupMemberUpdate:
                    var gmu = pkt.GetData<JoinLeaveGroupData>();
                    if (gmu is null) break;
                    var grp = _state.GetGroup(gmu.GroupId);
                    if (grp != null)
                    {
                        if (gmu.Joined && !grp.Members.Contains(gmu.Username)) grp.Members.Add(gmu.Username);
                        else if (!gmu.Joined) grp.Members.Remove(gmu.Username);
                    }
                    if (_state.OpenGroupChats.TryGetValue(gmu.GroupId, out var gchat))
                        gchat.UpdateMembers();
                    break;

                case PacketType.ContactRequest:
                    var cr = pkt.GetData<ContactRequestData>();
                    if (cr is null) break;
                    var res2 = MessageBox.Show($"{cr.FromDisplayName} ({cr.From}) wants to add you as a contact.\n\nAllow?",
                        "Contact Request", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res2 == MessageBoxResult.Yes)
                    {
                        _state.MyContacts.Add(cr.From);
                        _ = _state.Net.SendAsync(Packet.Create(PacketType.AddContact,
                            new ContactRequestData { From = cr.From, Accept = true }));
                    }
                    break;

                case PacketType.TicTacToe:
                    var ttt = pkt.GetData<TttPacket>();
                    if (ttt != null) HandleTttPacket(ttt);
                    break;

                case PacketType.RockPaperScissors:
                    var rps = pkt.GetData<RpsPacket>();
                    if (rps != null) HandleRpsPacket(rps);
                    break;

                case PacketType.TttGameList:
                    // Handled by pending spectate dialog
                    break;

                case PacketType.Gartic:
                case PacketType.GarticLobbies:
                    // Handled by GarticLobbyWindow / GarticWindow via PacketReceived event
                    break;

                case PacketType.Blackjack:
                case PacketType.BlackjackLobbies:
                    // Handled by BlackjackLobbyWindow / BlackjackWindow via PacketReceived event
                    break;

                case PacketType.Error:
                    var err = pkt.GetData<ErrorData>();
                    if (err?.Code == "KICKED")
                    {
                        MessageBox.Show(err.Message, "MSN Messenger", MessageBoxButton.OK, MessageBoxImage.Warning);
                        OnDisconnected();
                    }
                    break;
            }
        }

        private void OnDisconnected()
        {
            Dispatcher.Invoke(() =>
            {
                ConnIndicator.Text = "🔴";
                ConnIndicator.ToolTip = "Disconnected";
                MessageBox.Show("You have been disconnected from the server.", "Disconnected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        // ── Tic-Tac-Toe ────────────────────────────────────────────────────────
        private void HandleTttPacket(TttPacket pkt)
        {
            switch (pkt.Msg)
            {
                case TttMsgType.Invite:
                    HandleTttInvite(pkt);
                    break;

                case TttMsgType.InviteAccept:
                    // Game started — open window for us (we are one of the players)
                    if (!_state.OpenTttGames.ContainsKey(pkt.GameId))
                    {
                        var win = new TicTacToeWindow(pkt) { Owner = this };
                        _state.OpenTttGames[pkt.GameId] = win;
                        win.Closed += (_, _) => _state.OpenTttGames.Remove(pkt.GameId);
                        win.Show();
                    }
                    break;

                case TttMsgType.InviteDecline:
                    MessageBox.Show($"{pkt.From} declined your Tic-Tac-Toe invitation.",
                        "Invitation Declined", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case TttMsgType.SpectateJoin:
                    // Server confirmed we joined as spectator — open spectator window
                    if (!_state.OpenTttGames.ContainsKey(pkt.GameId))
                    {
                        var info = new TttGameInfo
                        {
                            GameId = pkt.GameId,
                            PlayerX = pkt.From,
                            PlayerO = pkt.To,
                            PlayerXDisplay = _state.GetContact(pkt.From)?.DisplayName ?? pkt.From,
                            PlayerODisplay = _state.GetContact(pkt.To)?.DisplayName ?? pkt.To,
                            Board = pkt.Board ?? new int[9],
                            IsXTurn = pkt.IsXTurn,
                            Spectators = pkt.Spectators
                        };
                        var win = new TicTacToeWindow(info, spectator: true) { Owner = this };
                        _state.OpenTttGames[pkt.GameId] = win;
                        win.Show();
                    }
                    break;

                case TttMsgType.Move:
                case TttMsgType.GameOver:
                    // These are forwarded to the game window itself via its own packet handler
                    break;
            }
        }

        private void HandleTttInvite(TttPacket pkt)
        {
            var fromDisplay = _state.GetContact(pkt.From)?.DisplayName ?? pkt.From;
            var result = MessageBox.Show(
                $"🎮 {fromDisplay} challenges you to Tic-Tac-Toe!\n\nYou will play as ○  (second).\nAccept?",
                "Tic-Tac-Toe Invitation", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _ = _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe,
                    new TttPacket { Msg = TttMsgType.InviteAccept, GameId = pkt.GameId, From = _state.MyUsername, To = pkt.From }));
            }
            else
            {
                _ = _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe,
                    new TttPacket { Msg = TttMsgType.InviteDecline, GameId = pkt.GameId, From = _state.MyUsername, To = pkt.From }));
            }
        }

        private async void ChallengeTicTacToe(string opponentUsername)
        {
            if (_state.GetContact(opponentUsername)?.IsInGame == true)
            {
                MessageBox.Show($"{_state.GetContact(opponentUsername)?.DisplayName ?? opponentUsername} is already in a game.",
                    "Cannot Challenge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe,
                new TttPacket { Msg = TttMsgType.Invite, From = _state.MyUsername, To = opponentUsername }));
        }

        private async void SpectateGame(string gameId, string playerXName, string playerOName)
        {
            if (_state.OpenTttGames.ContainsKey(gameId)) { _state.OpenTttGames[gameId].Focus(); return; }
            await _state.Net.SendAsync(Packet.Create(PacketType.TicTacToe,
                new TttPacket { Msg = TttMsgType.SpectateRequest, GameId = gameId, From = _state.MyUsername }));
        }

        // ── Rock Paper Scissors ────────────────────────────────────────────────
        private void HandleRpsPacket(RpsPacket pkt)
        {
            switch (pkt.Msg)
            {
                case RpsMsgType.Invite:
                    var fromDisplay = _state.GetContact(pkt.From)?.DisplayName ?? pkt.From;
                    var res = MessageBox.Show(
                        $"✂️ {fromDisplay} challenges you to Rock Paper Scissors!\n\nAccept?",
                        "RPS Invitation", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (res == MessageBoxResult.Yes)
                    {
                        _ = _state.Net.SendAsync(Packet.Create(PacketType.RockPaperScissors,
                            new RpsPacket { Msg = RpsMsgType.InviteAccept, GameId = pkt.GameId, From = _state.MyUsername, To = pkt.From }));
                    }
                    else
                    {
                        _ = _state.Net.SendAsync(Packet.Create(PacketType.RockPaperScissors,
                            new RpsPacket { Msg = RpsMsgType.InviteDecline, GameId = pkt.GameId, From = _state.MyUsername, To = pkt.From }));
                    }
                    break;

                case RpsMsgType.InviteAccept:
                    // Open game window
                    // Store it somewhere? For now just create and show.
                    // Ideally we track open games to prevent duplicates but Window logic handles itself mostly.
                    var win = new RockPaperScissorsWindow(pkt.GameId, pkt.From == _state.MyUsername ? pkt.To : pkt.From) { Owner = this };
                    win.Show();
                    break;

                case RpsMsgType.InviteDecline:
                    MessageBox.Show($"{pkt.From} declined your RPS invitation.", "Invitation Declined", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }

        private async void ChallengeRps(string opponent)
        {
            await _state.Net.SendAsync(Packet.Create(PacketType.RockPaperScissors,
                new RpsPacket { Msg = RpsMsgType.Invite, From = _state.MyUsername, To = opponent }));
        }

        // ── UI Builder ─────────────────────────────────────────────────────────
        private void RebuildContactList()
        {
            ContactsPanel.Children.Clear();

            var filter = _searchFilter.ToLower();

            // ── Contacts section ──
            var myContactsList = _state.Contacts
                .Where(c => _state.MyContacts.Contains(c.Username))
                .Where(c => string.IsNullOrEmpty(filter) ||
                    c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    c.Username.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Status == UserStatus.Offline ? 1 : 0)
                .ThenBy(c => c.DisplayName)
                .ToList();

            var otherOnline = _state.Contacts
                .Where(c => !_state.MyContacts.Contains(c.Username) && c.Status != UserStatus.Offline)
                .Where(c => string.IsNullOrEmpty(filter) ||
                    c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    c.Username.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (myContactsList.Any())
            {
                ContactsPanel.Children.Add(MakeGroupHeader("👥 Contacts", myContactsList.Count(c => c.Status != UserStatus.Offline), myContactsList.Count, out var contactBody));
                foreach (var c in myContactsList)
                    contactBody.Children.Add(MakeContactItem(c));
                ContactsPanel.Children.Add(contactBody);
            }

            if (otherOnline.Any() && string.IsNullOrEmpty(filter))
            {
                ContactsPanel.Children.Add(MakeGroupHeader("🌐 Others Online", otherOnline.Count, otherOnline.Count, out var otherBody));
                foreach (var c in otherOnline)
                    otherBody.Children.Add(MakeContactItem(c));
                ContactsPanel.Children.Add(otherBody);
            }

            // ── Groups section ──
            var myGroups = _state.Groups
                .Where(g => g.Members.Contains(_state.MyUsername))
                .Where(g => string.IsNullOrEmpty(filter) || g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (myGroups.Any())
            {
                ContactsPanel.Children.Add(MakeGroupHeader("💬 Group Chats", myGroups.Count, myGroups.Count, out var groupBody));
                foreach (var g in myGroups)
                    groupBody.Children.Add(MakeGroupItem(g));
                ContactsPanel.Children.Add(groupBody);
            }

            // Add Contact button
            var addBtn = new Button
            {
                Style = (Style)FindResource("MenuBtn"),
                Content = "➕ Add a Contact",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 6, 0, 4),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153))
            };
            addBtn.Click += (s, e) => ShowAddContact();
            ContactsPanel.Children.Add(addBtn);

            var newGroupBtn = new Button
            {
                Style = (Style)FindResource("MenuBtn"),
                Content = "💬 Create Group Chat",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 0, 0, 4),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153))
            };
            newGroupBtn.Click += (s, e) => ShowCreateGroup();
            ContactsPanel.Children.Add(newGroupBtn);
        }

        private Border MakeGroupHeader(string title, int online, int total, out StackPanel body)
        {
            bool isExpanded = true;
            var bodyPanel = new StackPanel();
            body = bodyPanel;

            var header = new Border
            {
                Background = (Brush)FindResource("GroupHeaderBrush"),
                BorderBrush = new SolidColorBrush(Color.FromRgb(140, 180, 224)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 3, 4, 3),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var arrow = new TextBlock { Text = "▼", FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            var name = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153)), VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock { Text = $"({online}/{total})", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 130)), VerticalAlignment = VerticalAlignment.Center };

            Grid.SetColumn(arrow, 0); Grid.SetColumn(name, 1); Grid.SetColumn(count, 2);
            grid.Children.Add(arrow); grid.Children.Add(name); grid.Children.Add(count);
            header.Child = grid;

            header.MouseLeftButtonUp += (s, e) =>
            {
                isExpanded = !isExpanded;
                arrow.Text = isExpanded ? "▼" : "▶";
                bodyPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            };

            return header;
        }

        private Border MakeContactItem(ContactVm contact)
        {
            var item = new Border { Padding = new Thickness(8, 3, 4, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            item.MouseEnter += (s, e) => item.Background = new SolidColorBrush(Color.FromArgb(120, 168, 200, 240));
            item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Avatar + status dot
            var avatarGrid = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

            var avatarBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(140, 180, 224)),
                Background = contact.Status == UserStatus.Offline
                    ? new SolidColorBrush(Color.FromRgb(220, 220, 220))
                    : new SolidColorBrush(Color.FromRgb(200, 230, 255)),
                ClipToBounds = true
            };

            // Show profile picture if available, otherwise emoji
            if (contact.ProfilePicture != null)
            {
                var img = new Image
                {
                    Source = contact.ProfilePicture,
                    Stretch = System.Windows.Media.Stretch.UniformToFill
                };
                avatarBorder.Child = img;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            }
            else if (contact.HasProfilePicture)
            {
                // Not loaded yet — show emoji placeholder and kick off async load
                var emojiText = new TextBlock { Text = contact.AvatarEmoji, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                avatarBorder.Child = emojiText;
                // Fetch in background
                _ = Task.Run(async () =>
                {
                    var img = await App.FileTransfer.GetProfilePictureAsync(contact.Username);
                    if (img != null)
                    {
                        contact.ProfilePicture = img;
                        Dispatcher.Invoke(() =>
                        {
                            avatarBorder.Child = FileTransferManager.MakeCrispImage(img);
                        });
                    }
                });
            }
            else
            {
                avatarBorder.Child = new TextBlock { Text = contact.AvatarEmoji, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }

            // Update avatar when profile picture loads
            contact.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ContactVm.ProfilePicture) && contact.ProfilePicture != null)
                    Dispatcher.Invoke(() => avatarBorder.Child = new Image { Source = contact.ProfilePicture, Stretch = System.Windows.Media.Stretch.UniformToFill });
                if (e.PropertyName == nameof(ContactVm.AvatarEmoji) && contact.ProfilePicture == null)
                    Dispatcher.Invoke(() => { if (avatarBorder.Child is TextBlock tb) tb.Text = contact.AvatarEmoji; });
            };

            var statusDot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(contact.StatusColor)) };
            avatarGrid.Children.Add(avatarBorder);
            avatarGrid.Children.Add(statusDot);

            contact.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ContactVm.StatusColor))
                    Dispatcher.Invoke(() => statusDot.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(contact.StatusColor)));
            };

            // Text
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var nameText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = contact.Status == UserStatus.Offline ? new SolidColorBrush(Color.FromRgb(130, 130, 130)) : new SolidColorBrush(Color.FromRgb(0, 30, 80)) };
            nameText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ContactVm.DisplayName)) { Source = contact });
            nameRow.Children.Add(nameText);
            textStack.Children.Add(nameRow);

            if (!string.IsNullOrEmpty(contact.PersonalMessage))
            {
                var statusText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 140)), FontStyle = FontStyles.Italic, TextTrimming = TextTrimming.CharacterEllipsis };
                statusText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ContactVm.PersonalMessage)) { Source = contact });
                textStack.Children.Add(statusText);
            }

            Grid.SetColumn(avatarGrid, 0); Grid.SetColumn(textStack, 1);
            grid.Children.Add(avatarGrid); grid.Children.Add(textStack);
            item.Child = grid;

            // Context menu
            var ctx = new ContextMenu();
            var msgItem = new MenuItem { Header = "💬 Send Message" };
            msgItem.Click += (s, e) => OpenOrFocusChat(contact.Username);
            ctx.Items.Add(msgItem);
            var nudgeItem = new MenuItem { Header = "👊 Send Nudge" };
            nudgeItem.Click += async (s, e) => await _state.Net.SendAsync(Packet.Create(PacketType.Nudge, new NudgeData { From = _state.MyUsername, To = contact.Username }));
            ctx.Items.Add(nudgeItem);
            ctx.Items.Add(new Separator());

            // Tic-Tac-Toe menu items — dynamic based on whether they're in a game
            var tttCapture = contact; // capture for lambda
            if (contact.IsInGame && !string.IsNullOrEmpty(contact.GameId))
            {
                var spectateItem = new MenuItem { Header = "🎮 Spectate Tic-Tac-Toe" };
                spectateItem.Click += (s, e) => SpectateGame(tttCapture.GameId, tttCapture.DisplayName, "");
                ctx.Items.Add(spectateItem);
            }
            else if (contact.Status != UserStatus.Offline)
            {
                var challengeItem = new MenuItem { Header = "🎮 Challenge to Tic-Tac-Toe" };
                challengeItem.Click += (s, e) => ChallengeTicTacToe(tttCapture.Username);
                ctx.Items.Add(challengeItem);

                var rpsItem = new MenuItem { Header = "✂️ Challenge to Rock Paper Scissors" };
                rpsItem.Click += (s, e) => ChallengeRps(tttCapture.Username);
                ctx.Items.Add(rpsItem);
            }
            ctx.Items.Add(new Separator());

            var removeItem = new MenuItem { Header = "❌ Remove Contact" };
            removeItem.Click += async (s, e) =>
            {
                _state.MyContacts.Remove(contact.Username);
                await _state.Net.SendAsync(Packet.Create(PacketType.RemoveContact, new ContactRequestData { From = contact.Username }));
                RebuildContactList();
            };
            ctx.Items.Add(removeItem);
            item.ContextMenu = ctx;

            item.MouseLeftButtonUp += (s, e) => OpenOrFocusChat(contact.Username);
            return item;
        }

        private Border MakeGroupItem(GroupVm group)
        {
            var item = new Border { Padding = new Thickness(8, 3, 4, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            item.MouseEnter += (s, e) => item.Background = new SolidColorBrush(Color.FromArgb(120, 168, 200, 240));
            item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBorder = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(140, 180, 224)), Background = new SolidColorBrush(Color.FromRgb(180, 220, 255)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            iconBorder.Child = new TextBlock { Text = "👥", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = group.Name, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0, 30, 80)) });
            textStack.Children.Add(new TextBlock { Text = $"{group.Members.Count} members", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 140)), FontStyle = FontStyles.Italic });

            Grid.SetColumn(iconBorder, 0); Grid.SetColumn(textStack, 1);
            grid.Children.Add(iconBorder); grid.Children.Add(textStack);
            item.Child = grid;

            // Context menu
            var ctx = new ContextMenu();
            var openItem = new MenuItem { Header = "💬 Open Chat" };
            openItem.Click += (s, e) => OpenOrFocusGroupChat(group.Id);
            ctx.Items.Add(openItem);
            var inviteItem = new MenuItem { Header = "➕ Invite Someone" };
            inviteItem.Click += (s, e) => ShowInviteToGroup(group);
            ctx.Items.Add(inviteItem);
            ctx.Items.Add(new Separator());
            var leaveItem = new MenuItem { Header = "🚪 Leave Group" };
            leaveItem.Click += async (s, e) =>
            {
                await _state.Net.SendAsync(Packet.Create(PacketType.LeaveGroup,
                    new JoinLeaveGroupData { GroupId = group.Id, Username = _state.MyUsername }));
                _state.Groups.Remove(group);
                RebuildContactList();
            };
            ctx.Items.Add(leaveItem);
            item.ContextMenu = ctx;

            item.MouseLeftButtonUp += (s, e) => OpenOrFocusGroupChat(group.Id);
            return item;
        }

        // ── File Receive ────────────────────────────────────────────────────────
        private void HandleFileReceive(FileReceiveData fr)
        {
            // Route to the correct chat window — open one if needed
            if (fr.IsGroup)
            {
                OpenOrFocusGroupChat(fr.GroupId);
                if (_state.OpenGroupChats.TryGetValue(fr.GroupId, out var gcw))
                    gcw.ReceiveFile(fr);
            }
            else
            {
                OpenOrFocusChat(fr.From);
                if (_state.OpenChats.TryGetValue(fr.From, out var cw))
                    cw.ReceiveFile(fr);
            }
        }

        private async Task FetchAndApplyProfilePicAsync(string username)
        {
            var img = await App.FileTransfer.GetProfilePictureAsync(username);
            var contact = _state.GetContact(username);
            if (contact != null && img != null)
            {
                contact.ProfilePicture = img;
                Dispatcher.Invoke(RebuildContactList);
            }
        }

        // ── Chat helpers ───────────────────────────────────────────────────────
        private void OpenOrFocusChat(string username, ChatMessageData? initialMsg = null, bool nudge = false)
        {
            if (_state.OpenChats.TryGetValue(username, out var existing))
            {
                existing.Activate();
                if (initialMsg != null) existing.ReceiveMessage(initialMsg);
                if (nudge) existing.ReceiveNudge();
            }
            else
            {
                var contact = _state.GetContact(username) ?? new ContactVm { Username = username, DisplayName = username };
                var chatWin = new ChatWindow(contact);
                _state.OpenChats[username] = chatWin;
                chatWin.Closed += (s, e) => _state.OpenChats.Remove(username);
                chatWin.Show();
                if (initialMsg != null) chatWin.ReceiveMessage(initialMsg);
                if (nudge) chatWin.ReceiveNudge();
            }
        }

        private void OpenOrFocusGroupChat(string groupId, GroupMessageData? initialMsg = null)
        {
            if (_state.OpenGroupChats.TryGetValue(groupId, out var existing))
            {
                existing.Activate();
                if (initialMsg != null) existing.ReceiveMessage(initialMsg);
            }
            else
            {
                var group = _state.GetGroup(groupId);
                if (group is null) return;
                var gcw = new GroupChatWindow(group);
                _state.OpenGroupChats[groupId] = gcw;
                gcw.Closed += (s, e) => _state.OpenGroupChats.Remove(groupId);
                gcw.Show();
                if (initialMsg != null) gcw.ReceiveMessage(initialMsg);
            }
        }

        // ── Dialogs ────────────────────────────────────────────────────────────
        private void ShowAddContact()
        {
            var dlg = new AddContactDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Username))
            {
                _state.MyContacts.Add(dlg.Username.ToLower());
                _ = _state.Net.SendAsync(Packet.Create(PacketType.AddContact,
                    new ContactRequestData { From = dlg.Username.ToLower() }));
            }
        }

        private void ShowCreateGroup()
        {
            var dlg = new CreateGroupDialog(_state.Contacts.ToList()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _ = _state.Net.SendAsync(Packet.Create(PacketType.CreateGroup, new CreateGroupData
                {
                    Name = dlg.GroupName,
                    Description = dlg.GroupDescription,
                    InitialMembers = dlg.SelectedMembers
                }));
            }
        }

        private void ShowInviteToGroup(GroupVm group)
        {
            var dlg = new AddContactDialog { Owner = this, Title = "Invite to Group" };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Username))
            {
                _ = _state.Net.SendAsync(Packet.Create(PacketType.InviteToGroup, new InviteToGroupData
                {
                    GroupId = group.Id,
                    Username = dlg.Username.ToLower()
                }));
            }
        }

        // ── My profile actions ──────────────────────────────────────────────────
        private void RefreshMyProfile()
        {
            MyNameText.Text = _state.MyDisplayName;
            MyStatusText.Text = _state.MyStatusText;
            MyStatusDot.Text = "●";
            MyStatusDot.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_state.MyStatusColor));
            MyPMText.Text = string.IsNullOrEmpty(_state.MyPersonalMessage) ? "Click to set a personal message..." : _state.MyPersonalMessage;
            MyPMText.FontStyle = string.IsNullOrEmpty(_state.MyPersonalMessage) ? FontStyles.Italic : FontStyles.Normal;

            // Show profile picture or emoji
            if (_state.MyProfilePicture != null)
            {
                MyAvatarImage.Source = _state.MyProfilePicture;
                MyAvatarImage.Visibility = Visibility.Visible;
                MyAvatarText.Visibility = Visibility.Collapsed;
            }
            else
            {
                MyAvatarText.Text = _state.MyAvatarEmoji;
                MyAvatarText.Visibility = Visibility.Visible;
                MyAvatarImage.Visibility = Visibility.Collapsed;
            }
        }

        private void Avatar_Click(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();

            var pfpItem = new MenuItem { Header = "🖼️  Set Profile Picture..." };
            pfpItem.Click += async (s2, e2) => await ShowProfilePictureDialogAsync();
            menu.Items.Add(pfpItem);

            if (_state.MyProfilePicture != null)
            {
                var removePfpItem = new MenuItem { Header = "❌ Remove Profile Picture" };
                removePfpItem.Click += (s2, e2) =>
                {
                    _state.MyProfilePicture = null;
                    _state.MyProfilePicFileId = "";
                    MyAvatarImage.Visibility = Visibility.Collapsed;
                    MyAvatarText.Visibility = Visibility.Visible;
                    MyAvatarText.Text = _state.MyAvatarEmoji;
                };
                menu.Items.Add(removePfpItem);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "── Emoji Avatars ──", IsEnabled = false });

            var emojis = new[] { "🙂", "😎", "🤓", "😜", "🐶", "🐱", "🦊", "🐸", "🦄", "🎮", "🎵", "⭐" };
            foreach (var em in emojis)
            {
                var em2 = em;
                var item = new MenuItem { Header = em, FontSize = 18 };
                item.Click += async (s2, e2) =>
                {
                    _state.MyAvatarEmoji = em2;
                    if (_state.MyProfilePicture == null) MyAvatarText.Text = em2;
                    await _state.Net.SendAsync(Packet.Create(PacketType.PresenceUpdate, new PresenceData
                    {
                        Username = _state.MyUsername,
                        Status = _state.MyStatus,
                        PersonalMessage = _state.MyPersonalMessage,
                        DisplayName = _state.MyDisplayName,
                        AvatarEmoji = em2
                    }));
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private async Task ShowProfilePictureDialogAsync()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose Profile Picture",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            var (success, message) = await App.FileTransfer.UploadProfilePictureAsync(dlg.FileName);
            if (success)
            {
                RefreshMyProfile();
                MessageBox.Show("Profile picture updated! 🎉", "MSN Messenger", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(message, "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MyName_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new SimpleInputDialog("Change Display Name", "Display name:", _state.MyDisplayName) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
            {
                _state.MyDisplayName = dlg.Value;
                MyNameText.Text = dlg.Value;
                _ = SendPresenceUpdate();
            }
        }

        private void MyStatus_Click(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            var statuses = new[] { (UserStatus.Online, "🟢 Online"), (UserStatus.Away, "🟡 Away"), (UserStatus.Busy, "🔴 Busy"), (UserStatus.AppearOffline, "👻 Appear Offline") };
            foreach (var (status, label) in statuses)
            {
                var s2 = status;
                var item = new MenuItem { Header = label };
                item.Click += async (s, e2) =>
                {
                    _state.MyStatus = s2;
                    RefreshMyProfile();
                    await SendPresenceUpdate();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private async void MyPM_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new SimpleInputDialog("Personal Message", "Personal message:", _state.MyPersonalMessage) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _state.MyPersonalMessage = dlg.Value ?? "";
                RefreshMyProfile();
                await SendPresenceUpdate();
            }
        }

        private async System.Threading.Tasks.Task SendPresenceUpdate() =>
            await _state.Net.SendAsync(Packet.Create(PacketType.PresenceUpdate, new PresenceData
            {
                Username = _state.MyUsername,
                Status = _state.MyStatus,
                PersonalMessage = _state.MyPersonalMessage,
                DisplayName = _state.MyDisplayName,
                AvatarEmoji = _state.MyAvatarEmoji
            }));

        // ── Toolbar / Menu handlers ────────────────────────────────────────────
        private void Toolbar_Invite(object sender, RoutedEventArgs e) => ShowAddContact();
        private void Toolbar_AddContact(object sender, RoutedEventArgs e) => ShowAddContact();
        private void Toolbar_NewGroup(object sender, RoutedEventArgs e) => ShowCreateGroup();
        private void Toolbar_Settings(object sender, RoutedEventArgs e)
        {
            MessageBox.Show($"Connected to: {_state.Net.ConnectedHost}:{_state.Net.ConnectedPort}\nUser: {_state.MyUsername}\nContacts: {_state.MyContacts.Count}\nGroups: {_state.Groups.Count}", "Connection Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Toolbar_Games(object sender, RoutedEventArgs e)
        {
            // Context menu for games
            var cm = new ContextMenu();
            var ttt = new MenuItem { Header = "Tic-Tac-Toe" };
            ttt.Click += (s, args) => OpenTttLobby();
            var gartic = new MenuItem { Header = "Gartic (Draw & Guess)" };
            gartic.Click += (s, args) => OpenGarticLobby();
            var phone = new MenuItem { Header = "Gartic Phone" };
            phone.Click += (s, args) => OpenGarticPhoneLobby();
            var paint = new MenuItem { Header = "🎨 Paint.IO" };
            paint.Click += (s, args) => new Windows.PaintIoWindow().Show();
            var blackjack = new MenuItem { Header = "🃏 Blackjack" };
            blackjack.Click += (s, args) => OpenBlackjackLobby();
            var uno = new MenuItem { Header = "🔴 Uno" };
            uno.Click += (s, args) => OpenUnoLobby();

            cm.Items.Add(ttt);
            cm.Items.Add(gartic);
            cm.Items.Add(phone);
            cm.Items.Add(paint);
            cm.Items.Add(blackjack);
            cm.Items.Add(uno);
            cm.IsOpen = true;
        }

        private void Toolbar_ServerBrowser(object sender, RoutedEventArgs e)
        {
            var browser = new ServerBrowserWindow { Owner = this };
            if (browser.ShowDialog() == true && browser.SelectedServer != null)
            {
                var s = browser.SelectedServer;
                MessageBox.Show(
                    $"Server: {s.ServerName}\nAddress: {s.Host}:{s.Port}\nOnline users: {s.UserCount}\n\nTo connect to this server, sign out and re-connect via the login screen's Server Browser.",
                    "Server Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OpenTttLobby()
        {
            // Simple approach: show pending games window or direct join if we implemented full lobby
            // For now, let's just create a new game or invite someone.
            // But TTT works by context menu on contact.
            // Let's launch a TTT Game List instead.
            _ = _state.Net.SendAsync(Packet.Create(PacketType.TttListGames, new { }));
            // And maybe a dialog to create game?
            // For MVP, just show game list (UserList update?) or just list active games.
            // We'll show a "Game Browser" window.
            // new Windows.TttGameBrowserWindow().Show();
            MessageBox.Show("Tic-Tac-Toe Lobby not implemented yet.", "Coming Soon");
        }

        private void OpenGarticLobby()
        {
            new GarticLobbyWindow { Owner = this }.Show();
        }

        private void OpenGarticPhoneLobby()
        {
            new GarticPhoneLobbyWindow { Owner = this }.Show();
        }

        private void OpenBlackjackLobby()
        {
            new BlackjackLobbyWindow { Owner = this }.Show();
        }

        private void OpenUnoLobby()
        {
            new UnoLobbyWindow { Owner = this }.Show();
        }

        private void Menu_File(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var si = new MenuItem { Header = "Sign Out" };
            si.Click += async (s, args) => { await _state.Net.SendAsync(Packet.Create(PacketType.Logout, new { })); _state.Net.Disconnect(); new LoginWindow().Show(); Close(); };
            menu.Items.Add(si);
            var ex = new MenuItem { Header = "Exit" };
            ex.Click += (s, args) => Application.Current.Shutdown();
            menu.Items.Add(ex);
            menu.IsOpen = true;
        }



        private void Menu_Contacts(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Add Contact...", Command = new RelayCommand(ShowAddContact) });
            menu.Items.Add(new MenuItem { Header = "Create Group Chat...", Command = new RelayCommand(ShowCreateGroup) });
            menu.IsOpen = true;
        }

        private void Menu_Actions(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Change Status", Command = new RelayCommand(() => MyStatus_Click(sender, null!)) });
            menu.IsOpen = true;
        }

        private void Menu_Tools(object sender, RoutedEventArgs e) => Toolbar_Settings(sender, e);

        private void ConnIndicator_Click(object sender, MouseButtonEventArgs e) => Toolbar_Settings(sender, e);

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            _searchFilter = SearchBox.Text;
            RebuildContactList();
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            await _state.Net.SendAsync(Packet.Create(PacketType.Logout, new { }));
            _state.Net.Disconnect();
            Application.Current.Shutdown();
        }
    }

    // Simple ICommand implementation
    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action a) { _action = a; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _action();
    }
}
