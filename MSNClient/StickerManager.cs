using System.IO;
using System.Windows.Media.Imaging;

namespace MSNClient
{
    /// <summary>
    /// Manages locally stored stickers — small images users can quickly send in chat.
    /// Stickers are stored as PNG files under %AppData%/MSNMessenger/Stickers/.
    /// </summary>
    public static class StickerManager
    {
        private static readonly string StickersFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MSNMessenger", "Stickers");

        static StickerManager()
        {
            Directory.CreateDirectory(StickersFolder);
        }

        /// <summary>Save an image file as a named sticker (copies as PNG).</summary>
        public static string SaveSticker(string name, string sourceFilePath)
        {
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var destPath = Path.Combine(StickersFolder, safeName + ".png");

            // Load, re-encode as PNG, and save
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(sourceFilePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 120; // keep stickers small
            bitmap.EndInit();
            bitmap.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);

            return destPath;
        }

        /// <summary>Save sticker from base64 data (received sticker).</summary>
        public static string SaveStickerFromBase64(string name, string base64)
        {
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var destPath = Path.Combine(StickersFolder, safeName + ".png");
            if (File.Exists(destPath)) return destPath; // already saved
            var bytes = Convert.FromBase64String(base64);
            File.WriteAllBytes(destPath, bytes);
            return destPath;
        }

        /// <summary>Get all saved stickers as (name, filePath, base64) tuples.</summary>
        public static List<(string Name, string FilePath, string Base64)> GetAllStickers()
        {
            var result = new List<(string, string, string)>();
            if (!Directory.Exists(StickersFolder)) return result;

            foreach (var file in Directory.GetFiles(StickersFolder, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var bytes = File.ReadAllBytes(file);
                result.Add((name, file, Convert.ToBase64String(bytes)));
            }
            return result;
        }

        /// <summary>Load a sticker as a BitmapImage.</summary>
        public static BitmapImage? LoadStickerImage(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(filePath, UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 120;
            img.EndInit();
            img.Freeze();
            return img;
        }

        /// <summary>Convert base64 sticker data to a BitmapImage for display.</summary>
        public static BitmapImage? Base64ToImage(string base64)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64);
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(bytes);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 120;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>Delete a sticker by name.</summary>
        public static void DeleteSticker(string name)
        {
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(StickersFolder, safeName + ".png");
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
