using System.Collections.Concurrent;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;

namespace RadeonFastFileSourcePlugin;

internal static class ImageBitmapCache
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<ImageCacheKey, object> KeyGates = new();
    private static readonly Dictionary<ImageCacheKey, CacheEntry> Entries = new();
    private static long totalBytes;

    public static bool Contains(IGraphicsDevicesAndContext devices, string filePath)
    {
        if (!TryCreateKey(devices, filePath, out var key))
            return false;

        lock (Gate)
        {
            return Entries.ContainsKey(key);
        }
    }

    public static bool TryWarm(
        IGraphicsDevicesAndContext devices,
        string filePath,
        Func<ID2D1Bitmap> createBitmap,
        string reason)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableImageBitmapCache || !TryCreateKey(devices, filePath, out var key))
            return false;

        lock (Gate)
        {
            if (Entries.ContainsKey(key))
                return true;
        }

        var fileSize = TryGetFileSize(filePath);
        var maxSingleBytes = (long)settings.ImageCacheMaxSingleFileMB * 1024 * 1024;
        if (fileSize > maxSingleBytes)
        {
            FastFileSourceLog.Write($"Image decode warmup skip reason=file-size bytes={fileSize} limit={maxSingleBytes} path=\"{filePath}\"");
            return false;
        }

        var keyGate = KeyGates.GetOrAdd(key, _ => new object());
        lock (keyGate)
        {
            lock (Gate)
            {
                if (Entries.ContainsKey(key))
                    return true;
            }

            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var bitmap = createBitmap();
            var bytes = EstimateBytes(bitmap);
            if (bytes > maxSingleBytes)
            {
                FastFileSourceLog.Write($"Image decode warmup skip reason=bitmap-size bytes={bytes} limit={maxSingleBytes} path=\"{filePath}\"");
                bitmap.Dispose();
                return false;
            }

            var entry = new CacheEntry(key, bitmap, bytes, DateTime.UtcNow);
            lock (Gate)
            {
                Entries.Add(key, entry);
                totalBytes += bytes;
                EvictIfNeeded(settings);
            }
            NativeImageCpuCache.Remove(filePath, "gpu-warm-ready");

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Image decode warmup ready reason={reason} elapsed={elapsedMs:F3} ms bytes={bytes} totalBytes={totalBytes} size={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} path=\"{filePath}\"");
            return true;
        }
    }

    public static ID2D1Bitmap CreateOrGet(
        IGraphicsDevicesAndContext devices,
        string filePath,
        Func<ID2D1Bitmap> createBitmap)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableImageBitmapCache || !TryCreateKey(devices, filePath, out var key))
            return createBitmap();

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var cached))
            {
                cached.LastAccessUtc = DateTime.UtcNow;
                cached.HitCount++;
                if (cached.HitCount <= 5 || cached.HitCount % 25 == 0)
                    FastFileSourceLog.Write($"Image cache hit bytes={cached.Bytes} hits={cached.HitCount} size={cached.Bitmap.PixelSize.Width}x{cached.Bitmap.PixelSize.Height} path=\"{filePath}\"");

                return CreateSharedReference(cached.Bitmap);
            }
        }

        var fileSize = TryGetFileSize(filePath);
        var maxSingleBytes = (long)settings.ImageCacheMaxSingleFileMB * 1024 * 1024;
        if (fileSize > maxSingleBytes)
        {
            FastFileSourceLog.Write($"Image cache skip file-size bytes={fileSize} limit={maxSingleBytes} path=\"{filePath}\"");
            return createBitmap();
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
                    FastFileSourceLog.Write($"Image cache hit-wait bytes={cached.Bytes} hits={cached.HitCount} size={cached.Bitmap.PixelSize.Width}x{cached.Bitmap.PixelSize.Height} path=\"{filePath}\"");
                    return CreateSharedReference(cached.Bitmap);
                }
            }

            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var bitmap = createBitmap();
            var bytes = EstimateBytes(bitmap);
            if (bytes > maxSingleBytes)
            {
                FastFileSourceLog.Write($"Image cache skip bitmap-size bytes={bytes} limit={maxSingleBytes} path=\"{filePath}\"");
                return bitmap;
            }

            var entry = new CacheEntry(key, bitmap, bytes, DateTime.UtcNow);
            lock (Gate)
            {
                Entries.Add(key, entry);
                totalBytes += bytes;
                EvictIfNeeded(settings);
            }
            NativeImageCpuCache.Remove(filePath, "gpu-cache-ready");

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Image cache store elapsed={elapsedMs:F3} ms bytes={bytes} size={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} path=\"{filePath}\"");
            return CreateSharedReference(bitmap);
        }
    }

    private static ID2D1Bitmap CreateSharedReference(ID2D1Bitmap bitmap)
    {
        bitmap.AddRef();
        return new ID2D1Bitmap(bitmap.NativePointer);
    }

    private static void EvictIfNeeded(FastFileSourceSettings settings)
    {
        var maxBytes = (long)settings.ImageCacheMaxMemoryMB * 1024 * 1024;
        if (totalBytes <= maxBytes)
            return;

        foreach (var entry in Entries.Values.OrderBy(e => e.LastAccessUtc).ToArray())
        {
            if (totalBytes <= maxBytes)
                break;

            Entries.Remove(entry.Key);
            totalBytes -= entry.Bytes;
            FastFileSourceLog.Write($"Image cache evict bytes={entry.Bytes} totalBytes={totalBytes} path=\"{entry.Key.FilePath}\"");
            entry.Bitmap.Dispose();
        }
    }

    private static bool TryCreateKey(IGraphicsDevicesAndContext devices, string filePath, out ImageCacheKey key)
    {
        key = default;
        try
        {
            var info = new FileInfo(filePath);
            key = new ImageCacheKey(
                Path.GetFullPath(filePath),
                devices.DeviceContext.NativePointer,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long TryGetFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private static long EstimateBytes(ID2D1Bitmap bitmap)
    {
        return (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
    }

    private readonly record struct ImageCacheKey(string FilePath, IntPtr DeviceContext, long Length, long LastWriteTimeUtcTicks);

    private sealed class CacheEntry(ImageCacheKey key, ID2D1Bitmap bitmap, long bytes, DateTime lastAccessUtc)
    {
        public ImageCacheKey Key { get; } = key;

        public ID2D1Bitmap Bitmap { get; } = bitmap;

        public long Bytes { get; } = bytes;

        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;

        public int HitCount { get; set; }
    }
}
