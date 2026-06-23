using System.Text.Json;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace RadeonFastFileSourcePlugin;

internal static class WarmupManager
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".bmp",
        ".gif",
        ".jfif",
        ".jpeg",
        ".jpg",
        ".jxl",
        ".png",
        ".psd",
        ".svg",
        ".tif",
        ".tiff",
        ".webp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi",
        ".m2ts",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".mts",
        ".ts",
        ".webm",
        ".wmv",
    };

    private static readonly object Gate = new();
    private static readonly HashSet<string> AudioWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImageWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImageDecodeWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task> ImageDecodeWarmupTasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoDecodeWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> VideoDecodeWarmupBlockedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    private static SemaphoreSlim warmupGate = new(6, 6);
    private static int warmupGateSize = 6;
    private static SemaphoreSlim imageDecodeGate = new(1, 1);
    private static int imageDecodeGateSize = 1;
    private static SemaphoreSlim videoDecodeGate = new(1, 1);
    private static int videoDecodeGateSize = 1;
    private static WarmupManifest? cached;
    private static long imageDecodeQueued;
    private static long imageDecodeCompleted;
    private static long imageDecodeFailed;

    private static string ManifestPath => Path.Combine(
        AppContext.BaseDirectory,
        "user",
        "RadeonFastFileSourcePlugin",
        "warmup_manifest.json");

    public static void Record(string type, string filePath, int trackIndex = 0, bool queueWarmup = true)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableProjectWarmup || string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
                return;

            if (!IsRecordable(type, fullPath))
                return;

            lock (Gate)
            {
                var manifest = LoadLocked();
                var key = WarmupEntry.MakeKey(type, fullPath, trackIndex);
                var entry = manifest.Entries.FirstOrDefault(x => x.Key == key);
                if (entry is null)
                {
                    entry = new WarmupEntry
                    {
                        Key = key,
                        Type = type,
                        Path = fullPath,
                        TrackIndex = trackIndex,
                    };
                    manifest.Entries.Add(entry);
                }

                entry.Hits++;
                entry.LastSeenUtc = DateTime.UtcNow;
                TrimLocked(manifest, settings);
                SaveSoonLocked(manifest);
            }

            if (queueWarmup)
                QueueRecordedWarmup(settings, type, fullPath, trackIndex);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"Warmup record failed type={type}: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
        }
    }

    public static void EnsureAudioWarmup(Action<string, int> queuePreload)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableProjectWarmup || !settings.EnableAudioWarmup)
            return;

        foreach (var entry in GetEntries("audio", settings))
        {
            if (!AudioWarmupStarted.Add(entry.Key))
                continue;

            queuePreload(entry.Path, entry.TrackIndex);
        }
    }

    public static void EnsureManifestImageCpuWarmup()
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableProjectWarmup ||
            !settings.EnableImageWarmup ||
            !settings.EnableImageDecodeWarmup ||
            !settings.EnableImageCpuDecodeCache ||
            !settings.EnableNativeImageDecoder)
        {
            return;
        }

        var queued = 0;
        foreach (var entry in GetEntries("image", settings))
        {
            if (Path.GetExtension(entry.Path).Equals(".psd", StringComparison.OrdinalIgnoreCase) ||
                !NativeImageBitmapFactory.IsSupported(entry.Path))
            {
                continue;
            }

            var decodeKey = WarmupEntry.MakeKey("image-decode", entry.Path, entry.TrackIndex);
            if (!TryMarkImageDecodeWarmupStarted(decodeKey))
                continue;

            QueueImageCpuDecodeWarmup(entry.Path, "startup-manifest");
            queued++;
        }

        FastFileSourceLog.Write(
            $"Image CPU startup warmup queued={queued} concurrency={settings.ImageDecodeWarmupMaxConcurrent}");
    }

    public static void EnsureImageWarmup(
        IGraphicsDevicesAndContext devices,
        Func<string, ID2D1Bitmap> createBitmap,
        bool allowBackgroundWarmup = true)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!allowBackgroundWarmup ||
            !settings.EnableProjectWarmup ||
            !settings.EnableImageWarmup)
            return;

        foreach (var entry in GetEntries("image", settings))
        {
            if (!ImageWarmupStarted.Add(entry.Key))
                continue;

            QueueFileWarmup("image", entry.Path, settings.WarmupMaxImageFileMB);
        }

        if (!settings.EnableImageDecodeWarmup)
            return;

        foreach (var entry in GetEntries("image", settings))
        {
            if (Path.GetExtension(entry.Path).Equals(".psd", StringComparison.OrdinalIgnoreCase))
                continue;

            var decodeKey = WarmupEntry.MakeKey("image-decode", entry.Path, entry.TrackIndex);
            if (!TryMarkImageDecodeWarmupStarted(decodeKey))
                continue;

            QueueImageDecodeWarmup(devices, entry.Path, createBitmap);
        }
    }

    public static void EnsureVideoFileWarmup()
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableProjectWarmup || !settings.EnableVideoFileWarmup)
            return;

        foreach (var entry in GetEntries("video", settings))
        {
            if (!VideoWarmupStarted.Add(entry.Key))
                continue;

            QueueFileWarmup("video", entry.Path, settings.WarmupMaxVideoFileMB);
        }
    }

    public static void EnsureVideoDecodeWarmup(
        IGraphicsDevicesAndContext devices,
        string currentFilePath,
        Func<string, WarmupVideoSource?> createSource)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableProjectWarmup || !settings.EnableVideoDecodeWarmup)
            return;

        var entries = PrioritizeCurrentFile(GetEntries("video", settings), currentFilePath)
            .Take(settings.VideoDecodeWarmupMaxQueuedPerCall)
            .ToList();
        var queued = 0;
        foreach (var entry in entries)
        {
            if (IsVideoDecodeWarmupBlocked(entry.Path))
                continue;

            var decodeKey = MakeVideoDecodeWarmupKey(devices, settings, entry);
            if (!TryMarkVideoDecodeWarmupStarted(decodeKey))
                continue;

            QueueVideoDecodeWarmup(devices, entry.Path, createSource);
            queued++;
        }

        if (queued > 0)
            FastFileSourceLog.WriteDetailed($"Video decode warmup batch queued={queued} candidates={entries.Count} current=\"{currentFilePath}\"");
    }

    private static void QueueRecordedWarmup(FastFileSourceSettings settings, string type, string filePath, int trackIndex)
    {
        if (!settings.EnableProjectWarmup)
            return;

        if (type == "image" && settings.EnableImageWarmup)
        {
            var key = WarmupEntry.MakeKey(type, filePath, trackIndex);
            if (ImageWarmupStarted.Add(key))
                QueueFileWarmup("image", filePath, settings.WarmupMaxImageFileMB);

            if (settings.EnableImageDecodeWarmup &&
                !Path.GetExtension(filePath).Equals(".psd", StringComparison.OrdinalIgnoreCase) &&
                NativeImageBitmapFactory.IsSupported(filePath))
            {
                var decodeKey = WarmupEntry.MakeKey("image-decode", filePath, trackIndex);
                if (TryMarkImageDecodeWarmupStarted(decodeKey))
                    QueueImageCpuDecodeWarmup(filePath, "project-discovery");
            }
        }
        else if (type == "video" && settings.EnableVideoFileWarmup)
        {
            var key = WarmupEntry.MakeKey(type, filePath, trackIndex);
            if (VideoWarmupStarted.Add(key))
                QueueFileWarmup("video", filePath, settings.WarmupMaxVideoFileMB);
        }
    }

    private static void QueueFileWarmup(string type, string filePath, int maxFileMB)
    {
        FastFileSourceLog.WriteDetailed($"File warmup queued type={type} path=\"{filePath}\"");
        _ = Task.Run(() => WarmFile(type, filePath, maxFileMB));
    }

    private static void QueueImageDecodeWarmup(
        IGraphicsDevicesAndContext devices,
        string filePath,
        Func<string, ID2D1Bitmap> createBitmap)
    {
        FastFileSourceLog.WriteDetailed($"Image decode warmup queued path=\"{filePath}\"");
        TrackImageDecodeTask(filePath, Task.Run(() => WarmImageDecode(devices, filePath, createBitmap)));
    }

    private static void QueueImageCpuDecodeWarmup(string filePath, string source)
    {
        FastFileSourceLog.WriteDetailed($"Image CPU decode warmup queued source={source} path=\"{filePath}\"");
        TrackImageDecodeTask(filePath, Task.Run(() => WarmImageCpuDecode(filePath, source)));
    }

    private static void TrackImageDecodeTask(string filePath, Task task)
    {
        var key = Path.GetFullPath(filePath);
        Interlocked.Increment(ref imageDecodeQueued);
        lock (Gate)
        {
            ImageDecodeWarmupTasks[key] = task;
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (Gate)
                {
                    if (ImageDecodeWarmupTasks.TryGetValue(key, out var current) &&
                        ReferenceEquals(current, completedTask))
                    {
                        ImageDecodeWarmupTasks.Remove(key);
                    }
                }

                FastFileSourceLog.WriteDetailed($"Image decode warmup progress {GetImageWarmupStatusText()}");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static string GetImageWarmupStatusText()
    {
        int active;
        lock (Gate)
        {
            active = ImageDecodeWarmupTasks.Count;
        }

        return $"画像先読み: 実行中 {active} / 予約 {Interlocked.Read(ref imageDecodeQueued)} / 完了 {Interlocked.Read(ref imageDecodeCompleted)} / 失敗 {Interlocked.Read(ref imageDecodeFailed)}";
    }

    private static void QueueVideoDecodeWarmup(
        IGraphicsDevicesAndContext devices,
        string filePath,
        Func<string, WarmupVideoSource?> createSource)
    {
        FastFileSourceLog.WriteDetailed($"Video decode warmup queued path=\"{filePath}\"");
        _ = Task.Run(() => WarmVideoDecode(devices, filePath, createSource));
    }

    private static void WarmFile(string type, string filePath, int maxFileMB)
    {
        var settings = FastFileSourceSettingsStore.Current;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var gate = GetWarmupGate(settings);
        gate.Wait();
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return;

            var maxBytes = (long)maxFileMB * 1024 * 1024;
            if (info.Length > maxBytes)
            {
                FastFileSourceLog.Write($"File warmup skip type={type} reason=size bytes={info.Length} limit={maxBytes} path=\"{filePath}\"");
                return;
            }

            var buffer = new byte[settings.WarmupReadBufferMB * 1024 * 1024];
            long total = 0;
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan);
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                total += read;
            }

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var mbPerSec = elapsedMs <= 0 ? 0 : total / 1024.0 / 1024.0 / (elapsedMs / 1000.0);
            FastFileSourceLog.Write($"File warmup ready type={type} bytes={total} elapsed={elapsedMs:F3} ms throughput={mbPerSec:F1} MiB/s path=\"{filePath}\"");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"File warmup failed type={type}: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
        }
        finally
        {
            gate.Release();
        }
    }

    private static void WarmImageDecode(
        IGraphicsDevicesAndContext devices,
        string filePath,
        Func<string, ID2D1Bitmap> createBitmap)
    {
        var settings = FastFileSourceSettingsStore.Current;
        var gate = GetImageDecodeGate(settings);
        gate.Wait();
        try
        {
            if (NativeImageCpuCache.TryWarm(filePath, "project-warmup"))
            {
                Interlocked.Increment(ref imageDecodeCompleted);
                FastFileSourceLog.Write($"Image decode warmup ready mode=cpu-cache path=\"{filePath}\"");
                return;
            }

            if (ImageBitmapCache.Contains(devices, filePath))
            {
                Interlocked.Increment(ref imageDecodeCompleted);
                return;
            }

            if (ImageBitmapCache.TryWarm(devices, filePath, () => createBitmap(filePath), "project-warmup"))
                Interlocked.Increment(ref imageDecodeCompleted);
            else
                Interlocked.Increment(ref imageDecodeFailed);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref imageDecodeFailed);
            FastFileSourceLog.Write($"Image decode warmup failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
        }
        finally
        {
            gate.Release();
        }
    }

    private static void WarmImageCpuDecode(string filePath, string source)
    {
        var settings = FastFileSourceSettingsStore.Current;
        var gate = GetImageDecodeGate(settings);
        gate.Wait();
        try
        {
            if (NativeImageCpuCache.TryWarm(filePath, source))
            {
                Interlocked.Increment(ref imageDecodeCompleted);
                FastFileSourceLog.Write($"Image decode warmup ready mode=cpu-cache source={source} path=\"{filePath}\"");
            }
            else
            {
                Interlocked.Increment(ref imageDecodeFailed);
                FastFileSourceLog.Write($"Image decode warmup skipped mode=cpu-cache source={source} path=\"{filePath}\"");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref imageDecodeFailed);
            FastFileSourceLog.Write($"Image decode warmup failed source={source}: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
        }
        finally
        {
            gate.Release();
        }
    }

    private static void WarmVideoDecode(
        IGraphicsDevicesAndContext devices,
        string filePath,
        Func<string, WarmupVideoSource?> createSource)
    {
        var settings = FastFileSourceSettingsStore.Current;
        var gate = GetVideoDecodeGate(settings);
        gate.Wait();
        WarmupVideoSource? warmupSource = null;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var maxUpdateMs = 0.0;
        TimeSpan? lastUpdateTime = null;
        try
        {
            warmupSource = createSource(filePath);
            if (warmupSource is null)
            {
                BlockVideoDecodeWarmup(filePath, "no-source");
                FastFileSourceLog.Write($"Video decode warmup skipped reason=no-source path=\"{filePath}\"");
                return;
            }

            var source = warmupSource.Source;
            var duration = source.Duration;
            if (duration <= TimeSpan.Zero)
            {
                BlockVideoDecodeWarmup(filePath, "invalid-duration");
                FastFileSourceLog.Write($"Video decode warmup skipped reason=invalid-duration backend={warmupSource.Backend} duration={duration} path=\"{filePath}\"");
                return;
            }

            var frames = Math.Max(1, settings.VideoDecodeWarmupFrames);
            for (var i = 0; i < frames; i++)
            {
                var time = i == 0 || duration <= TimeSpan.Zero
                    ? TimeSpan.Zero
                    : TimeSpan.FromTicks(Math.Min(duration.Ticks - 1, TimeSpan.FromSeconds(i / 60.0).Ticks));
                var updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                source.Update(time);
                var updateMs = System.Diagnostics.Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
                maxUpdateMs = Math.Max(maxUpdateMs, updateMs);
                lastUpdateTime = time;
            }

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var returned = VideoSourceCache.TryReturn(
                devices,
                filePath,
                warmupSource.Backend,
                source,
                frames,
                maxUpdateMs,
                lastUpdateTime);
            if (returned)
                warmupSource.Detach();

            FastFileSourceLog.Write($"Video decode warmup ready backend={warmupSource.Backend} frames={frames} duration={duration} elapsed={elapsedMs:F3} ms maxUpdate={maxUpdateMs:F3} ms cached={returned} path=\"{filePath}\"");
        }
        catch (Exception ex)
        {
            BlockVideoDecodeWarmup(filePath, ex.GetType().Name);
            FastFileSourceLog.Write($"Video decode warmup failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
        }
        finally
        {
            warmupSource?.Dispose();
            gate.Release();
        }
    }

    private static SemaphoreSlim GetWarmupGate(FastFileSourceSettings settings)
    {
        var size = Math.Clamp(settings.WarmupMaxConcurrentTasks, 1, 32);
        if (size == warmupGateSize)
            return warmupGate;

        lock (Gate)
        {
            if (size == warmupGateSize)
                return warmupGate;

            warmupGate = new SemaphoreSlim(size, size);
            warmupGateSize = size;
            FastFileSourceLog.Write($"File warmup concurrency={size}");
            return warmupGate;
        }
    }

    private static SemaphoreSlim GetImageDecodeGate(FastFileSourceSettings settings)
    {
        var size = Math.Clamp(settings.ImageDecodeWarmupMaxConcurrent, 1, 4);
        if (size == imageDecodeGateSize)
            return imageDecodeGate;

        lock (Gate)
        {
            if (size == imageDecodeGateSize)
                return imageDecodeGate;

            imageDecodeGate = new SemaphoreSlim(size, size);
            imageDecodeGateSize = size;
            FastFileSourceLog.Write($"Image decode warmup concurrency={size}");
            return imageDecodeGate;
        }
    }

    private static SemaphoreSlim GetVideoDecodeGate(FastFileSourceSettings settings)
    {
        var size = Math.Clamp(settings.VideoDecodeWarmupMaxConcurrent, 1, 4);
        if (size == videoDecodeGateSize)
            return videoDecodeGate;

        lock (Gate)
        {
            if (size == videoDecodeGateSize)
                return videoDecodeGate;

            videoDecodeGate = new SemaphoreSlim(size, size);
            videoDecodeGateSize = size;
            FastFileSourceLog.Write($"Video decode warmup concurrency={size}");
            return videoDecodeGate;
        }
    }

    private static string MakeVideoDecodeWarmupKey(
        IGraphicsDevicesAndContext devices,
        FastFileSourceSettings settings,
        WarmupEntry entry)
    {
        var deviceKey = settings.VideoSourceCacheUseDeviceContextKey
            ? devices.DeviceContext.NativePointer.ToInt64().ToString("X")
            : "any-device";
        return $"{WarmupEntry.MakeKey("video-decode", entry.Path, entry.TrackIndex)}|device={deviceKey}";
    }

    private static bool TryMarkVideoDecodeWarmupStarted(string decodeKey)
    {
        lock (Gate)
        {
            return VideoDecodeWarmupStarted.Add(decodeKey);
        }
    }

    private static bool TryMarkImageDecodeWarmupStarted(string decodeKey)
    {
        lock (Gate)
        {
            return ImageDecodeWarmupStarted.Add(decodeKey);
        }
    }

    private static bool IsVideoDecodeWarmupBlocked(string filePath)
    {
        var key = NormalizeVideoDecodeWarmupPath(filePath);
        if (key is null)
            return true;

        lock (Gate)
        {
            if (!VideoDecodeWarmupBlockedUntilUtc.TryGetValue(key, out var untilUtc))
                return false;

            if (untilUtc > DateTime.UtcNow)
                return true;

            VideoDecodeWarmupBlockedUntilUtc.Remove(key);
            return false;
        }
    }

    private static void BlockVideoDecodeWarmup(string filePath, string reason)
    {
        var key = NormalizeVideoDecodeWarmupPath(filePath);
        if (key is null)
            return;

        lock (Gate)
        {
            VideoDecodeWarmupBlockedUntilUtc[key] = DateTime.UtcNow.AddMinutes(10);
        }

        FastFileSourceLog.WriteDetailed($"Video decode warmup blocked reason={reason} minutes=10 path=\"{filePath}\"");
    }

    private static string? NormalizeVideoDecodeWarmupPath(string filePath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static List<WarmupEntry> GetEntries(string type, FastFileSourceSettings settings)
    {
        lock (Gate)
        {
            return LoadLocked()
                .Entries
                .Where(entry => entry.Type == type && File.Exists(entry.Path) && IsRecordable(type, entry.Path))
                .OrderByDescending(entry => entry.LastSeenUtc)
                .ThenByDescending(entry => entry.Hits)
                .Take(settings.WarmupMaxFiles)
                .Select(entry => entry.Clone())
                .ToList();
        }
    }

    private static IEnumerable<WarmupEntry> PrioritizeCurrentFile(IEnumerable<WarmupEntry> entries, string currentFilePath)
    {
        string? currentFullPath = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(currentFilePath))
                currentFullPath = Path.GetFullPath(currentFilePath);
        }
        catch
        {
            currentFullPath = null;
        }

        if (string.IsNullOrWhiteSpace(currentFullPath))
            return entries;

        return entries
            .OrderByDescending(entry => entry.Path.Equals(currentFullPath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.LastSeenUtc)
            .ThenByDescending(entry => entry.Hits);
    }

    private static bool IsRecordable(string type, string path)
    {
        var extension = Path.GetExtension(path);
        return type switch
        {
            "audio" => AudioExtensions.Contains(extension),
            "image" => ImageExtensions.Contains(extension),
            "video" => VideoExtensions.Contains(extension),
            _ => false,
        };
    }

    private static WarmupManifest LoadLocked()
    {
        if (cached is not null)
            return cached;

        try
        {
            if (File.Exists(ManifestPath))
            {
                var json = File.ReadAllText(ManifestPath);
                cached = JsonSerializer.Deserialize<WarmupManifest>(json, JsonOptions()) ?? new WarmupManifest();
            }
            else
            {
                cached = new WarmupManifest();
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Warmup manifest load failed: {ex.GetType().Name}: {ex.Message}");
            cached = new WarmupManifest();
        }

        return cached;
    }

    private static void TrimLocked(WarmupManifest manifest, FastFileSourceSettings settings)
    {
        var max = Math.Max(settings.WarmupMaxFiles * 3, settings.WarmupMaxFiles);
        if (manifest.Entries.Count <= max)
            return;

        manifest.Entries = manifest
            .Entries
            .OrderByDescending(entry => entry.LastSeenUtc)
            .ThenByDescending(entry => entry.Hits)
            .Take(max)
            .ToList();
    }

    private static void SaveSoonLocked(WarmupManifest manifest)
    {
        try
        {
            var dir = Path.GetDirectoryName(ManifestPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions()));
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Warmup manifest save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
        };
    }

    private sealed class WarmupManifest
    {
        public List<WarmupEntry> Entries { get; set; } = new();
    }

    private sealed class WarmupEntry
    {
        public string Key { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public int TrackIndex { get; set; }

        public int Hits { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public static string MakeKey(string type, string path, int trackIndex)
        {
            return $"{type}|{trackIndex}|{path}";
        }

        public WarmupEntry Clone()
        {
            return new WarmupEntry
            {
                Key = Key,
                Type = Type,
                Path = Path,
                TrackIndex = TrackIndex,
                Hits = Hits,
                LastSeenUtc = LastSeenUtc,
            };
        }
    }
}
