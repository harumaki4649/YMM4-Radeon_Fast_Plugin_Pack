using System.Collections.Concurrent;

namespace RadeonFastFileSourcePlugin;

internal static class NativeImageCpuCache
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<ImageCpuCacheKey, object> KeyGates = new();
    private static readonly Dictionary<ImageCpuCacheKey, CacheEntry> Entries = new();
    private static long totalBytes;

    public static bool TryGetOrDecode(string filePath, string reason, out DecodedNativeImage image)
    {
        image = default;
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableImageCpuDecodeCache ||
            !settings.EnableNativeImageDecoder ||
            !NativeImageBitmapFactory.IsSupported(filePath) ||
            !TryCreateKey(filePath, out var key))
        {
            return false;
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var cached))
            {
                cached.LastAccessUtc = DateTime.UtcNow;
                cached.HitCount++;
                image = cached.Image;
                if (cached.HitCount <= 5 || cached.HitCount % 25 == 0)
                    FastFileSourceLog.Write($"Image CPU decode cache hit reason={reason} hits={cached.HitCount} bytes={cached.Bytes} totalBytes={totalBytes} size={image.Width}x{image.Height} path=\"{filePath}\"");
                return true;
            }
        }

        var keyGate = KeyGates.GetOrAdd(key, _ => new object());
        lock (keyGate)
        {
            lock (Gate)
            {
                if (Entries.TryGetValue(key, out var cached))
                {
                    cached.LastAccessUtc = DateTime.UtcNow;
                    cached.HitCount++;
                    image = cached.Image;
                    FastFileSourceLog.Write($"Image CPU decode cache hit-wait reason={reason} hits={cached.HitCount} bytes={cached.Bytes} totalBytes={totalBytes} size={image.Width}x{image.Height} path=\"{filePath}\"");
                    return true;
                }
            }

            if (!NativeImageBitmapFactory.TryDecodeToCpu(filePath, out var decoded))
                return false;

            var maxSingleBytes = (long)settings.ImageCpuDecodeCacheMaxSingleFileMB * 1024 * 1024;
            if (decoded.Bytes > maxSingleBytes)
            {
                FastFileSourceLog.Write($"Image CPU decode cache skip reason=single-limit bytes={decoded.Bytes} limit={maxSingleBytes} path=\"{filePath}\"");
                image = decoded;
                return true;
            }

            var entry = new CacheEntry(key, decoded, decoded.Bytes, DateTime.UtcNow);
            lock (Gate)
            {
                Entries[key] = entry;
                totalBytes += entry.Bytes;
                EvictIfNeeded(settings);
            }

            image = decoded;
            FastFileSourceLog.Write($"Image CPU decode cache store reason={reason} bytes={entry.Bytes} totalBytes={totalBytes} size={decoded.Width}x{decoded.Height} path=\"{filePath}\"");
            return true;
        }
    }

    public static bool TryWarm(string filePath, string reason)
    {
        return TryGetOrDecode(filePath, reason, out _);
    }

    public static bool Remove(string filePath, string reason)
    {
        if (!TryCreateKey(filePath, out var key))
            return false;

        lock (Gate)
        {
            if (!Entries.Remove(key, out var entry))
                return false;

            totalBytes -= entry.Bytes;
            FastFileSourceLog.WriteDetailed(
                $"Image CPU decode cache release reason={reason} bytes={entry.Bytes} totalBytes={totalBytes} path=\"{filePath}\"");
            return true;
        }
    }

    private static void EvictIfNeeded(FastFileSourceSettings settings)
    {
        var maxBytes = (long)settings.ImageCpuDecodeCacheMaxMemoryMB * 1024 * 1024;
        if (totalBytes <= maxBytes)
            return;

        foreach (var entry in Entries.Values.OrderBy(entry => entry.LastAccessUtc).ToArray())
        {
            if (totalBytes <= maxBytes)
                break;

            Entries.Remove(entry.Key);
            totalBytes -= entry.Bytes;
            FastFileSourceLog.Write($"Image CPU decode cache evict bytes={entry.Bytes} totalBytes={totalBytes} path=\"{entry.Key.FilePath}\"");
        }
    }

    private static bool TryCreateKey(string filePath, out ImageCpuCacheKey key)
    {
        key = default;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return false;

            key = new ImageCpuCacheKey(
                Path.GetFullPath(filePath),
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct ImageCpuCacheKey(string FilePath, long Length, long LastWriteTimeUtcTicks);

    private sealed class CacheEntry(ImageCpuCacheKey key, DecodedNativeImage image, long bytes, DateTime lastAccessUtc)
    {
        public ImageCpuCacheKey Key { get; } = key;
        public DecodedNativeImage Image { get; } = image;
        public long Bytes { get; } = bytes;
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
        public int HitCount { get; set; }
    }
}
