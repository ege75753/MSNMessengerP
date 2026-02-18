namespace MSNServer
{
    public class StoredFile
    {
        public string FileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string MimeType { get; set; } = "";
        public long FileSize { get; set; }
        public string UploaderUsername { get; set; } = "";
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        // Path on disk relative to files dir
        public string DiskPath { get; set; } = "";
    }

    /// <summary>
    /// Stores files on disk under data/files/{fileId}.bin
    /// Metadata kept in memory (reloaded from disk on startup via scan).
    /// Max inline size: files ≤ InlineThresholdBytes are sent inline in FileReceive packets.
    /// </summary>
    public class FileStore
    {
        private readonly string _filesDir;
        private readonly Dictionary<string, StoredFile> _files = new();
        private readonly object _lock = new();

        // Files smaller than this get inlined into the FileReceive packet (no extra round-trip)
        public const long InlineThresholdBytes = 2 * 1024 * 1024; // 2 MB
        public const long MaxFileSizeBytes = 50 * 1024 * 1024;    // 50 MB hard limit

        public FileStore(string dataDir)
        {
            _filesDir = Path.Combine(dataDir, "files");
            Directory.CreateDirectory(_filesDir);
            ScanExistingFiles();
        }

        private void ScanExistingFiles()
        {
            // Rebuild metadata from disk on startup using sidecar .meta files
            foreach (var metaPath in Directory.GetFiles(_filesDir, "*.meta"))
            {
                try
                {
                    var lines = File.ReadAllLines(metaPath);
                    var dict = lines
                        .Where(l => l.Contains('='))
                        .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);

                    var sf = new StoredFile
                    {
                        FileId = dict.GetValueOrDefault("FileId", ""),
                        FileName = dict.GetValueOrDefault("FileName", ""),
                        MimeType = dict.GetValueOrDefault("MimeType", "application/octet-stream"),
                        FileSize = long.TryParse(dict.GetValueOrDefault("FileSize", "0"), out var fs) ? fs : 0,
                        UploaderUsername = dict.GetValueOrDefault("Uploader", ""),
                        DiskPath = Path.ChangeExtension(metaPath, ".bin")
                    };

                    if (!string.IsNullOrEmpty(sf.FileId) && File.Exists(sf.DiskPath))
                        _files[sf.FileId] = sf;
                }
                catch { }
            }

            Console.WriteLine($"[FileStore] Loaded {_files.Count} file(s) from disk.");
        }

        public async Task<StoredFile?> StoreAsync(string fileId, string fileName, string mimeType,
            byte[] data, string uploaderUsername)
        {
            if (data.Length > MaxFileSizeBytes) return null;

            var diskPath = Path.Combine(_filesDir, $"{fileId}.bin");
            var metaPath = Path.Combine(_filesDir, $"{fileId}.meta");

            await File.WriteAllBytesAsync(diskPath, data);
            await File.WriteAllTextAsync(metaPath,
                $"FileId={fileId}\nFileName={fileName}\nMimeType={mimeType}\nFileSize={data.Length}\nUploader={uploaderUsername}\n");

            var sf = new StoredFile
            {
                FileId = fileId,
                FileName = fileName,
                MimeType = mimeType,
                FileSize = data.Length,
                UploaderUsername = uploaderUsername,
                DiskPath = diskPath
            };

            lock (_lock) _files[fileId] = sf;
            return sf;
        }

        public StoredFile? GetMeta(string fileId)
        {
            lock (_lock) return _files.TryGetValue(fileId, out var f) ? f : null;
        }

        public async Task<byte[]?> ReadAsync(string fileId)
        {
            var meta = GetMeta(fileId);
            if (meta is null || !File.Exists(meta.DiskPath)) return null;
            return await File.ReadAllBytesAsync(meta.DiskPath);
        }

        public bool Exists(string fileId)
        {
            lock (_lock) return _files.ContainsKey(fileId);
        }

        public void Delete(string fileId)
        {
            lock (_lock)
            {
                if (!_files.TryGetValue(fileId, out var meta)) return;
                _files.Remove(fileId);
                try { File.Delete(meta.DiskPath); } catch { }
                try { File.Delete(Path.ChangeExtension(meta.DiskPath, ".meta")); } catch { }
            }
        }
    }
}
