using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace RadeonFastFileSourcePlugin;

internal static class VideoSourceCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<VideoSourceCacheKey, List<Entry>> Entries = new();
    private static readonly HashSet<string> DeviceMismatchLogged = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool disabledCacheCleared = true;

    public static CachedVideoSourceLease? TryTake(IGraphicsDevicesAndContext devices, string filePath, string backend)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableVideoSourceCache || settings.VideoSourceCacheMaxEntries <= 0)
        {
            ClearIfDisabled();
            return null;
        }

        disabledCacheCleared = false;

        if (!TryCreateKey(devices, filePath, backend, out var key))
            return null;

        lock (Gate)
        {
            EvictExpiredLocked(settings);

            if (TryTakeExistingLocked(key, settings, backend, filePath, out var lease, out var missReason))
                return lease;

            LogDeviceMismatchIfAnyLocked(key, settings);

            var waitMs = settings.EnableProjectWarmup && settings.EnableVideoDecodeWarmup
                ? settings.VideoSourceCacheWaitForWarmupMs
                : 0;
            if (waitMs > 0)
            {
                var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var remainingMs = waitMs;
                while (remainingMs > 0)
                {
                    Monitor.Wait(Gate, remainingMs);
                    EvictExpiredLocked(settings);
                    if (TryTakeExistingLocked(key, settings, backend, filePath, out lease, out missReason))
                    {
                        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
                        FastFileSourceLog.Write(
                            $"Video source cache wait-hit backend={backend} waited={elapsedMs:F3} ms path=\"{filePath}\"");
                        return lease;
                    }

                    remainingMs = waitMs - (int)Math.Ceiling(System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds);
                }

                FastFileSourceLog.WriteDetailed(
                    $"Video source cache wait-miss backend={backend} reason={missReason} waited={System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds:F3} ms path=\"{filePath}\"");
            }
            else
            {
                FastFileSourceLog.WriteDetailed($"Video source cache miss backend={backend} reason={missReason} path=\"{filePath}\"");
            }

            return null;
        }
    }

    private static bool TryTakeExistingLocked(
        VideoSourceCacheKey key,
        FastFileSourceSettings settings,
        string backend,
        string filePath,
        out CachedVideoSourceLease? lease,
        out string missReason)
    {
        lease = null;
        missReason = "no-entry";

        if (!Entries.TryGetValue(key, out var list))
            return false;

        while (list.Count > 0)
        {
            var last = list.Count - 1;
            var entry = list[last];

            if (entry.Duration <= TimeSpan.Zero)
            {
                list.RemoveAt(last);
                entry.Source.Dispose();
                FastFileSourceLog.Write($"Video source cache evict backend={backend} reason=invalid-duration duration={entry.Duration} path=\"{filePath}\"");
                continue;
            }

            if (IsExpired(entry, settings))
            {
                list.RemoveAt(last);
                entry.Source.Dispose();
                FastFileSourceLog.Write($"Video source cache evict backend={backend} reason=expired ageSec={(DateTime.UtcNow - entry.ReturnedUtc).TotalSeconds:F1} path=\"{filePath}\"");
                continue;
            }

            var ageMs = (DateTime.UtcNow - entry.ReturnedUtc).TotalMilliseconds;
            if (ageMs < settings.VideoSourceCacheMinReuseAgeMs)
            {
                missReason = "too-young";
                FastFileSourceLog.WriteDetailed(
                    $"Video source cache miss backend={backend} reason=too-young ageMs={ageMs:F0} minAgeMs={settings.VideoSourceCacheMinReuseAgeMs} path=\"{filePath}\"");
                return false;
            }

            list.RemoveAt(last);
            FastFileSourceLog.Write(
                $"Video source cache hit backend={backend} updates={entry.UpdateCount} bytes={entry.Key.Length} ageSec={(DateTime.UtcNow - entry.ReturnedUtc).TotalSeconds:F1} remaining={list.Count} deviceKey={settings.VideoSourceCacheUseDeviceContextKey} path=\"{filePath}\"");
            lease = new CachedVideoSourceLease(entry.Source, entry.LastUpdateTime, entry.UpdateCount, entry.MaxUpdateMs);
            return true;
        }

        Entries.Remove(key);
        missReason = "empty";
        return false;
    }

    public static bool TryReturn(
        IGraphicsDevicesAndContext devices,
        string filePath,
        string backend,
        IVideoFileSource source,
        int updateCount,
        double maxUpdateMs,
        TimeSpan? lastUpdateTime)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableVideoSourceCache || settings.VideoSourceCacheMaxEntries <= 0)
        {
            ClearIfDisabled();
            return false;
        }

        disabledCacheCleared = false;

        if (!TryCreateKey(devices, filePath, backend, out var key))
            return false;

        // Read source.Duration outside the shared lock: the property may internally call
        // COM/MediaFoundation APIs that can block for an unpredictable amount of time,
        // and holding Gate during that window serialises all VideoSourceCache callers.
        TimeSpan duration;
        try
        {
            duration = source.Duration;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write(
                $"Video source cache reject backend={backend} reason=duration-failed error={ex.GetType().Name}: {ex.Message} updates={updateCount} bytes={key.Length} path=\"{filePath}\"");
            return false;
        }

        if (duration <= TimeSpan.Zero)
        {
            FastFileSourceLog.Write(
                $"Video source cache reject backend={backend} reason=invalid-duration duration={duration} updates={updateCount} bytes={key.Length} path=\"{filePath}\"");
            return false;
        }

        if (updateCount <= 0)
        {
            FastFileSourceLog.Write(
                $"Video source cache reject backend={backend} reason=no-updates updates={updateCount} bytes={key.Length} path=\"{filePath}\"");
            return false;
        }

        lock (Gate)
        {
            EvictExpiredLocked(settings);

            var isProbe = updateCount < settings.VideoSourceCacheMinUpdatesToKeep;
            var keepSlowProbe = isProbe && maxUpdateMs >= settings.VideoSourceCacheMinSlowUpdateToKeepMs;
            if (isProbe && !keepSlowProbe)
            {
                FastFileSourceLog.Write(
                    $"Video source cache reject backend={backend} reason=too-few-updates updates={updateCount} minUpdates={settings.VideoSourceCacheMinUpdatesToKeep} maxUpdate={maxUpdateMs:F3} ms slowKeepMs={settings.VideoSourceCacheMinSlowUpdateToKeepMs:F3} bytes={key.Length} path=\"{filePath}\"");
                return false;
            }

            if (!Entries.TryGetValue(key, out var list))
            {
                list = new List<Entry>();
                Entries.Add(key, list);
            }

            list.Add(new Entry(key, source, DateTime.UtcNow, updateCount, maxUpdateMs, lastUpdateTime, duration));
            Monitor.PulseAll(Gate);
            FastFileSourceLog.Write(
                $"Video source cache return backend={backend} reason={(keepSlowProbe ? "slow-probe" : "normal")} entries={CountEntriesLocked()} probeEntries={CountProbeEntriesLocked()} updates={updateCount} maxUpdate={maxUpdateMs:F3} ms duration={duration} bytes={key.Length} deviceKey={settings.VideoSourceCacheUseDeviceContextKey} path=\"{filePath}\"");
            EvictProbeOverflowLocked(settings);
            EvictOverflowLocked(settings);
            return true;
        }
    }

    private static bool TryCreateKey(IGraphicsDevicesAndContext devices, string filePath, string backend, out VideoSourceCacheKey key)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                key = default;
                return false;
            }

            var settings = FastFileSourceSettingsStore.Current;
            key = new VideoSourceCacheKey(
                Path.GetFullPath(filePath),
                backend,
                settings.VideoSourceCacheUseDeviceContextKey ? devices.DeviceContext.NativePointer : IntPtr.Zero,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            key = default;
            return false;
        }
    }

    private static void ClearIfDisabled()
    {
        lock (Gate)
        {
            if (disabledCacheCleared)
                return;

            var count = CountEntriesLocked();
            foreach (var entry in Entries.Values.SelectMany(list => list))
                entry.Source.Dispose();

            Entries.Clear();
            disabledCacheCleared = true;
            FastFileSourceLog.Write($"Video source cache cleared reason=disabled entries={count}");
        }
    }

    private static void EvictExpiredLocked(FastFileSourceSettings settings)
    {
        if (Entries.Count == 0)
            return;

        foreach (var pair in Entries.ToArray())
        {
            var list = pair.Value;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                if (!IsExpired(entry, settings))
                    continue;

                list.RemoveAt(i);
                entry.Source.Dispose();
                FastFileSourceLog.Write($"Video source cache evict backend={entry.Key.Backend} reason=expired path=\"{entry.Key.FilePath}\"");
            }

            if (list.Count == 0)
                Entries.Remove(pair.Key);
        }
    }

    private static void EvictOverflowLocked(FastFileSourceSettings settings)
    {
        var maxEntries = settings.VideoSourceCacheMaxEntries;
        var count = CountEntriesLocked();
        while (count > maxEntries)
        {
            var victim = Entries
                .SelectMany(pair => pair.Value.Select((entry, index) => new { pair.Key, Entry = entry, Index = index }))
                .OrderBy(item => KeepScore(item.Entry, settings))
                .ThenBy(item => item.Entry.ReturnedUtc)
                .FirstOrDefault();

            if (victim is null)
                return;

            var list = Entries[victim.Key];
            list.RemoveAt(victim.Index);
            victim.Entry.Source.Dispose();
            count--;
            FastFileSourceLog.Write(
                $"Video source cache evict backend={victim.Entry.Key.Backend} reason=overflow entries={count} updates={victim.Entry.UpdateCount} bytes={victim.Entry.Key.Length} score={KeepScore(victim.Entry, settings):F1} path=\"{victim.Entry.Key.FilePath}\"");
            if (list.Count == 0)
                Entries.Remove(victim.Key);
        }
    }

    private static void EvictProbeOverflowLocked(FastFileSourceSettings settings)
    {
        var maxProbeEntries = settings.VideoSourceCacheMaxProbeEntries;
        if (maxProbeEntries <= 0)
            return;

        var probeCount = CountProbeEntriesLocked();
        while (probeCount > maxProbeEntries)
        {
            var victim = Entries
                .SelectMany(pair => pair.Value.Select((entry, index) => new { pair.Key, Entry = entry, Index = index }))
                .Where(item => item.Entry.UpdateCount < settings.VideoSourceCacheMinUpdatesToKeep)
                .OrderBy(item => item.Entry.MaxUpdateMs)
                .ThenBy(item => item.Entry.ReturnedUtc)
                .FirstOrDefault();

            if (victim is null)
                return;

            var list = Entries[victim.Key];
            list.RemoveAt(victim.Index);
            victim.Entry.Source.Dispose();
            probeCount--;
            FastFileSourceLog.Write(
                $"Video source cache evict backend={victim.Entry.Key.Backend} reason=probe-overflow probeEntries={probeCount} updates={victim.Entry.UpdateCount} maxUpdate={victim.Entry.MaxUpdateMs:F3} ms path=\"{victim.Entry.Key.FilePath}\"");
            if (list.Count == 0)
                Entries.Remove(victim.Key);
        }
    }

    private static double KeepScore(Entry entry, FastFileSourceSettings settings)
    {
        var score = 0.0;
        if (entry.UpdateCount >= settings.VideoSourceCacheMinUpdatesToKeep)
            score += 1000.0;

        var largeFileBytes = (long)settings.VideoSourceCachePreferLargeFileMB * 1024 * 1024;
        if (entry.Key.Length >= largeFileBytes)
            score += 250.0;

        score += Math.Min(entry.UpdateCount, 5000) * 0.2;
        score += Math.Min(entry.MaxUpdateMs, 1000.0) * 0.5;
        score += Math.Min(entry.Key.Length / (1024.0 * 1024.0), 2048.0) * 0.1;
        return score;
    }

    private static bool IsExpired(Entry entry, FastFileSourceSettings settings)
    {
        return DateTime.UtcNow - entry.ReturnedUtc > TimeSpan.FromSeconds(settings.VideoSourceCacheTtlSeconds);
    }

    private static void LogDeviceMismatchIfAnyLocked(VideoSourceCacheKey requested, FastFileSourceSettings settings)
    {
        if (!settings.VideoSourceCacheUseDeviceContextKey)
            return;

        var candidates = Entries
            .Where(pair =>
                pair.Key.FilePath.Equals(requested.FilePath, StringComparison.OrdinalIgnoreCase) &&
                pair.Key.Backend.Equals(requested.Backend, StringComparison.OrdinalIgnoreCase) &&
                pair.Key.Length == requested.Length &&
                pair.Key.LastWriteTimeUtcTicks == requested.LastWriteTimeUtcTicks &&
                pair.Key.DeviceContext != requested.DeviceContext)
            .Sum(pair => pair.Value.Count);

        if (candidates <= 0)
            return;

        var logKey = $"{requested.Backend}|{requested.FilePath}|{requested.DeviceContext}";
        if (!DeviceMismatchLogged.Add(logKey))
            return;

        FastFileSourceLog.Write(
            $"Video source cache miss backend={requested.Backend} reason=device-mismatch candidates={candidates} requestedDevice=0x{requested.DeviceContext.ToInt64():X} path=\"{requested.FilePath}\"");
    }

    private static int CountEntriesLocked()
    {
        return Entries.Values.Sum(list => list.Count);
    }

    private static int CountProbeEntriesLocked()
    {
        return Entries.Values.Sum(list => list.Count(entry => entry.UpdateCount < FastFileSourceSettingsStore.Current.VideoSourceCacheMinUpdatesToKeep));
    }

    private readonly record struct VideoSourceCacheKey(
        string FilePath,
        string Backend,
        IntPtr DeviceContext,
        long Length,
        long LastWriteTimeUtcTicks);

    private sealed class Entry(
        VideoSourceCacheKey key,
        IVideoFileSource source,
        DateTime returnedUtc,
        int updateCount,
        double maxUpdateMs,
        TimeSpan? lastUpdateTime,
        TimeSpan duration)
    {
        public VideoSourceCacheKey Key { get; } = key;
        public IVideoFileSource Source { get; } = source;
        public DateTime ReturnedUtc { get; } = returnedUtc;
        public int UpdateCount { get; } = updateCount;
        public double MaxUpdateMs { get; } = maxUpdateMs;
        public TimeSpan? LastUpdateTime { get; } = lastUpdateTime;
        public TimeSpan Duration { get; } = duration;
    }
}

internal sealed class CachedVideoSourceLease(
    IVideoFileSource source,
    TimeSpan? lastUpdateTime,
    int updateCount,
    double maxUpdateMs)
{
    public IVideoFileSource Source { get; } = source;
    public TimeSpan? LastUpdateTime { get; } = lastUpdateTime;
    public int UpdateCount { get; } = updateCount;
    public double MaxUpdateMs { get; } = maxUpdateMs;
}
