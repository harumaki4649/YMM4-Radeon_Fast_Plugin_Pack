using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.MediaFoundation;

namespace RadeonFastFileSourcePlugin;

public sealed class RadeonFastVideoFileSourcePlugin : IVideoFileSourcePlugin
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
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

    public RadeonFastVideoFileSourcePlugin()
    {
        FastFileSourceLog.Write("Video plugin constructed");
    }

    public string Name => "Radeon 高速動画読み込み";

    public IVideoFileSource? CreateVideoFileSource(IGraphicsDevicesAndContext devices, string filePath)
    {
        FastFileSourceLog.Write($"Video plugin create path=\"{filePath}\"");
        WarmupManager.EnsureVideoFileWarmup();

        var extension = Path.GetExtension(filePath);
        if (!SupportedVideoExtensions.Contains(extension))
        {
            FastFileSourceLog.Write($"Video rejected unsupported extension=\"{extension}\" path=\"{filePath}\"");
            return null;
        }

        WarmupManager.Record("video", filePath);
        WarmupManager.EnsureVideoDecodeWarmup(devices, filePath, path => CreateWarmupSource(devices, path));

        var settings = FastFileSourceSettingsStore.Current;
        if (settings.EnableNativeVideoDecoder)
        {
            var nativeSource = NativeVideoFileSource.TryCreate(devices, filePath);
            if (nativeSource is not null)
            {
                if (settings.EnableVideoSourceCache)
                    FastFileSourceLog.WriteDetailed($"Video source cache bypassed for native backend path=\"{filePath}\"");
                return nativeSource;
            }
        }

        var adaptivePreferMf = VideoBackendAdaptivePreference.ShouldPreferMediaFoundation(filePath, settings, out var adaptiveReason);
        var preferMediaFoundation = settings.PreferMediaFoundationVideo || adaptivePreferMf;
        FastFileSourceLog.Write($"Video backend order preferMF={settings.PreferMediaFoundationVideo} adaptiveMF={adaptivePreferMf} adaptiveReason={adaptiveReason} path=\"{filePath}\"");

        if (preferMediaFoundation)
            return TryCreateMediaFoundation(devices, filePath) ?? TryCreateFFmpeg(devices, filePath);

        return TryCreateFFmpeg(devices, filePath) ?? TryCreateMediaFoundation(devices, filePath);
    }

    private static WarmupVideoSource? CreateWarmupSource(IGraphicsDevicesAndContext devices, string filePath)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (settings.PreferMediaFoundationVideo)
            return CreateRawMediaFoundation(devices, filePath) ?? CreateRawFFmpeg(devices, filePath);

        return CreateRawFFmpeg(devices, filePath) ?? CreateRawMediaFoundation(devices, filePath);
    }

    private static WarmupVideoSource? CreateRawFFmpeg(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            return new WarmupVideoSource(new FFmpegVideoFileSource(devices, filePath), "FFmpeg");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"Video decode warmup FFmpeg create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
    }

    private static WarmupVideoSource? CreateRawMediaFoundation(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            var source = new MFVideoFileSourcePlugin().CreateVideoFileSource(devices, filePath);
            return source is null ? null : new WarmupVideoSource(source, "MediaFoundation");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"Video decode warmup MediaFoundation create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
    }

    private static IVideoFileSource? TryCreateFFmpeg(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            var cached = VideoSourceCache.TryTake(devices, filePath, "FFmpeg");
            if (cached is not null)
                return new TimingVideoFileSource(
                    cached.Source,
                    devices,
                    filePath,
                    "FFmpeg",
                    fromCache: true,
                    cached.LastUpdateTime,
                    cached.UpdateCount,
                    cached.MaxUpdateMs,
                    () => new FFmpegVideoFileSource(devices, filePath));

            using var _ = FastFileSourceLog.Measure("Video FFmpeg create");
            var source = new FFmpegVideoFileSource(devices, filePath);
            var warmedAt = TryWarmInitialFrame(source, filePath, "FFmpeg");
            FastFileSourceLog.Write($"Video accepted backend=FFmpeg duration={source.Duration} path=\"{filePath}\"");
            return new TimingVideoFileSource(source, devices, filePath, "FFmpeg", fromCache: false, cachedLastUpdateTime: warmedAt, cachedUpdateCount: 0, cachedMaxUpdateMs: 0, recreate: null);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Video FFmpeg create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
    }

    private static IVideoFileSource? TryCreateMediaFoundation(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            var cached = VideoSourceCache.TryTake(devices, filePath, "MediaFoundation");
            if (cached is not null)
                return new TimingVideoFileSource(
                    cached.Source,
                    devices,
                    filePath,
                    "MediaFoundation",
                    fromCache: true,
                    cached.LastUpdateTime,
                    cached.UpdateCount,
                    cached.MaxUpdateMs,
                    () =>
                    {
                        var source = new MFVideoFileSourcePlugin().CreateVideoFileSource(devices, filePath);
                        if (source is null)
                            throw new InvalidOperationException("MediaFoundation returned null");
                        return source;
                    });

            using var _ = FastFileSourceLog.Measure("Video MediaFoundation create");
            var source = new MFVideoFileSourcePlugin().CreateVideoFileSource(devices, filePath);
            if (source is null)
            {
                FastFileSourceLog.Write($"Video MediaFoundation returned null path=\"{filePath}\"");
                return null;
            }

            var warmedAt = TryWarmInitialFrame(source, filePath, "MediaFoundation");
            FastFileSourceLog.Write($"Video accepted backend=MediaFoundation duration={source.Duration} path=\"{filePath}\"");
            return new TimingVideoFileSource(source, devices, filePath, "MediaFoundation", fromCache: false, cachedLastUpdateTime: warmedAt, cachedUpdateCount: 0, cachedMaxUpdateMs: 0, recreate: null);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Video MediaFoundation create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
    }

    private static TimeSpan? TryWarmInitialFrame(IVideoFileSource source, string filePath, string backend)
    {
        if (!FastFileSourceSettingsStore.Current.EnableVideoInitialFrameWarmup)
            return null;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            source.Update(TimeSpan.Zero);
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Video initial frame warmup backend={backend} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
            return TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Video initial frame warmup failed backend={backend}: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
    }
}

internal sealed class TimingVideoFileSource(
    IVideoFileSource inner,
    IGraphicsDevicesAndContext devices,
    string filePath,
    string backend,
    bool fromCache,
    TimeSpan? cachedLastUpdateTime,
    int cachedUpdateCount,
    double cachedMaxUpdateMs,
    Func<IVideoFileSource>? recreate) : IVideoFileSource
{
    private IVideoFileSource inner = inner;
    private int updateCount;
    private int slowUpdateCount;
    private int backwardSeekCount;
    private int largeJumpCount;
    private double totalUpdateMs;
    private double maxUpdateMs;
    private readonly TimeSpan? cachedSourceLastUpdateTime = cachedLastUpdateTime;
    private readonly int cachedSourceUpdateCount = cachedUpdateCount;
    private readonly double cachedSourceMaxUpdateMs = cachedMaxUpdateMs;
    private TimeSpan? lastUpdateTime = cachedLastUpdateTime;
    private bool disposed;

    public TimeSpan Duration => inner.Duration;

    public ID2D1Image Output => inner.Output;

    public int GetFrameIndex(TimeSpan time)
    {
        return inner.GetFrameIndex(time);
    }

    public void Update(TimeSpan time)
    {
        if (fromCache && updateCount == 0 && cachedSourceLastUpdateTime.HasValue)
        {
            var firstJumpSeconds = Math.Abs((time - cachedSourceLastUpdateTime.Value).TotalSeconds);
            var maxJumpSeconds = FastFileSourceSettingsStore.Current.VideoSourceCacheMaxFirstSeekJumpSeconds;
            if (firstJumpSeconds > maxJumpSeconds && recreate is not null)
            {
                FastFileSourceLog.Write(
                    $"Video source cache bypass backend={backend} reason=far-first-seek jumpSec={firstJumpSeconds:F3} limitSec={maxJumpSeconds:F3} time={time} cachedLast={cachedSourceLastUpdateTime.Value} path=\"{filePath}\"");
                inner.Dispose();
                using var _ = FastFileSourceLog.Measure($"Video {backend} recreate after cache bypass");
                inner = recreate();
            }
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var frameIndex = inner.GetFrameIndex(time);
        var previousTime = lastUpdateTime;
        inner.Update(time);
        updateCount++;
        lastUpdateTime = time;

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        totalUpdateMs += elapsedMs;
        maxUpdateMs = Math.Max(maxUpdateMs, elapsedMs);
        var deltaMs = previousTime.HasValue ? (time - previousTime.Value).TotalMilliseconds : 0.0;
        if (previousTime.HasValue && deltaMs < -0.5)
            backwardSeekCount++;
        if (previousTime.HasValue && Math.Abs(deltaMs) > 100.0)
            largeJumpCount++;
        VideoBackendAdaptivePreference.RecordUpdate(filePath, backend, elapsedMs, deltaMs, fromCache);

        if (elapsedMs >= 3.0)
        {
            slowUpdateCount++;
            FastFileSourceLog.Write($"Video Update slow backend={backend} cached={fromCache} count={updateCount} slow={slowUpdateCount} frame={frameIndex} time={time} deltaMs={deltaMs:F3} elapsed={elapsedMs:F3} ms jumps={largeJumpCount} backward={backwardSeekCount} path=\"{filePath}\"");
        }
        else if (updateCount == 1)
        {
            FastFileSourceLog.Write($"Video first Update backend={backend} cached={fromCache} frame={frameIndex} time={time} elapsed={elapsedMs:F3} ms duration={Duration} path=\"{filePath}\"");
        }
        else if (updateCount % 500 == 0)
        {
            FastFileSourceLog.Write($"Video Update stats backend={backend} cached={fromCache} count={updateCount} avg={totalUpdateMs / updateCount:F3} ms max={maxUpdateMs:F3} ms slow={slowUpdateCount} jumps={largeJumpCount} backward={backwardSeekCount} path=\"{filePath}\"");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        var avg = updateCount > 0 ? totalUpdateMs / updateCount : 0;
        FastFileSourceLog.Write($"Video dispose backend={backend} cached={fromCache} updates={updateCount} avg={avg:F3} ms max={maxUpdateMs:F3} ms slow={slowUpdateCount} jumps={largeJumpCount} backward={backwardSeekCount} path=\"{filePath}\"");
        if (fromCache && updateCount == 0)
        {
            inner.Dispose();
            return;
        }

        if (!VideoSourceCache.TryReturn(devices, filePath, backend, inner, updateCount, maxUpdateMs, lastUpdateTime))
            inner.Dispose();
    }
}

internal static class VideoBackendAdaptivePreference
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);

    public static bool ShouldPreferMediaFoundation(string filePath, FastFileSourceSettings settings, out string reason)
    {
        reason = "none";
        if (!settings.EnableAdaptiveVideoBackend)
        {
            reason = "disabled";
            return false;
        }

        if (!IsEligible(filePath, settings, out var key, out var sizeMB))
        {
            reason = $"ineligible sizeMB={sizeMB:F1}";
            return false;
        }

        lock (Gate)
        {
            if (!States.TryGetValue(key, out var state))
                return false;

            var now = DateTime.UtcNow;
            if (state.SuppressMediaFoundationUntilUtc > now)
            {
                reason = $"suppressed until={state.SuppressMediaFoundationUntilUtc:O}";
                return false;
            }

            if (state.PreferMediaFoundationUntilUtc > now)
            {
                reason = $"slow-ffmpeg-random-access ffmpegSlowJumps={state.FFmpegSlowLargeJumpCount} until={state.PreferMediaFoundationUntilUtc:O}";
                return true;
            }

            if (state.PreferMediaFoundationUntilUtc != default)
            {
                state.PreferMediaFoundationUntilUtc = default;
                reason = "expired";
            }

            return false;
        }
    }

    public static void RecordUpdate(string filePath, string backend, double elapsedMs, double deltaMs, bool fromCache)
    {
        if (fromCache)
            return;

        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableAdaptiveVideoBackend)
            return;

        if (Math.Abs(deltaMs) < settings.AdaptiveVideoLargeJumpMs || elapsedMs < settings.AdaptiveVideoSlowUpdateMs)
            return;

        if (!IsEligible(filePath, settings, out var key, out var sizeMB))
            return;

        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (States.Count > 200)
                TrimStatesLocked(now);

            if (!States.TryGetValue(key, out var state))
            {
                state = new State();
                States.Add(key, state);
            }

            if (backend.Equals("FFmpeg", StringComparison.OrdinalIgnoreCase))
            {
                state.FFmpegSlowLargeJumpCount++;
                if (state.FFmpegSlowLargeJumpCount >= settings.AdaptiveVideoSlowJumpCount &&
                    state.PreferMediaFoundationUntilUtc <= now &&
                    state.SuppressMediaFoundationUntilUtc <= now)
                {
                    state.PreferMediaFoundationUntilUtc = now.AddSeconds(settings.AdaptiveVideoPreferenceSeconds);
                    FastFileSourceLog.Write(
                        $"Video adaptive backend prefer=MediaFoundation reason=ffmpeg-slow-random-access slowJumps={state.FFmpegSlowLargeJumpCount} elapsed={elapsedMs:F3} ms deltaMs={deltaMs:F3} sizeMB={sizeMB:F1} seconds={settings.AdaptiveVideoPreferenceSeconds} path=\"{filePath}\"");
                }
                return;
            }

            if (backend.Equals("MediaFoundation", StringComparison.OrdinalIgnoreCase))
            {
                state.MediaFoundationSlowLargeJumpCount++;
                if (state.MediaFoundationSlowLargeJumpCount >= settings.AdaptiveVideoSlowJumpCount)
                {
                    state.SuppressMediaFoundationUntilUtc = now.AddSeconds(settings.AdaptiveVideoPreferenceSeconds);
                    state.PreferMediaFoundationUntilUtc = default;
                    FastFileSourceLog.Write(
                        $"Video adaptive backend suppress=MediaFoundation reason=mf-slow-random-access slowJumps={state.MediaFoundationSlowLargeJumpCount} elapsed={elapsedMs:F3} ms deltaMs={deltaMs:F3} sizeMB={sizeMB:F1} seconds={settings.AdaptiveVideoPreferenceSeconds} path=\"{filePath}\"");
                }
            }
        }
    }

    private static void TrimStatesLocked(DateTime now)
    {
        var toRemove = States
            .Where(pair =>
                pair.Value.PreferMediaFoundationUntilUtc < now &&
                pair.Value.SuppressMediaFoundationUntilUtc < now)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var k in toRemove)
            States.Remove(k);
    }

    private static bool IsEligible(string filePath, FastFileSourceSettings settings, out string key, out double sizeMB)
    {
        key = string.Empty;
        sizeMB = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var info = new FileInfo(filePath);
            if (!info.Exists)
                return false;

            key = Path.GetFullPath(filePath);
            sizeMB = info.Length / 1024.0 / 1024.0;
            return sizeMB >= settings.AdaptiveVideoMinFileMB;
        }
        catch
        {
            return false;
        }
    }

    private sealed class State
    {
        public int FFmpegSlowLargeJumpCount { get; set; }

        public int MediaFoundationSlowLargeJumpCount { get; set; }

        public DateTime PreferMediaFoundationUntilUtc { get; set; }

        public DateTime SuppressMediaFoundationUntilUtc { get; set; }
    }
}
