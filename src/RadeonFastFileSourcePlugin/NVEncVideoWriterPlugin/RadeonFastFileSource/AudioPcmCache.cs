using System.Collections.Concurrent;
using YukkuriMovieMaker.Plugin.FileSource;

namespace RadeonFastFileSourcePlugin;

internal static class AudioPcmCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<AudioCacheKey, CacheEntry> Entries = new();
    private static readonly Dictionary<AudioCacheKey, int> OpenCounts = new();
    private static readonly ConcurrentDictionary<AudioCacheKey, Lazy<CacheEntry?>> Inflight = new();
    private static SemaphoreSlim decodeGate = new(4, 4);
    private static int decodeGateSize = 4;
    private static long totalBytes;

    public static IAudioFileSource? TryCreate(
        string backend,
        string filePath,
        int trackIndex,
        Func<IAudioFileSource> createSource)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableAudioPcmCache)
            return null;

        if (!RuntimeContextDetector.IsExportCallStack())
        {
            FastFileSourceLog.WriteDetailed($"Audio cache bypass backend={backend} reason=not-export path=\"{filePath}\"");
            return null;
        }

        if (!ShouldCache(settings, filePath))
            return null;

        if (!TryCreateKey(filePath, trackIndex, out var key))
            return null;

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var cached))
            {
                cached.LastAccessUtc = DateTime.UtcNow;
                cached.HitCount++;
                if (cached.HitCount <= 5 || cached.HitCount % 25 == 0)
                    FastFileSourceLog.Write($"Audio cache hit backend={backend} bytes={cached.Bytes} hits={cached.HitCount} path=\"{filePath}\"");
                return new CachedPcmAudioFileSource(cached, filePath, backend);
            }

            if (!ShouldBuildCacheNow(settings, key, backend, filePath))
            {
                TryStartBackgroundPreload(settings, backend, filePath, key, createSource);
                return null;
            }
        }

        var lazy = Inflight.GetOrAdd(key, _ => new Lazy<CacheEntry?>(
            () => DecodeAndStore(settings, backend, filePath, key, createSource),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var entry = lazy.Value;
            if (entry is null)
                return null;

            FastFileSourceLog.WriteDetailed($"Audio cache ready backend={backend} bytes={entry.Bytes} path=\"{filePath}\"");
            return new CachedPcmAudioFileSource(entry, filePath, backend);
        }
        finally
        {
            Inflight.TryRemove(key, out _);
        }
    }

    public static void QueuePreload(
        string backend,
        string filePath,
        int trackIndex,
        Func<IAudioFileSource> createSource,
        string reason)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableAudioPcmCache || !settings.EnableProjectWarmup || !settings.EnableAudioWarmup)
            return;

        if (!ShouldCache(settings, filePath))
            return;

        if (!TryCreateKey(filePath, trackIndex, out var key))
            return;

        lock (Gate)
        {
            if (Entries.ContainsKey(key))
            {
                FastFileSourceLog.WriteDetailed($"Audio warmup skip backend={backend} reason=already-cached source={reason} path=\"{filePath}\"");
                return;
            }
        }

        TryStartBackgroundPreload(settings, backend, filePath, key, createSource, $"warmup-{reason}");
    }

    private static void TryStartBackgroundPreload(
        FastFileSourceSettings settings,
        string backend,
        string filePath,
        AudioCacheKey key,
        Func<IAudioFileSource> createSource,
        string reason = "deferred")
    {
        if ((!settings.EnableAudioBackgroundPreload && !reason.StartsWith("warmup-", StringComparison.Ordinal)) || IsTempAudio(filePath))
            return;

        lock (Gate)
        {
            if (Entries.ContainsKey(key))
                return;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var lazy = new Lazy<CacheEntry?>(
            () => DecodeAndStore(settings, backend, filePath, key, createSource),
            LazyThreadSafetyMode.ExecutionAndPublication);

        if (!Inflight.TryAdd(key, lazy))
            return;

        FastFileSourceLog.Write($"Audio preload queued backend={backend} reason={reason} path=\"{filePath}\"");
        _ = Task.Run(() =>
        {
            try
            {
                var entry = lazy.Value;
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (entry is null)
                    FastFileSourceLog.Write($"Audio preload skipped backend={backend} reason={reason} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
                else
                    FastFileSourceLog.Write($"Audio preload ready backend={backend} reason={reason} bytes={entry.Bytes} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
            }
            catch (Exception ex)
            {
                FastFileSourceLog.Write($"Audio preload failed backend={backend}: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            }
            finally
            {
                Inflight.TryRemove(key, out _);
            }
        });
    }

    private static bool ShouldBuildCacheNow(
        FastFileSourceSettings settings,
        AudioCacheKey key,
        string backend,
        string filePath)
    {
        var count = OpenCounts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        OpenCounts[key] = count;

        if (count < settings.AudioCacheMinOpenCount)
        {
            FastFileSourceLog.WriteDetailed($"Audio cache defer backend={backend} open={count}/{settings.AudioCacheMinOpenCount} path=\"{filePath}\"");
            TrimOpenCountsIfNeeded();
            return false;
        }

        if (count == settings.AudioCacheMinOpenCount)
            FastFileSourceLog.Write($"Audio cache promote backend={backend} open={count} path=\"{filePath}\"");

        TrimOpenCountsIfNeeded();
        return true;
    }

    private static void TrimOpenCountsIfNeeded()
    {
        if (OpenCounts.Count <= 4096)
            return;

        foreach (var cachedKey in Entries.Keys)
            OpenCounts.Remove(cachedKey);

        if (OpenCounts.Count <= 2048)
            return;

        foreach (var key in OpenCounts.Keys.Take(OpenCounts.Count - 2048).ToArray())
            OpenCounts.Remove(key);
    }

    private static CacheEntry? DecodeAndStore(
        FastFileSourceSettings settings,
        string backend,
        string filePath,
        AudioCacheKey key,
        Func<IAudioFileSource> createSource)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var gate = GetDecodeGate(settings);
        gate.Wait();
        try
        {
            return DecodeAndStoreCore(settings, backend, filePath, key, createSource, start);
        }
        finally
        {
            gate.Release();
        }
    }

    private static CacheEntry? DecodeAndStoreCore(
        FastFileSourceSettings settings,
        string backend,
        string filePath,
        AudioCacheKey key,
        Func<IAudioFileSource> createSource,
        long start)
    {
        using var source = createSource();

        if (source.Hz <= 0)
            throw new InvalidOperationException($"Audio source has invalid sample rate: {filePath}");

        if (source.Duration.TotalSeconds > settings.AudioCacheMaxDurationSeconds)
        {
            FastFileSourceLog.Write($"Audio cache skip duration backend={backend} duration={source.Duration} path=\"{filePath}\"");
            return null;
        }

        var maxSingleBytes = (long)settings.AudioCacheMaxSingleFileMB * 1024 * 1024;
        var maxSingleSamples = maxSingleBytes / sizeof(float);
        var estimatedSamples = EstimateStereoSampleCount(source.Duration, source.Hz, settings.AudioCacheReadChunkSamples);
        if (estimatedSamples > maxSingleSamples)
        {
            FastFileSourceLog.Write($"Audio cache skip estimated-size backend={backend} samples={estimatedSamples} limit={maxSingleSamples} path=\"{filePath}\"");
            return null;
        }

        var data = new float[Math.Max(settings.AudioCacheReadChunkSamples, estimatedSamples)];
        var totalSamples = 0;

        source.Seek(TimeSpan.Zero);
        while (true)
        {
            if (data.Length - totalSamples < settings.AudioCacheReadChunkSamples)
            {
                var nextLength = Math.Min(
                    maxSingleSamples,
                    Math.Max((long)data.Length * 2, (long)data.Length + settings.AudioCacheReadChunkSamples));
                if (nextLength <= data.Length)
                {
                    FastFileSourceLog.Write($"Audio cache skip single-limit backend={backend} bytes={(long)totalSamples * sizeof(float)} limit={maxSingleBytes} path=\"{filePath}\"");
                    return null;
                }

                Array.Resize(ref data, checked((int)nextLength));
            }

            var read = source.Read(data, totalSamples, Math.Min(settings.AudioCacheReadChunkSamples, data.Length - totalSamples));
            if (read <= 0)
                break;

            totalSamples += read;
            var bytesSoFar = (long)totalSamples * sizeof(float);
            if (bytesSoFar > maxSingleBytes)
            {
                FastFileSourceLog.Write($"Audio cache skip single-limit backend={backend} bytes={bytesSoFar} limit={maxSingleBytes} path=\"{filePath}\"");
                return null;
            }
        }

        if (totalSamples == 0)
        {
            FastFileSourceLog.Write($"Audio cache skip empty backend={backend} path=\"{filePath}\"");
            return null;
        }

        var entry = new CacheEntry(
            key,
            data,
            totalSamples,
            source.Hz,
            source.Duration,
            DateTime.UtcNow);

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                existing.LastAccessUtc = DateTime.UtcNow;
                return existing;
            }

            Entries.Add(key, entry);
            totalBytes += entry.Bytes;
            EvictIfNeeded(settings);
        }

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        FastFileSourceLog.Write($"Audio cache store backend={backend} samples={totalSamples} bytes={entry.Bytes} hz={entry.Hz} duration={entry.Duration} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
        return entry;
    }

    private static int EstimateStereoSampleCount(TimeSpan duration, int hz, int readChunkSamples)
    {
        var expected = Math.Ceiling(duration.TotalSeconds * hz * 2);
        if (double.IsNaN(expected) || double.IsInfinity(expected) || expected <= 0)
            return readChunkSamples;

        var withMargin = Math.Min(int.MaxValue, expected + readChunkSamples);
        return Math.Max(readChunkSamples, checked((int)withMargin));
    }

    private static SemaphoreSlim GetDecodeGate(FastFileSourceSettings settings)
    {
        var size = Math.Clamp(settings.AudioCacheMaxConcurrentDecodes, 1, 32);
        if (size == decodeGateSize)
            return decodeGate;

        lock (Gate)
        {
            if (size == decodeGateSize)
                return decodeGate;

            decodeGate = new SemaphoreSlim(size, size);
            decodeGateSize = size;
            FastFileSourceLog.Write($"Audio cache decode concurrency={size}");
            // The old gate is intentionally not Disposed: threads that captured a reference
            // before this replacement may still be inside gate.Wait() or gate.Release().
            // Calling Dispose() while a thread is blocked in Wait() throws
            // ObjectDisposedException and breaks the decode pipeline.
            // The abandoned semaphore is collected by the GC finalizer.
            return decodeGate;
        }
    }

    private static bool ShouldCache(FastFileSourceSettings settings, string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (settings.CacheTempAudio && extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            return true;

        if (settings.CacheMp3Audio && extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!settings.CacheMediaAudio)
            return false;

        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTempAudio(string filePath)
    {
        return Path.GetExtension(filePath).Equals(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateKey(string filePath, int trackIndex, out AudioCacheKey key)
    {
        key = default;
        try
        {
            var info = new FileInfo(filePath);
            key = new AudioCacheKey(
                Path.GetFullPath(filePath),
                trackIndex,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EvictIfNeeded(FastFileSourceSettings settings)
    {
        var maxBytes = (long)settings.AudioCacheMaxMemoryMB * 1024 * 1024;
        if (totalBytes <= maxBytes)
            return;

        foreach (var entry in Entries.Values.OrderBy(e => e.LastAccessUtc).ToArray())
        {
            if (totalBytes <= maxBytes)
                break;

            Entries.Remove(entry.Key);
            totalBytes -= entry.Bytes;
            FastFileSourceLog.Write($"Audio cache evict bytes={entry.Bytes} totalBytes={totalBytes} path=\"{entry.Key.FilePath}\"");
        }
    }

    private readonly record struct AudioCacheKey(string FilePath, int TrackIndex, long Length, long LastWriteTimeUtcTicks);

    private sealed class CacheEntry(
        AudioCacheKey key,
        float[] samples,
        int sampleCount,
        int hz,
        TimeSpan duration,
        DateTime lastAccessUtc)
    {
        public AudioCacheKey Key { get; } = key;

        public float[] Samples { get; } = samples;

        public int SampleCount { get; } = sampleCount;

        public int Hz { get; } = hz;

        public TimeSpan Duration { get; } = duration;

        public long Bytes { get; } = (long)sampleCount * sizeof(float);

        public double SamplesPerSecond { get; } = duration.TotalSeconds > 0
            ? sampleCount / duration.TotalSeconds
            : hz * 2.0;

        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;

        public int HitCount { get; set; }
    }

    private sealed class CachedPcmAudioFileSource(CacheEntry entry, string filePath, string backend) : IAudioFileSource
    {
        private int position;
        private int readCount;
        private int seekCount;

        public TimeSpan Duration => entry.Duration;

        public int Hz => entry.Hz;

        public int Read(float[] destBuffer, int offset, int count)
        {
            var available = Math.Max(0, entry.SampleCount - position);
            var toCopy = Math.Min(count, available);
            if (toCopy > 0)
            {
                Array.Copy(entry.Samples, position, destBuffer, offset, toCopy);
                position += toCopy;
            }

            readCount++;
            if (readCount == 1)
                FastFileSourceLog.WriteDetailed($"Audio cache first Read backend={backend} samples={count} read={toCopy} hz={Hz} duration={Duration} path=\"{filePath}\"");

            return toCopy;
        }

        public void Seek(TimeSpan time)
        {
            var samplePosition = (long)Math.Round(time.TotalSeconds * entry.SamplesPerSecond);
            position = (int)Math.Clamp(samplePosition, 0, entry.SampleCount);
            seekCount++;
            if (seekCount == 1)
                FastFileSourceLog.WriteDetailed($"Audio cache first Seek backend={backend} time={time} sample={position} path=\"{filePath}\"");
        }

        public void Dispose()
        {
            entry.LastAccessUtc = DateTime.UtcNow;
            FastFileSourceLog.WriteDetailed($"Audio cache dispose backend={backend} reads={readCount} seeks={seekCount} path=\"{filePath}\"");
        }
    }
}
