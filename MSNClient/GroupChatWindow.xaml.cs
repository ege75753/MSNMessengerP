using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MSNShared;

namespace MSNClient
{
    public partial class GroupChatWindow : Window
    {
        private readonly GroupVm _group;
        private readonly ClientState _state = App.State;
        private Color _myColor = Color.FromRgb(0, 0, 128);
        private string _myFont = "Tahoma";
        private int _myFontSize = 10;
        private bool _isBold, _isItalic, _isUnderline;
        private DispatcherTimer? _typingDebounce;
        private bool _isSendingTyping;
        private readonly Dictionary<string, DateTime> _typingUsers = new();
        private DispatcherTimer? _typingClearTimer;

        private static readonly string[] Emoticons =
            { "😊", "😂", "😍", "😎", "😢", "😡", "🤔", "👍", "👎", "❤️", "🎉", "🔥", "😜", "🙈", "💀" };

        public GroupChatWindow(GroupVm group)
        {
            InitializeComponent();
            _group = group;
            Title = $"[Group] {group.Name}";
            GroupNameText.Text = group.Name;
            GroupDescText.Text = string.IsNullOrEmpty(group.Description) ? $"Group Chat — {group.Members.Count} members" : group.Description;

            SetupFontCombos();
            UpdateMembers();
            AddSystemMessage($"Welcome to '{group.Name}' group chat! 👋");
            InputBox.Focus();

            _typingClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _typingClearTimer.Tick += (s, e) =>
            {
                _typingUsers.Clear();
                UpdateTypingText();
            };
        }

        public void UpdateMembers()
        {
            Dispatcher.Invoke(() =>
            {
                MembersList.Items.Clear();
                foreach (var m in _group.Members)
                {
                    var contact = _state.GetContact(m);
                    var isMe = m == _state.MyUsername;
                    var display = isMe ? $"👤 {_state.MyDisplayName} (you)" :
                        contact != null ? $"{contact.AvatarEmoji} {contact.DisplayName}" : $"👤 {m}";
                    MembersList.Items.Add(display);
                }
                GroupDescText.Text = string.IsNullOrEmpty(_group.Description) ? $"Group Chat — {_group.Members.Count} members" : _group.Description;
            });
        }

        public void ReceiveMessage(GroupMessageData msg)
        {
            Dispatcher.Invoke(() =>
            {
                _typingUsers.Remove(msg.From);
                UpdateTypingText();
                AddMessage(msg.From, msg.Content, false, msg.Color, msg.FontFamily, msg.FontSize, msg.Bold, msg.Italic, msg.Underline);
                if (!IsActive) { Title = $"[New] {_group.Name}"; FlashWindow(); }
            });
        }

        public void SetTyping(string username, bool typing)
        {
            Dispatcher.Invoke(() =>
            {
                if (typing) _typingUsers[username] = DateTime.Now;
                else _typingUsers.Remove(username);
                UpdateTypingText();
                if (typing) { _typingClearTimer?.Stop(); _typingClearTimer?.Start(); }
            });
        }

        private void UpdateTypingText()
        {
            if (_typingUsers.Count == 0) { TypingText.Text = ""; return; }
            var names = _typingUsers.Keys.Select(u => _state.GetContact(u)?.DisplayName ?? u).ToList();
            TypingText.Text = names.Count == 1 ? $"{names[0]} is typing..." : $"{string.Join(", ", names)} are typing...";
        }

        private void AddMessage(string sender, string text, bool isMe, string color = "#000080",
            string font = "Tahoma", int size = 10, bool bold = false, bool italic = false, bool underline = false)
        {
            var contact = _state.GetContact(sender);
            var displayName = isMe ? _state.MyDisplayName : (contact?.DisplayName ?? sender);
            var avatar = isMe ? _state.MyAvatarEmoji : (contact?.AvatarEmoji ?? "🙂");

            var container = new Grid { Margin = new Thickness(0, 2, 0, 3) };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatarBorder = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = isMe ? new SolidColorBrush(Color.FromRgb(200, 230, 255)) : new SolidColorBrush(Color.FromRgb(220, 240, 200)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            avatarBorder.Child = new TextBlock { Text = avatar, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var textStack = new StackPanel();
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            headerRow.Children.Add(new TextBlock { Text = displayName + " ", FontWeight = FontWeights.Bold, FontSize = 10, Foreground = isMe ? new SolidColorBrush(Color.FromRgb(0, 0, 128)) : new SolidColorBrush(Color.FromRgb(128, 0, 0)) });
            headerRow.Children.Add(new TextBlock { Text = $"({DateTime.Now:h:mm tt})", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)) });
            textStack.Children.Add(headerRow);
            textStack.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(font),
                FontSize = size,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 0),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = underline ? TextDecorations.Underline : null
            });

            Grid.SetColumn(avatarBorder, 0); Grid.SetColumn(textStack, 1);
            container.Children.Add(avatarBorder); container.Children.Add(textStack);
            MessagesPanel.Children.Add(container);
            ChatScroll.UpdateLayout(); ChatScroll.ScrollToEnd();
        }

        private void AddSystemMessage(string text)
        {
            MessagesPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            });
            ChatScroll.ScrollToEnd();
        }

        private string GetInputText() =>
            new TextRange(InputBox.Document.ContentStart, InputBox.Document.ContentEnd).Text.Trim();
        private void ClearInput() => InputBox.Document.Blocks.Clear();

        private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessage();

        private async System.Threading.Tasks.Task SendMessage()
        {
            var text = GetInputText();
            if (string.IsNullOrWhiteSpace(text)) return;
            var colorHex = $"#{_myColor.R:X2}{_myColor.G:X2}{_myColor.B:X2}";
            AddMessage(_state.MyUsername, text, true, colorHex, _myFont, _myFontSize, _isBold, _isItalic, _isUnderline);
            ClearInput();
            await _state.Net.SendAsync(Packet.Create(PacketType.GroupMessage, new GroupMessageData
            {
                From = _state.MyUsername,
                GroupId = _group.Id,
                Content = text,
                Color = colorHex,
                FontFamily = _myFont,
                FontSize = _myFontSize,
                Bold = _isBold,
                Italic = _isItalic,
                Underline = _isUnderline
            }));
            Title = $"[Group] {_group.Name}";
        }

        private async void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            { e.Handled = true; await SendMessage(); }
        }

        private async void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSendingTyping) return;
            _isSendingTyping = true;
            await _state.Net.SendAsync(Packet.Create(PacketType.ChatTyping, new TypingData
            { From = _state.MyUsername, To = _group.Id, IsGroup = true, IsTyping = true }));
            _typingDebounce?.Stop();
            _typingDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _typingDebounce.Tick += async (s, _) =>
            {
                _typingDebounce.Stop(); _isSendingTyping = false;
                await _state.Net.SendAsync(Packet.Create(PacketType.ChatTyping, new TypingData
                { From = _state.MyUsername, To = _group.Id, IsGroup = true, IsTyping = false }));
            };
            _typingDebounce.Start();
        }

        public void ReceiveFile(FileReceiveData fr)
        {
            Dispatcher.Invoke(() =>
            {
                AddFileBubble(fr.FromDisplayName.Length > 0 ? fr.FromDisplayName : fr.From,
                    fr.FileName, fr.FileSize, fr.MimeType, fr.FileId,
                    isMe: false, inlineBase64: fr.InlineDataBase64);
                if (!IsActive) { Title = $"[File] {_group.Name}"; FlashWindow(); }
            });
        }

        private void AddFileBubble(string sender, string fileName, long fileSize, string mimeType,
            string fileId, bool isMe, string? inlineBase64, string? filePath = null)
        {
            var isImage = MimeTypes.IsImage(mimeType);
            var container = new Border
            {
                Background = isMe ? new SolidColorBrush(Color.FromRgb(220, 235, 255)) : new SolidColorBrush(Color.FromRgb(235, 255, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(140, 180, 224)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(isMe ? 60 : 0, 3, isMe ? 0 : 60, 3),
                HorizontalAlignment = isMe ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 380
            };
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(new TextBlock { Text = sender, FontWeight = FontWeights.Bold, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153)) });
            header.Children.Add(new TextBlock { Text = $" sent a file  ({DateTime.Now:h:mm tt})", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)) });
            stack.Children.Add(header);

            if (isImage)
            {
                if (!string.IsNullOrEmpty(inlineBase64) || (filePath != null && System.IO.File.Exists(filePath)))
                {
                    var img = !string.IsNullOrEmpty(inlineBase64)
                        ? FileTransferManager.Base64ToImage(inlineBase64)
                        : FileTransferManager.FileToImage(filePath!);
                    if (img != null)
                    {
                        var image = new Image { Source = img, MaxWidth = 320, MaxHeight = 240, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand };
                        image.MouseLeftButtonUp += (s, e) =>
                        {
                            var w = new Window { Title = fileName, Background = System.Windows.Media.Brushes.Black, Width = 800, Height = 600, Owner = this };
                            w.Content = new Image { Source = img, Stretch = System.Windows.Media.Stretch.Uniform };
                            w.Show();
                        };
                        stack.Children.Add(image);
                    }
                }
                else
                {
                    var loadBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = $"🖼️  Load image: {fileName} ({MimeTypes.FriendlySize(fileSize)})", FontSize = 10 };
                    loadBtn.Click += async (s, e) =>
                    {
                        loadBtn.IsEnabled = false; loadBtn.Content = "Loading...";
                        var fd = await App.FileTransfer.DownloadFileAsync(fileId);
                        if (fd?.Found == true)
                        {
                            var img = FileTransferManager.Base64ToImage(fd.DataBase64);
                            if (img != null)
                            {
                                var image = new Image { Source = img, MaxWidth = 320, MaxHeight = 240, Stretch = System.Windows.Media.Stretch.Uniform, Cursor = Cursors.Hand };
                                var idx = stack.Children.IndexOf(loadBtn);
                                stack.Children.Remove(loadBtn);
                                stack.Children.Insert(idx, image);
                            }
                        }
                        else { loadBtn.Content = "❌ Failed"; }
                    };
                    stack.Children.Add(loadBtn);
                }
            }
            else
            {
                var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
                fileRow.Children.Add(new TextBlock { Text = "📁  ", FontSize = 16 });
                var fi = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                fi.Children.Add(new TextBlock { Text = fileName, FontSize = 11, FontWeight = FontWeights.SemiBold, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis });
                fi.Children.Add(new TextBlock { Text = MimeTypes.FriendlySize(fileSize), FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)) });
                fileRow.Children.Add(fi);
                stack.Children.Add(fileRow);

                var dlBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = "⬇ Save File", FontSize = 10 };
                dlBtn.Click += async (s, e) =>
                {
                    dlBtn.IsEnabled = false; dlBtn.Content = "Downloading...";
                    var fd = filePath != null ? new FileDataResponse { Found = true, FileName = fileName, DataBase64 = Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(filePath)) } : await App.FileTransfer.DownloadFileAsync(fileId);
                    if (fd?.Found == true)
                    {
                        var saveDlg = new Microsoft.Win32.SaveFileDialog { FileName = fd.FileName };
                        if (saveDlg.ShowDialog() == true)
                        {
                            await App.FileTransfer.SaveFileAsync(fd, saveDlg.FileName);
                            dlBtn.Content = "✅ Saved!";
                        }
                        else { dlBtn.IsEnabled = true; dlBtn.Content = "⬇ Save File"; }
                    }
                    else { dlBtn.Content = "❌ Failed"; }
                };
                stack.Children.Add(dlBtn);
            }

            container.Child = stack;
            MessagesPanel.Children.Add(container);
            ChatScroll.UpdateLayout(); ChatScroll.ScrollToEnd();
        }

        private async void SendFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send File to Group", Filter = "All Files|*.*|Images|*.png;*.jpg;*.jpeg;*.gif" };
            if (dlg.ShowDialog() != true) return;
            var info = new System.IO.FileInfo(dlg.FileName);
            AddSystemMessage($"📤 Sending '{System.IO.Path.GetFileName(dlg.FileName)}'...");
            var (success, message, fileId) = await App.FileTransfer.SendFileAsync(dlg.FileName, _group.Id, true);
            if (success)
                AddFileBubble(_state.MyDisplayName, System.IO.Path.GetFileName(dlg.FileName), info.Length, MimeTypes.FromFileName(dlg.FileName), fileId, isMe: true, inlineBase64: null, filePath: dlg.FileName);
            else
                AddSystemMessage($"❌ Failed: {message}");
        }

        private async Task InviteToGroup()
        {
            var dlg = new AddContactDialog { Owner = this, Title = "Invite to Group" };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Username))
            {
                await _state.Net.SendAsync(Packet.Create(PacketType.InviteToGroup, new InviteToGroupData
                { GroupId = _group.Id, Username = dlg.Username.ToLower() }));
            }
        }

        private void InviteToGroup_Click(object sender, RoutedEventArgs e) => _ = InviteToGroup();


        private void SetupFontCombos()
        {
            foreach (var f in new[] { "Tahoma", "Arial", "Comic Sans MS", "Courier New", "Times New Roman" })
                FontCombo.Items.Add(f);
            FontCombo.SelectedIndex = 0;
            for (int i = 8; i <= 20; i += 2) SizeCombo.Items.Add(i);
            SizeCombo.SelectedIndex = 1;
        }

        private void Font_Changed(object sender, SelectionChangedEventArgs e) { if (FontCombo.SelectedItem is string f) _myFont = f; }
        private void Size_Changed(object sender, SelectionChangedEventArgs e) { if (SizeCombo.SelectedItem is int s) _myFontSize = s; }
        private void Bold_Click(object sender, RoutedEventArgs e) { _isBold = !_isBold; ((Button)sender).FontWeight = _isBold ? FontWeights.Bold : FontWeights.Normal; }
        private void Italic_Click(object sender, RoutedEventArgs e) { _isItalic = !_isItalic; ((Button)sender).FontStyle = _isItalic ? FontStyles.Italic : FontStyles.Normal; }
        private void Underline_Click(object sender, RoutedEventArgs e) { _isUnderline = !_isUnderline; }


        private void Color_Click(object sender, RoutedEventArgs e)
        {
            var colors = new[] { ("#000080", "Navy"), ("#800000", "Dark Red"), ("#008000", "Green"), ("#800080", "Purple"), ("#FF6600", "Orange"), ("#000000", "Black") };
            var menu = new ContextMenu();
            foreach (var (hex, name) in colors)
            {
                var h = hex;
                var item = new MenuItem { Header = name, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)) };
                item.Click += (s, e2) => _myColor = (Color)ColorConverter.ConvertFromString(h);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private void Emoticon_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var wrap = new WrapPanel { Width = 200 };
            foreach (var em in Emoticons)
            {
                var em2 = em;
                var btn = new Button { Content = em2, FontSize = 18, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = 34, Height = 34, Cursor = Cursors.Hand };
                btn.Click += (s2, e2) => { menu.IsOpen = false; InputBox.Selection.Text = em2; InputBox.Focus(); };
                wrap.Children.Add(btn);
            }
            var mi = new MenuItem(); mi.Header = wrap; menu.Items.Add(mi);
            menu.IsOpen = true;
        }

        private void Sticker_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var stickers = StickerManager.GetAllStickers();

            if (stickers.Count > 0)
            {
                var wrap = new WrapPanel { Width = 280 };
                foreach (var (name, path, base64) in stickers)
                {
                    var stickerImg = StickerManager.LoadStickerImage(path);
                    if (stickerImg == null) continue;
                    var imgBtn = new Button
                    {
                        Width = 64,
                        Height = 64,
                        Margin = new Thickness(2),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                        Cursor = Cursors.Hand,
                        ToolTip = name,
                        Content = new Image { Source = stickerImg, Stretch = System.Windows.Media.Stretch.Uniform }
                    };
                    var b64 = base64; var n = name;
                    imgBtn.Click += async (s2, e2) =>
                    {
                        menu.IsOpen = false;
                        AddStickerBubble(_state.MyDisplayName, n, b64, true);
                        await _state.Net.SendAsync(Packet.Create(PacketType.StickerSend, new StickerData
                        {
                            From = _state.MyUsername,
                            IsGroup = true,
                            GroupId = _group.Id,
                            StickerName = n,
                            StickerBase64 = b64
                        }));
                    };
                    wrap.Children.Add(imgBtn);
                }
                var mi2 = new MenuItem(); mi2.Header = wrap; menu.Items.Add(mi2);
                menu.Items.Add(new Separator());
            }

            var createItem = new MenuItem { Header = "➕ Create Sticker..." };
            createItem.Click += (s2, e2) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Image for Sticker",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
                };
                if (dlg.ShowDialog() != true) return;
                var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                StickerManager.SaveSticker(name, dlg.FileName);
                AddSystemMessage($"🏷️ Sticker '{name}' created!");
            };
            menu.Items.Add(createItem);
            menu.IsOpen = true;
        }

        private void AddStickerBubble(string sender, string stickerName, string base64, bool isMe)
        {
            var container = new Border
            {
                Background = isMe
                    ? new SolidColorBrush(Color.FromRgb(220, 235, 255))
                    : new SolidColorBrush(Color.FromRgb(235, 255, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(140, 180, 224)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(isMe ? 40 : 0, 3, isMe ? 0 : 40, 3),
                HorizontalAlignment = isMe ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 180
            };
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = sender, FontWeight = FontWeights.Bold, FontSize = 10, Foreground = isMe ? new SolidColorBrush(Color.FromRgb(0, 0, 128)) : new SolidColorBrush(Color.FromRgb(128, 0, 0)) });
            header.Children.Add(new TextBlock { Text = $" ({DateTime.Now:h:mm tt})", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)) });
            stack.Children.Add(header);

            var img = StickerManager.Base64ToImage(base64);
            if (img != null)
                stack.Children.Add(new Image { Source = img, MaxWidth = 120, MaxHeight = 120, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 4, 0, 2) });
            else
                stack.Children.Add(new TextBlock { Text = $"🏷️ {stickerName}", FontSize = 10 });

            container.Child = stack;
            MessagesPanel.Children.Add(container);
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
        }

        public void ReceiveSticker(StickerData data)
        {
            Dispatcher.Invoke(() =>
            {
                var contact = _state.GetContact(data.From);
                var displayName = contact?.DisplayName ?? data.From;
                AddStickerBubble(displayName, data.StickerName, data.StickerBase64, false);
                if (!IsActive) { Title = $"[Sticker] {_group.Name}"; FlashWindow(); }
            });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);
        private void FlashWindow()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            FlashWindow(hwnd, true);
        }

        private async void Toolbar_Nudge(object sender, RoutedEventArgs e)
        {
            AddSystemMessage($"👊 You sent the group a nudge!");
            await _state.Net.SendAsync(Packet.Create(PacketType.Nudge, new NudgeData
            {
                From = _state.MyUsername,
                IsGroup = true,
                GroupId = _group.Id
            }));
            ShakeWindow();
        }

        public void ReceiveNudge(string from)
        {
            Dispatcher.Invoke(() =>
            {
                var contact = _state.GetContact(from);
                var displayName = contact?.DisplayName ?? from;
                AddSystemMessage($"👊 {displayName} sent the group a nudge!");
                ShakeWindow();
                if (!IsActive) { Title = $"[Nudge] {_group.Name}"; FlashWindow(); }
            });
        }

        private void ShakeWindow()
        {
            var originalLeft = Left;
            var originalTop = Top;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            int count = 0;
            var rand = new Random();
            timer.Tick += (s, e) =>
            {
                Left = originalLeft + rand.Next(-5, 6);
                Top = originalTop + rand.Next(-5, 6);
                count++;
                if (count >= 8) { timer.Stop(); Left = originalLeft; Top = originalTop; }
            };
            timer.Start();
        }
    }
}
