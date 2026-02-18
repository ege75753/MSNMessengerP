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
    public partial class ChatWindow : Window
    {
        private readonly ContactVm _contact;
        private readonly ClientState _state = App.State;
        private Color _myColor = Color.FromRgb(0, 0, 128);
        private string _myFont = "Tahoma";
        private int _myFontSize = 10;
        private bool _isBold, _isItalic, _isUnderline;
        private DispatcherTimer? _typingDebounce;
        private bool _isSendingTyping;

        private static readonly string[] Emoticons =
            { "😊", "😂", "😍", "😎", "😢", "😡", "🤔", "👍", "👎", "❤️", "🎉", "🔥", "😜", "🙈", "💀", "😴", "🤣" };

        public ChatWindow(ContactVm contact)
        {
            InitializeComponent();
            _contact = contact;
            Title = $"{contact.DisplayName} - Conversation";

            ChatToText.Text = $"To: {contact.DisplayName} <{contact.Username}>";
            UpdateContactStatus(null);
            ContactAvatar.Text = contact.AvatarEmoji;
            SideAvatar.Text = contact.AvatarEmoji;

            // Load profile picture if available
            _ = LoadContactProfilePicAsync();

            SetupFontCombos();
            InputBox.Focus();
        }

        private async Task LoadContactProfilePicAsync()
        {
            if (!_contact.HasProfilePicture) return;
            var img = await App.FileTransfer.GetProfilePictureAsync(_contact.Username);
            if (img != null)
            {
                Dispatcher.Invoke(() =>
                {
                    ContactAvatarImage.Source = img;
                    ContactAvatarImage.Visibility = Visibility.Visible;
                    ContactAvatar.Visibility = Visibility.Collapsed;
                    SideAvatarImage.Source = img;
                    SideAvatarImage.Visibility = Visibility.Visible;
                    SideAvatar.Visibility = Visibility.Collapsed;
                });
            }
        }

        public void UpdateContactStatus(PresenceData? pd)
        {
            Dispatcher.Invoke(() =>
            {
                var status = pd?.Status ?? _contact.Status;
                var pm = pd?.PersonalMessage ?? _contact.PersonalMessage;
                ContactStatusInfo.Text = $"{status}" + (string.IsNullOrEmpty(pm) ? "" : $" - {pm}");
                StatusWarning.Text = status == UserStatus.Away ? $"ℹ  {_contact.DisplayName} may not reply — status is Away." : "";
                if (pd?.AvatarEmoji != null) { ContactAvatar.Text = pd.AvatarEmoji; SideAvatar.Text = pd.AvatarEmoji; }
            });
        }

        public void ReceiveMessage(ChatMessageData msg)
        {
            Dispatcher.Invoke(() =>
            {
                TypingText.Text = "";
                AddMessage(msg.From, msg.Content, false, msg.Color, msg.FontFamily, msg.FontSize, msg.Bold, msg.Italic, msg.Underline);
                if (!IsActive) { Title = $"[New] {_contact.DisplayName} - Conversation"; FlashWindow(); }
            });
        }

        public void SetTyping(bool typing)
        {
            Dispatcher.Invoke(() =>
            {
                TypingText.Text = typing ? "typing..." : "";
            });
        }

        public void ReceiveNudge()
        {
            Dispatcher.Invoke(() =>
            {
                AddSystemMessage($"👊 {_contact.DisplayName} sent you a nudge!");
                ShakeWindow();
                if (!IsActive) { Title = $"[Nudge!] {_contact.DisplayName}"; FlashWindow(); }
            });
        }

        private void AddMessage(string sender, string text, bool isMe, string color = "#000080",
            string font = "Tahoma", int size = 10, bool bold = false, bool italic = false, bool underline = false)
        {
            var para = new StackPanel { Margin = new Thickness(0, 2, 0, 3) };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock
            {
                Text = sender + " ",
                FontWeight = FontWeights.Bold,
                FontSize = 10,
                Foreground = isMe ? new SolidColorBrush(Color.FromRgb(0, 0, 128)) : new SolidColorBrush(Color.FromRgb(128, 0, 0))
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"({DateTime.Now:h:mm tt})",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130))
            });

            var msgBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(font),
                FontSize = size,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 0, 0, 0),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = underline ? TextDecorations.Underline : null
            };

            para.Children.Add(headerPanel);
            para.Children.Add(msgBlock);
            MessagesPanel.Children.Add(para);
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
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
            AddMessage(_state.MyDisplayName, text, true, colorHex, _myFont, _myFontSize, _isBold, _isItalic, _isUnderline);
            ClearInput();

            await _state.Net.SendAsync(Packet.Create(PacketType.ChatMessage, new ChatMessageData
            {
                From = _state.MyUsername,
                To = _contact.Username,
                Content = text,
                Color = colorHex,
                FontFamily = _myFont,
                FontSize = _myFontSize,
                Bold = _isBold,
                Italic = _isItalic,
                Underline = _isUnderline
            }));

            Title = $"{_contact.DisplayName} - Conversation";
            SendStatus.Text = "Sent";
        }

        private async void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                await SendMessage();
            }
        }

        private async void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            SendStatus.Text = "";
            if (_isSendingTyping) return;
            _isSendingTyping = true;
            await _state.Net.SendAsync(Packet.Create(PacketType.ChatTyping, new TypingData
            {
                From = _state.MyUsername, To = _contact.Username, IsTyping = true
            }));

            _typingDebounce?.Stop();
            _typingDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _typingDebounce.Tick += async (s, _) =>
            {
                _typingDebounce.Stop();
                _isSendingTyping = false;
                await _state.Net.SendAsync(Packet.Create(PacketType.ChatTyping, new TypingData
                {
                    From = _state.MyUsername, To = _contact.Username, IsTyping = false
                }));
            };
            _typingDebounce.Start();
        }

        private void SetupFontCombos()
        {
            foreach (var f in new[] { "Tahoma", "Arial", "Comic Sans MS", "Courier New", "Times New Roman", "Verdana" })
                FontCombo.Items.Add(f);
            FontCombo.SelectedIndex = 0;
            for (int i = 8; i <= 20; i += 2) SizeCombo.Items.Add(i);
            SizeCombo.SelectedIndex = 1;
        }

        private void Font_Changed(object sender, SelectionChangedEventArgs e)
        { if (FontCombo.SelectedItem is string f) _myFont = f; }

        private void Size_Changed(object sender, SelectionChangedEventArgs e)
        { if (SizeCombo.SelectedItem is int s) _myFontSize = s; }


        private void Bold_Click(object sender, RoutedEventArgs e) { _isBold = !_isBold; ((Button)sender).FontWeight = _isBold ? FontWeights.Bold : FontWeights.Normal; }
        private void Italic_Click(object sender, RoutedEventArgs e) { _isItalic = !_isItalic; ((Button)sender).FontStyle = _isItalic ? FontStyles.Italic : FontStyles.Normal; }
        private void Underline_Click(object sender, RoutedEventArgs e) { _isUnderline = !_isUnderline; }


        private void Color_Click(object sender, RoutedEventArgs e)
        {
            var colors = new[] { ("#000080", "Navy"), ("#800000", "Dark Red"), ("#008000", "Green"), ("#800080", "Purple"), ("#FF6600", "Orange"), ("#000000", "Black"), ("#CC0066", "Pink") };
            var menu = new ContextMenu();
            foreach (var (hex, name) in colors)
            {
                var h = hex;
                var item = new MenuItem { Header = name, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)) };
                item.Click += (s2, e2) => _myColor = (Color)ColorConverter.ConvertFromString(h);
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
                btn.Click += (s2, e2) =>
                {
                    menu.IsOpen = false;
                    var range = InputBox.Selection;
                    range.Text = em2;
                    InputBox.Focus();
                };
                wrap.Children.Add(btn);
            }
            var mi = new MenuItem(); mi.Header = wrap; menu.Items.Add(mi);
            menu.IsOpen = true;
        }

        private async void Toolbar_Nudge(object sender, RoutedEventArgs e)
        {
            AddSystemMessage($"👊 You sent {_contact.DisplayName} a nudge!");
            await _state.Net.SendAsync(Packet.Create(PacketType.Nudge, new NudgeData { From = _state.MyUsername, To = _contact.Username }));
            ShakeWindow();
        }

        private async void Toolbar_File(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Send File",
                Filter = "All Files|*.*|Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Documents|*.pdf;*.txt;*.doc;*.docx|Archives|*.zip;*.rar;*.7z"
            };
            if (dlg.ShowDialog() != true) return;

            var info = new FileInfo(dlg.FileName);
            if (info.Length > FileStore.MaxFileSizeBytes)
            {
                MessageBox.Show($"File is too large. Maximum size is {FileStore.MaxFileSizeBytes / 1024 / 1024} MB.", "File Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AddSystemMessage($"📤 Sending '{Path.GetFileName(dlg.FileName)}'... ({MimeTypes.FriendlySize(info.Length)})");
            SendStatus.Text = "Sending file...";

            var (success, message, fileId) = await App.FileTransfer.SendFileAsync(dlg.FileName, _contact.Username, false);

            if (success)
            {
                SendStatus.Text = "File sent ✅";
                // Show it in our own chat as sent (since server only routes to recipient)
                AddFileBubble(_state.MyDisplayName, Path.GetFileName(dlg.FileName), info.Length, MimeTypes.FromFileName(dlg.FileName),
                    fileId, isMe: true, inlineBase64: null, filePath: dlg.FileName);
            }
            else
            {
                AddSystemMessage($"❌ Failed to send file: {message}");
                SendStatus.Text = "";
            }
        }

        public void ReceiveFile(FileReceiveData fr)
        {
            Dispatcher.Invoke(() =>
            {
                AddFileBubble(fr.FromDisplayName.Length > 0 ? fr.FromDisplayName : fr.From,
                    fr.FileName, fr.FileSize, fr.MimeType,
                    fr.FileId, isMe: false, inlineBase64: fr.InlineDataBase64);
                if (!IsActive) { Title = $"[File] {_contact.DisplayName}"; FlashWindow(); }
            });
        }

        private void AddFileBubble(string sender, string fileName, long fileSize, string mimeType,
            string fileId, bool isMe, string? inlineBase64, string? filePath = null)
        {
            var isImage = MimeTypes.IsImage(mimeType);
            var container = new Border
            {
                Background = isMe
                    ? new SolidColorBrush(Color.FromRgb(220, 235, 255))
                    : new SolidColorBrush(Color.FromRgb(235, 255, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(140, 180, 224)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(isMe ? 40 : 0, 3, isMe ? 0 : 40, 3),
                HorizontalAlignment = isMe ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 380
            };

            var stack = new StackPanel();

            // Header
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(new TextBlock { Text = isMe ? "You" : sender, FontWeight = FontWeights.Bold, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 153)) });
            header.Children.Add(new TextBlock { Text = $" sent a file  ({DateTime.Now:h:mm tt})", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)) });
            stack.Children.Add(header);

            if (isImage)
            {
                // Show image inline if we have data, else show load button
                if (!string.IsNullOrEmpty(inlineBase64) || (filePath != null && File.Exists(filePath)))
                {
                    var img = !string.IsNullOrEmpty(inlineBase64)
                        ? FileTransferManager.Base64ToImage(inlineBase64)
                        : FileTransferManager.FileToImage(filePath!);

                    if (img != null)
                    {
                        var image = new Image
                        {
                            Source = img,
                            MaxWidth = 340,
                            MaxHeight = 280,
                            Stretch = System.Windows.Media.Stretch.Uniform,
                            Margin = new Thickness(0, 4, 0, 4),
                            Cursor = Cursors.Hand
                        };
                        image.MouseLeftButtonUp += (s, e) => ShowFullImage(img, fileName);
                        stack.Children.Add(image);
                    }
                    else
                    {
                        stack.Children.Add(new TextBlock { Text = $"🖼️  {fileName}", FontSize = 11 });
                    }
                }
                else
                {
                    // Need to fetch from server
                    var imgPlaceholder = new StackPanel { Orientation = Orientation.Horizontal };
                    imgPlaceholder.Children.Add(new TextBlock { Text = "🖼️  ", FontSize = 14 });
                    var loadBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = $"Load image: {fileName} ({MimeTypes.FriendlySize(fileSize)})", FontSize = 10 };
                    loadBtn.Click += async (s, e) =>
                    {
                        loadBtn.IsEnabled = false;
                        loadBtn.Content = "Loading...";
                        var fd = await App.FileTransfer.DownloadFileAsync(fileId);
                        if (fd?.Found == true)
                        {
                            var img = FileTransferManager.Base64ToImage(fd.DataBase64);
                            if (img != null)
                            {
                                imgPlaceholder.Visibility = Visibility.Collapsed;
                                var image = new Image { Source = img, MaxWidth = 340, MaxHeight = 280, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand };
                                image.MouseLeftButtonUp += (s2, e2) => ShowFullImage(img, fileName);
                                var idx = stack.Children.IndexOf(imgPlaceholder);
                                stack.Children.Insert(idx >= 0 ? idx : stack.Children.Count, image);
                            }
                        }
                        else { loadBtn.Content = "Failed to load ❌"; }
                    };
                    imgPlaceholder.Children.Add(loadBtn);
                    stack.Children.Add(imgPlaceholder);
                }
            }
            else
            {
                // Non-image file — show download button
                var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                fileRow.Children.Add(new TextBlock { Text = GetFileIcon(mimeType) + "  ", FontSize = 16 });
                var fileInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                fileInfo.Children.Add(new TextBlock { Text = fileName, FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
                fileInfo.Children.Add(new TextBlock { Text = MimeTypes.FriendlySize(fileSize), FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)) });
                fileRow.Children.Add(fileInfo);
                stack.Children.Add(fileRow);

                var dlBtn = new Button { Style = (Style)Application.Current.FindResource("MSNBtn"), Content = "⬇ Save File", Margin = new Thickness(0, 4, 0, 0), FontSize = 10 };
                dlBtn.Click += async (s, e) =>
                {
                    dlBtn.IsEnabled = false;
                    dlBtn.Content = "Downloading...";

                    FileDataResponse? fd;
                    // For files we just sent ourselves, save directly from disk — no server round-trip
                    if (isMe && filePath != null && File.Exists(filePath))
                    {
                        fd = new FileDataResponse { Found = true, FileName = fileName, MimeType = mimeType, DataBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(filePath)) };
                    }
                    else
                    {
                        fd = await App.FileTransfer.DownloadFileAsync(fileId);
                    }

                    if (fd?.Found == true)
                    {
                        var saveDlg = new Microsoft.Win32.SaveFileDialog { FileName = fd.FileName, Filter = "All Files|*.*" };
                        if (saveDlg.ShowDialog() == true)
                        {
                            var ok = await App.FileTransfer.SaveFileAsync(fd, saveDlg.FileName);
                            dlBtn.Content = ok ? "✅ Saved!" : "❌ Save failed";
                            if (ok) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDlg.FileName}\"");
                        }
                        else { dlBtn.IsEnabled = true; dlBtn.Content = "⬇ Save File"; }
                    }
                    else { dlBtn.Content = "❌ Download failed"; }
                };
                stack.Children.Add(dlBtn);
            }

            container.Child = stack;
            MessagesPanel.Children.Add(container);
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
        }

        private static string GetFileIcon(string mime) => mime switch
        {
            var m when m.StartsWith("image/") => "🖼️",
            var m when m.StartsWith("video/") => "🎬",
            var m when m.StartsWith("audio/") => "🎵",
            "application/pdf" => "📄",
            "application/zip" or "application/x-zip-compressed" => "🗜️",
            "text/plain" => "📝",
            _ => "📁"
        };

        private void ShowFullImage(System.Windows.Media.ImageSource img, string title)
        {
            var w = new Window
            {
                Title = title,
                Background = System.Windows.Media.Brushes.Black,
                Width = 800, Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            w.Content = new Image { Source = img, Stretch = System.Windows.Media.Stretch.Uniform };
            w.Show();
        }

        private void Toolbar_Block(object sender, RoutedEventArgs e) =>
            MessageBox.Show($"{_contact.DisplayName} has been blocked.", "MSN Messenger", MessageBoxButton.OK, MessageBoxImage.Information);

        private void ShakeWindow()
        {
            var origLeft = Left; var origTop = Top;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            int count = 0;
            timer.Tick += (s, e) =>
            {
                count++;
                if (count > 14) { timer.Stop(); Left = origLeft; Top = origTop; return; }
                Left = origLeft + (count % 2 == 0 ? -6 : 6);
                Top = origTop + (count % 3 == 0 ? -3 : 3);
            };
            timer.Start();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        private void FlashWindow()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            FlashWindow(hwnd, true);
        }
    }
}
