using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MSNShared;

namespace MSNClient
{
    /// <summary>
    /// Handles file sending/receiving and profile pictures.
    /// Profile picture fixes:
    ///   1. Cache keyed on fileId (not username) – new upload always misses cache
    ///   2. Old fileId evicted from cache before upload completes
    ///   3. Images decoded at 2x display size + HighQuality scaling – no pixelation
    /// </summary>
    public class FileTransferManager
    {
        private readonly ClientState _state;

        // Cache: fileId -> ImageSource  (null means "confirmed no picture for this id")
        private readonly Dictionary<string, ImageSource?> _picCache = new();
        private readonly object _cacheLock = new();

        private readonly Dictionary<string, TaskCompletionSource<FileDataResponse?>> _pendingDownloads = new();
        private readonly Dictionary<string, TaskCompletionSource<ProfilePicDataResponse?>> _pendingProfilePics = new();
        private readonly object _pendingLock = new();

        public FileTransferManager(ClientState state)
        {
            _state = state;
            _state.Net.PacketReceived += OnPacket;
        }

        private void OnPacket(Packet pkt)
        {
            switch (pkt.Type)
            {
                case PacketType.FileData:
                    var fd = pkt.GetData<FileDataResponse>();
                    if (fd != null) CompleteFileDownload(fd);
                    break;
                case PacketType.ProfilePicData:
                    var ppd = pkt.GetData<ProfilePicDataResponse>();
                    if (ppd != null) CompleteProfilePicDownload(ppd);
                    break;
            }
        }

        // ── Send file ──────────────────────────────────────────────────────────
        public async Task<(bool success, string message, string fileId)> SendFileAsync(
            string filePath, string to, bool isGroup)
        {
            if (!File.Exists(filePath)) return (false, "File not found.", "");
            var info = new FileInfo(filePath);
            if (info.Length > FileStore.MaxFileSizeBytes)
                return (false, $"File too large (max {FileStore.MaxFileSizeBytes / 1024 / 1024} MB).", "");

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(filePath); }
            catch (Exception ex) { return (false, $"Could not read file: {ex.Message}", ""); }

            var mime = MimeTypes.FromFileName(filePath);
            var tcs = new TaskCompletionSource<FileSendAckData?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(Packet p)
            {
                if (p.Type == PacketType.FileSendAck)
                { _state.Net.PacketReceived -= Handler; tcs.TrySetResult(p.GetData<FileSendAckData>()); }
            }
            _state.Net.PacketReceived += Handler;

            await _state.Net.SendAsync(Packet.Create(PacketType.FileSend, new FileSendData
            {
                To = to, IsGroup = isGroup,
                FileName = Path.GetFileName(filePath), FileSize = info.Length,
                MimeType = mime, DataBase64 = Convert.ToBase64String(bytes)
            }));

            var ack = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);

            return ack?.Success == true
                ? (true, "File sent.", ack.FileId)
                : (false, ack?.Message ?? "Send failed.", "");
        }

         // ── Download file ──────────────────────────────────────────────────────
        public async Task<FileDataResponse?> DownloadFileAsync(string fileId)
        {
            TaskCompletionSource<FileDataResponse?>? existingTcs;
            lock (_pendingLock)
            {
                if (_pendingDownloads.TryGetValue(fileId, out var ex))
                {
                    existingTcs = ex;
                }
                else
                {
                    existingTcs = new TaskCompletionSource<FileDataResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingDownloads[fileId] = existingTcs;
                }
            }
            bool isExisting = false;
            lock (_pendingLock)
            {
                isExisting = _pendingDownloads.Count > 1 && _pendingDownloads.ContainsKey(fileId);
            }
            if (isExisting && existingTcs != null) return await existingTcs.Task;
            await _state.Net.SendAsync(Packet.Create(PacketType.FileRequest, new FileRequestData { FileId = fileId }));
            try { return await existingTcs!.Task.WaitAsync(TimeSpan.FromSeconds(60)); }

            catch { return null; }
            finally { lock (_pendingLock) _pendingDownloads.Remove(fileId); }
        }

        private void CompleteFileDownload(FileDataResponse fd)
        {
            lock (_pendingLock)
            { if (_pendingDownloads.TryGetValue(fd.FileId, out var tcs)) tcs.TrySetResult(fd); }
        }

        public async Task<bool> SaveFileAsync(FileDataResponse fd, string savePath)
        {
            if (!fd.Found || string.IsNullOrEmpty(fd.DataBase64)) return false;
            try { await File.WriteAllBytesAsync(savePath, Convert.FromBase64String(fd.DataBase64)); return true; }
            catch { return false; }
        }

        // ── Profile pictures ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the profile picture for 'username', using fileId-keyed cache.
        /// displayPx = intended display size in WPF pixels (we decode at 2x for HiDPI).
        /// </summary>
        public async Task<ImageSource?> GetProfilePictureAsync(string username, int displayPx = 46)
        {
            // Determine the fileId we expect
            string fileId;
            if (username == _state.MyUsername)
                fileId = _state.MyProfilePicFileId;
            else
                fileId = _state.GetContact(username)?.ProfilePicFileId ?? "";

            if (string.IsNullOrEmpty(fileId)) return null;

            // Cache hit?
            lock (_cacheLock)
            { if (_picCache.TryGetValue(fileId, out var hit)) return hit; }

            // Fetch from server
            var result = await FetchProfilePicFromServerAsync(username);
            if (result?.Found != true || string.IsNullOrEmpty(result.DataBase64))
            {
                lock (_cacheLock) _picCache[fileId] = null;
                return null;
            }

            var img = BytesToImage(Convert.FromBase64String(result.DataBase64), displayPx);
            lock (_cacheLock) _picCache[fileId] = img;
            return img;
        }

        /// <summary>Evict a specific fileId from cache (call before/after re-upload).</summary>
        public void EvictCache(string fileId)
        {
            if (string.IsNullOrEmpty(fileId)) return;
            lock (_cacheLock) _picCache.Remove(fileId);
        }

        private async Task<ProfilePicDataResponse?> FetchProfilePicFromServerAsync(string username)
        {
            TaskCompletionSource<ProfilePicDataResponse?>? existingTcs;
            lock (_pendingLock)
            {
                if (_pendingProfilePics.TryGetValue(username, out var ex))
                {
                    existingTcs = ex;
                }
                else
                {
                    existingTcs = new TaskCompletionSource<ProfilePicDataResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingProfilePics[username] = existingTcs;
                }
            }
            bool isExisting = false;
            lock (_pendingLock)
            {
                isExisting = _pendingProfilePics.Count > 1 && _pendingProfilePics.ContainsKey(username);
            }
            if (isExisting && existingTcs != null) return await existingTcs.Task;
            await _state.Net.SendAsync(Packet.Create(PacketType.RequestProfilePic, new RequestProfilePicData { Username = username }));
            try { return await existingTcs!.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { return null; }
            finally { lock (_pendingLock) _pendingProfilePics.Remove(username); }
        }

        private void CompleteProfilePicDownload(ProfilePicDataResponse ppd)
        {
            lock (_pendingLock)
            { if (_pendingProfilePics.TryGetValue(ppd.Username, out var tcs)) tcs.TrySetResult(ppd); }

            if (!ppd.Found || string.IsNullOrEmpty(ppd.DataBase64)) return;

            var img = BytesToImage(Convert.FromBase64String(ppd.DataBase64), 46);
            if (img == null) return;

            if (!string.IsNullOrEmpty(ppd.FileId))
            { lock (_cacheLock) _picCache[ppd.FileId] = img; }

            var contact = _state.GetContact(ppd.Username);
            if (contact != null)
                System.Windows.Application.Current?.Dispatcher.Invoke(() => contact.ProfilePicture = img);
        }

        /// <summary>Upload a new profile picture. Evicts old cache entry first.</summary>
        public async Task<(bool success, string message)> UploadProfilePictureAsync(string filePath)
        {
            if (!File.Exists(filePath)) return (false, "File not found.");
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(filePath); }
            catch (Exception ex) { return (false, ex.Message); }
            if (bytes.Length > 5 * 1024 * 1024) return (false, "Image must be under 5 MB.");
            var mime = MimeTypes.FromFileName(filePath);
            if (!MimeTypes.IsImage(mime)) return (false, "Not a supported image type.");

            // Evict stale cache entry BEFORE sending
            EvictCache(_state.MyProfilePicFileId);

            var tcs = new TaskCompletionSource<ProfilePictureAckData?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(Packet p)
            {
                if (p.Type == PacketType.ProfilePictureAck)
                { _state.Net.PacketReceived -= Handler; tcs.TrySetResult(p.GetData<ProfilePictureAckData>()); }
            }
            _state.Net.PacketReceived += Handler;

            await _state.Net.SendAsync(Packet.Create(PacketType.ProfilePictureUpdate,
                new ProfilePictureUpdateData { MimeType = mime, DataBase64 = Convert.ToBase64String(bytes) }));

            var ack = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);

            if (ack?.Success == true)
            {
                var img = BytesToImage(bytes, 46);
                _state.MyProfilePicture = img;
                _state.MyProfilePicFileId = ack.FileId;
                // Store under the brand-new fileId
                if (!string.IsNullOrEmpty(ack.FileId))
                { lock (_cacheLock) _picCache[ack.FileId] = img; }
                return (true, "Profile picture updated!");
            }
            return (false, ack?.Message ?? "Upload failed.");
        }

        // ── Image helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Decode raw bytes → frozen BitmapImage.
        /// displayPx: intended display size in WPF device-independent pixels.
        ///   We decode at 2× so images are sharp on HiDPI / 125 % scaling.
        ///   Pass 0 to skip the hint (full resolution).
        /// </summary>
        public static ImageSource? BytesToImage(byte[] bytes, int displayPx = 0)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                if (displayPx > 0) bi.DecodePixelWidth = Math.Max(displayPx * 2, 92);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        public static ImageSource? Base64ToImage(string base64, int displayPx = 0)
        {
            try { return BytesToImage(Convert.FromBase64String(base64), displayPx); }
            catch { return null; }
        }

        public static ImageSource? FileToImage(string filePath, int displayPx = 0)
        {
            try { return BytesToImage(File.ReadAllBytes(filePath), displayPx); }
            catch { return null; }
        }

        /// <summary>
        /// Create a WPF Image element that renders crisply at any size.
        /// Uses HighQuality BitmapScaling to avoid the blurry default nearest-neighbour.
        /// </summary>
        public static Image MakeCrispImage(ImageSource src, double maxW = double.NaN, double maxH = double.NaN)
        {
            var img = new Image
            {
                Source = src,
                Stretch = Stretch.UniformToFill,
                StretchDirection = StretchDirection.Both
            };
            if (!double.IsNaN(maxW)) img.MaxWidth = maxW;
            if (!double.IsNaN(maxH)) img.MaxHeight = maxH;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
    }

    // Expose FileStore constants to client without referencing server project
    public static class FileStore
    {
        public const long MaxFileSizeBytes = 50 * 1024 * 1024;
        public const long InlineThresholdBytes = 2 * 1024 * 1024;
    }
}
