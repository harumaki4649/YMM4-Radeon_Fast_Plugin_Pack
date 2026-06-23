using System.Text.Json;

namespace RadeonFastFileSourcePlugin;

internal sealed class FastFileSourceSettings
{
    public bool EnableDetailedLog { get; set; } = false;

    public bool EnableThreadPoolBoost { get; set; } = true;

    public int ThreadPoolMinWorkerThreads { get; set; } = 24;

    public bool EnableAudioPcmCache { get; set; } = true;

    public bool EnableAudioBackgroundPreload { get; set; } = false;

    public bool EnableNativeAudioDecoder { get; set; } = true;

    public bool EnableNativeTempAudioDecoder { get; set; } = false;

    public int AudioCacheMaxMemoryMB { get; set; } = 2048;

    public int AudioCacheMaxSingleFileMB { get; set; } = 256;

    public double AudioCacheMaxDurationSeconds { get; set; } = 600;

    public bool CacheTempAudio { get; set; } = false;

    public bool CacheMp3Audio { get; set; } = true;

    public bool CacheMediaAudio { get; set; } = false;

    public int AudioCacheMinOpenCount { get; set; } = 2;

    public int AudioCacheReadChunkSamples { get; set; } = 65536;

    public int AudioCacheMaxConcurrentDecodes { get; set; } = 1;

    public bool EnableImageBitmapCache { get; set; } = true;

    public bool EnableNativeImageDecoder { get; set; } = true;

    public bool EnableImageCpuDecodeCache { get; set; } = true;

    public int ImageCacheMaxMemoryMB { get; set; } = 4096;

    public int ImageCacheMaxSingleFileMB { get; set; } = 256;

    public int ImageCpuDecodeCacheMaxMemoryMB { get; set; } = 4096;

    public int ImageCpuDecodeCacheMaxSingleFileMB { get; set; } = 1024;

    public bool PreferMediaFoundationVideo { get; set; } = false;

    public bool EnableAdaptiveVideoBackend { get; set; } = true;

    public int AdaptiveVideoMinFileMB { get; set; } = 16;

    public double AdaptiveVideoLargeJumpMs { get; set; } = 250.0;

    public double AdaptiveVideoSlowUpdateMs { get; set; } = 20.0;

    public int AdaptiveVideoSlowJumpCount { get; set; } = 8;

    public int AdaptiveVideoPreferenceSeconds { get; set; } = 600;

    public bool EnableVideoSourceCache { get; set; } = false;

    public int VideoSourceCacheMaxEntries { get; set; } = 32;

    public int VideoSourceCacheMaxProbeEntries { get; set; } = 3;

    public int VideoSourceCacheTtlSeconds { get; set; } = 180;

    public int VideoSourceCacheMinUpdatesToKeep { get; set; } = 0;

    public double VideoSourceCacheMinSlowUpdateToKeepMs { get; set; } = 20.0;

    public int VideoSourceCachePreferLargeFileMB { get; set; } = 32;

    public double VideoSourceCacheMaxFirstSeekJumpSeconds { get; set; } = 2.0;

    public int VideoSourceCacheMinReuseAgeMs { get; set; } = 0;

    public bool VideoSourceCacheUseDeviceContextKey { get; set; } = true;

    public int VideoSourceCacheWaitForWarmupMs { get; set; } = 0;

    public bool EnableProjectWarmup { get; set; } = false;

    public bool EnableAudioWarmup { get; set; } = true;

    public bool EnableImageWarmup { get; set; } = false;

    public bool EnableVideoFileWarmup { get; set; } = false;

    public bool EnableImageDecodeWarmup { get; set; } = true;

    public int ImageDecodeWarmupMaxConcurrent { get; set; } = 1;

    public bool EnableVideoDecodeWarmup { get; set; } = false;

    public int VideoDecodeWarmupMaxConcurrent { get; set; } = 1;

    public int VideoDecodeWarmupFrames { get; set; } = 2;

    public bool EnableVideoInitialFrameWarmup { get; set; } = false;

    public int VideoDecodeWarmupMaxQueuedPerCall { get; set; } = 8;

    public int WarmupMaxFiles { get; set; } = 96;

    public int WarmupMaxConcurrentTasks { get; set; } = 6;

    public int WarmupMaxImageFileMB { get; set; } = 512;

    public int WarmupMaxVideoFileMB { get; set; } = 1024;

    public int WarmupReadBufferMB { get; set; } = 4;

    public bool EnableTimelineToolWarmup { get; set; } = false;

    public bool EnableAutoProjectWarmup { get; set; } = true;

    public bool EnableInternalApiProbe { get; set; } = false;

    public bool EnableInjectionProfiler { get; set; } = false;

    public bool EnableInjectionArgumentLog { get; set; } = false;

    public double InjectionSlowThresholdMs { get; set; } = 8.0;

    public double InjectionRenderSlowThresholdMs { get; set; } = 25.0;

    public int InjectionSummaryInterval { get; set; } = 1000;

    public bool EnableSlowArgumentPropertyLog { get; set; } = false;

    public double SlowArgumentPropertyLogThresholdMs { get; set; } = 35.0;

    public int SlowArgumentPropertyMaxPerType { get; set; } = 6;

    public bool EnableSlowArgumentPathWarmup { get; set; } = false;

    public bool EnableRenderScenePathWarmup { get; set; } = false;

    public int RenderScenePathWarmupMaxPaths { get; set; } = 512;

    public int RenderScenePathWarmupMaxDepth { get; set; } = 8;

    public int RenderScenePathWarmupMaxCollectionItems { get; set; } = 2048;

    public bool EnablePsdStateCache { get; set; } = true;

    public int PsdStateCacheLogInterval { get; set; } = 500;

    public bool EnablePsdStateManifest { get; set; } = true;

    public bool EnablePsdManifestFileWarmup { get; set; } = true;

    public int PsdManifestMaxStates { get; set; } = 512;

    public int PsdManifestCandidateLogCount { get; set; } = 12;

    public double PsdManifestCandidateMinPrepareMs { get; set; } = 20.0;

    public bool EnablePsdInternalApiProbe { get; set; } = false;

    public int PsdInternalApiProbeMaxTypes { get; set; } = 40;

    public bool EnableExperimentalPsdFieldCache { get; set; } = false;

    public double PsdFieldCacheReplaceMinPrepareMs { get; set; } = 50.0;

    public bool EnableExperimentalPsdParallelPreload { get; set; } = false;

    public int PsdParallelPreloadMaxEntries { get; set; } = 32;

    public int PsdParallelPreloadMaxConcurrent { get; set; } = 3;

    public int PsdParallelPreloadWaitMs { get; set; } = 0;

    public bool EnableExperimentalPsdLayerPredecode { get; set; } = false;

    public int PsdLayerPredecodeMaxLayers { get; set; } = 128;

    public bool EnableExperimentalParallelInjection { get; set; } = false;
}

internal static class FastFileSourceSettingsStore
{
    private static readonly object Gate = new();
    private static FastFileSourceSettings? cached;
    private static DateTime lastLoadUtc;
    private static string? lastLoggedSignature;

    private static string SettingsPath => Path.Combine(
        AppContext.BaseDirectory,
        "user",
        "RadeonFastFileSourcePlugin",
        "settings.json");

    public static FastFileSourceSettings Current
    {
        get
        {
            lock (Gate)
            {
                if (cached is not null && DateTime.UtcNow - lastLoadUtc < TimeSpan.FromSeconds(5))
                    return cached;

                cached = Load();
                lastLoadUtc = DateTime.UtcNow;
                return cached;
            }
        }
    }

    private static FastFileSourceSettings Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(SettingsPath))
            {
                var defaults = new FastFileSourceSettings();
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(defaults, JsonOptions()));
                FastFileSourceLog.Write($"Settings created path=\"{SettingsPath}\"");
                return defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<FastFileSourceSettings>(json, JsonOptions()) ?? new FastFileSourceSettings();
            settings.ThreadPoolMinWorkerThreads = Math.Clamp(settings.ThreadPoolMinWorkerThreads, 1, 256);
            settings.AudioCacheMaxMemoryMB = Math.Clamp(settings.AudioCacheMaxMemoryMB, 64, 32768);
            settings.AudioCacheMaxSingleFileMB = Math.Clamp(settings.AudioCacheMaxSingleFileMB, 8, 8192);
            settings.AudioCacheMaxDurationSeconds = Math.Clamp(settings.AudioCacheMaxDurationSeconds, 1, 3600);
            settings.AudioCacheMinOpenCount = Math.Clamp(settings.AudioCacheMinOpenCount, 1, 16);
            settings.AudioCacheReadChunkSamples = Math.Clamp(settings.AudioCacheReadChunkSamples, 4096, 1048576);
            settings.AudioCacheMaxConcurrentDecodes = Math.Clamp(settings.AudioCacheMaxConcurrentDecodes, 1, 32);
            settings.ImageCacheMaxMemoryMB = Math.Clamp(settings.ImageCacheMaxMemoryMB, 64, 32768);
            settings.ImageCacheMaxSingleFileMB = Math.Clamp(settings.ImageCacheMaxSingleFileMB, 1, 2048);
            settings.ImageCpuDecodeCacheMaxMemoryMB = Math.Clamp(settings.ImageCpuDecodeCacheMaxMemoryMB, 64, 32768);
            settings.ImageCpuDecodeCacheMaxSingleFileMB = Math.Clamp(settings.ImageCpuDecodeCacheMaxSingleFileMB, 1, 8192);
            settings.AdaptiveVideoMinFileMB = Math.Clamp(settings.AdaptiveVideoMinFileMB, 0, 8192);
            settings.AdaptiveVideoLargeJumpMs = Math.Clamp(settings.AdaptiveVideoLargeJumpMs, 0, 60000);
            settings.AdaptiveVideoSlowUpdateMs = Math.Clamp(settings.AdaptiveVideoSlowUpdateMs, 0, 10000);
            settings.AdaptiveVideoSlowJumpCount = Math.Clamp(settings.AdaptiveVideoSlowJumpCount, 1, 1000);
            settings.AdaptiveVideoPreferenceSeconds = Math.Clamp(settings.AdaptiveVideoPreferenceSeconds, 1, 86400);
            settings.VideoSourceCacheMaxEntries = Math.Clamp(settings.VideoSourceCacheMaxEntries, 0, 128);
            settings.VideoSourceCacheMaxProbeEntries = Math.Clamp(settings.VideoSourceCacheMaxProbeEntries, 0, 32);
            settings.VideoSourceCacheTtlSeconds = Math.Clamp(settings.VideoSourceCacheTtlSeconds, 1, 3600);
            settings.VideoSourceCacheMinUpdatesToKeep = Math.Clamp(settings.VideoSourceCacheMinUpdatesToKeep, 0, 1000);
            settings.VideoSourceCacheMinSlowUpdateToKeepMs = Math.Clamp(settings.VideoSourceCacheMinSlowUpdateToKeepMs, 0, 10000);
            settings.VideoSourceCachePreferLargeFileMB = Math.Clamp(settings.VideoSourceCachePreferLargeFileMB, 1, 8192);
            settings.VideoSourceCacheMaxFirstSeekJumpSeconds = Math.Clamp(settings.VideoSourceCacheMaxFirstSeekJumpSeconds, 0, 3600);
            settings.VideoSourceCacheMinReuseAgeMs = Math.Clamp(settings.VideoSourceCacheMinReuseAgeMs, 0, 30000);
            settings.VideoSourceCacheWaitForWarmupMs = Math.Clamp(settings.VideoSourceCacheWaitForWarmupMs, 0, 1000);
            settings.WarmupMaxFiles = Math.Clamp(settings.WarmupMaxFiles, 0, 4096);
            settings.WarmupMaxConcurrentTasks = Math.Clamp(settings.WarmupMaxConcurrentTasks, 1, 32);
            settings.ImageDecodeWarmupMaxConcurrent = Math.Clamp(settings.ImageDecodeWarmupMaxConcurrent, 1, 4);
            settings.VideoDecodeWarmupMaxConcurrent = Math.Clamp(settings.VideoDecodeWarmupMaxConcurrent, 1, 4);
            settings.VideoDecodeWarmupFrames = Math.Clamp(settings.VideoDecodeWarmupFrames, 1, 8);
            settings.VideoDecodeWarmupMaxQueuedPerCall = Math.Clamp(settings.VideoDecodeWarmupMaxQueuedPerCall, 1, 128);
            settings.WarmupMaxImageFileMB = Math.Clamp(settings.WarmupMaxImageFileMB, 1, 8192);
            settings.WarmupMaxVideoFileMB = Math.Clamp(settings.WarmupMaxVideoFileMB, 1, 16384);
            settings.WarmupReadBufferMB = Math.Clamp(settings.WarmupReadBufferMB, 1, 64);
            settings.InjectionSlowThresholdMs = Math.Clamp(settings.InjectionSlowThresholdMs, 0, 10000);
            settings.InjectionRenderSlowThresholdMs = Math.Clamp(settings.InjectionRenderSlowThresholdMs, 0, 10000);
            settings.InjectionSummaryInterval = Math.Clamp(settings.InjectionSummaryInterval, 0, 100000);
            settings.SlowArgumentPropertyLogThresholdMs = Math.Clamp(settings.SlowArgumentPropertyLogThresholdMs, 0, 10000);
            settings.SlowArgumentPropertyMaxPerType = Math.Clamp(settings.SlowArgumentPropertyMaxPerType, 0, 1000);
            settings.RenderScenePathWarmupMaxPaths = Math.Clamp(settings.RenderScenePathWarmupMaxPaths, 0, 8192);
            settings.RenderScenePathWarmupMaxDepth = Math.Clamp(settings.RenderScenePathWarmupMaxDepth, 1, 16);
            settings.RenderScenePathWarmupMaxCollectionItems = Math.Clamp(settings.RenderScenePathWarmupMaxCollectionItems, 1, 65536);
            settings.PsdStateCacheLogInterval = Math.Clamp(settings.PsdStateCacheLogInterval, 0, 100000);
            settings.PsdManifestMaxStates = Math.Clamp(settings.PsdManifestMaxStates, 16, 8192);
            settings.PsdManifestCandidateLogCount = Math.Clamp(settings.PsdManifestCandidateLogCount, 0, 256);
            settings.PsdManifestCandidateMinPrepareMs = Math.Clamp(settings.PsdManifestCandidateMinPrepareMs, 0, 10000);
            settings.PsdInternalApiProbeMaxTypes = Math.Clamp(settings.PsdInternalApiProbeMaxTypes, 0, 512);
            settings.PsdFieldCacheReplaceMinPrepareMs = Math.Clamp(settings.PsdFieldCacheReplaceMinPrepareMs, 0, 10000);
            settings.PsdParallelPreloadMaxEntries = Math.Clamp(settings.PsdParallelPreloadMaxEntries, 0, 512);
            settings.PsdParallelPreloadMaxConcurrent = Math.Clamp(settings.PsdParallelPreloadMaxConcurrent, 1, 16);
            settings.PsdParallelPreloadWaitMs = Math.Clamp(settings.PsdParallelPreloadWaitMs, 0, 1000);
            settings.PsdLayerPredecodeMaxLayers = Math.Clamp(settings.PsdLayerPredecodeMaxLayers, 0, 1024);
            LogSettings(settings);
            return settings;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Settings load failed: {ex.GetType().Name}: {ex.Message}");
            return new FastFileSourceSettings();
        }
    }

    private static void LogSettings(FastFileSourceSettings settings)
    {
        var signature = string.Join(
            "|",
            settings.EnableDetailedLog,
            settings.EnableThreadPoolBoost,
            settings.ThreadPoolMinWorkerThreads,
            settings.EnableAudioPcmCache,
            settings.EnableAudioBackgroundPreload,
            settings.EnableNativeAudioDecoder,
            settings.EnableNativeTempAudioDecoder,
            settings.CacheTempAudio,
            settings.CacheMp3Audio,
            settings.CacheMediaAudio,
            settings.AudioCacheMaxMemoryMB,
            settings.AudioCacheMaxSingleFileMB,
            settings.AudioCacheMaxDurationSeconds,
            settings.AudioCacheMinOpenCount,
            settings.AudioCacheReadChunkSamples,
            settings.AudioCacheMaxConcurrentDecodes,
            settings.EnableImageBitmapCache,
            settings.EnableNativeImageDecoder,
            settings.EnableImageCpuDecodeCache,
            settings.ImageCacheMaxMemoryMB,
            settings.ImageCacheMaxSingleFileMB,
            settings.ImageCpuDecodeCacheMaxMemoryMB,
            settings.ImageCpuDecodeCacheMaxSingleFileMB,
            settings.PreferMediaFoundationVideo,
            settings.EnableAdaptiveVideoBackend,
            settings.AdaptiveVideoMinFileMB,
            settings.AdaptiveVideoLargeJumpMs,
            settings.AdaptiveVideoSlowUpdateMs,
            settings.AdaptiveVideoSlowJumpCount,
            settings.AdaptiveVideoPreferenceSeconds,
            settings.EnableVideoSourceCache,
            settings.VideoSourceCacheMaxEntries,
            settings.VideoSourceCacheMaxProbeEntries,
            settings.VideoSourceCacheTtlSeconds,
            settings.VideoSourceCacheMinUpdatesToKeep,
            settings.VideoSourceCacheMinSlowUpdateToKeepMs,
            settings.VideoSourceCachePreferLargeFileMB,
            settings.VideoSourceCacheMaxFirstSeekJumpSeconds,
            settings.VideoSourceCacheMinReuseAgeMs,
            settings.VideoSourceCacheUseDeviceContextKey,
            settings.VideoSourceCacheWaitForWarmupMs,
            settings.EnableProjectWarmup,
            settings.EnableAudioWarmup,
            settings.EnableImageWarmup,
            settings.EnableVideoFileWarmup,
            settings.EnableImageDecodeWarmup,
            settings.ImageDecodeWarmupMaxConcurrent,
            settings.EnableVideoDecodeWarmup,
            settings.VideoDecodeWarmupMaxConcurrent,
            settings.VideoDecodeWarmupFrames,
            settings.EnableVideoInitialFrameWarmup,
            settings.VideoDecodeWarmupMaxQueuedPerCall,
            settings.WarmupMaxFiles,
            settings.WarmupMaxConcurrentTasks,
            settings.WarmupMaxImageFileMB,
            settings.WarmupMaxVideoFileMB,
            settings.WarmupReadBufferMB,
            settings.EnableTimelineToolWarmup,
            settings.EnableAutoProjectWarmup,
            settings.EnableInternalApiProbe,
            settings.EnableInjectionProfiler,
            settings.EnableInjectionArgumentLog,
            settings.InjectionSlowThresholdMs,
            settings.InjectionRenderSlowThresholdMs,
            settings.InjectionSummaryInterval,
            settings.EnableSlowArgumentPropertyLog,
            settings.SlowArgumentPropertyLogThresholdMs,
            settings.SlowArgumentPropertyMaxPerType,
            settings.EnableSlowArgumentPathWarmup,
            settings.EnableRenderScenePathWarmup,
            settings.RenderScenePathWarmupMaxPaths,
            settings.RenderScenePathWarmupMaxDepth,
            settings.RenderScenePathWarmupMaxCollectionItems,
            settings.EnablePsdStateCache,
            settings.PsdStateCacheLogInterval,
            settings.EnablePsdStateManifest,
            settings.EnablePsdManifestFileWarmup,
            settings.PsdManifestMaxStates,
            settings.PsdManifestCandidateLogCount,
            settings.PsdManifestCandidateMinPrepareMs,
            settings.EnablePsdInternalApiProbe,
            settings.PsdInternalApiProbeMaxTypes,
            settings.EnableExperimentalPsdFieldCache,
            settings.PsdFieldCacheReplaceMinPrepareMs,
            settings.EnableExperimentalPsdParallelPreload,
            settings.PsdParallelPreloadMaxEntries,
            settings.PsdParallelPreloadMaxConcurrent,
            settings.PsdParallelPreloadWaitMs,
            settings.EnableExperimentalPsdLayerPredecode,
            settings.PsdLayerPredecodeMaxLayers,
            settings.EnableExperimentalParallelInjection);

        lock (Gate)
        {
            if (signature == lastLoggedSignature)
                return;

            lastLoggedSignature = signature;
        }

        FastFileSourceLog.Write(
            "Settings loaded " +
            $"detailed={settings.EnableDetailedLog} " +
            $"threadPoolBoost={settings.EnableThreadPoolBoost} minWorkerThreads={settings.ThreadPoolMinWorkerThreads} " +
            $"audioCache={settings.EnableAudioPcmCache} audioPreload={settings.EnableAudioBackgroundPreload} nativeAudio={settings.EnableNativeAudioDecoder} nativeTempAudio={settings.EnableNativeTempAudioDecoder} " +
            $"cacheTemp={settings.CacheTempAudio} cacheMp3={settings.CacheMp3Audio} cacheMedia={settings.CacheMediaAudio} " +
            $"audioMemMB={settings.AudioCacheMaxMemoryMB} audioSingleMB={settings.AudioCacheMaxSingleFileMB} audioMaxSeconds={settings.AudioCacheMaxDurationSeconds:F0} " +
            $"audioMinOpen={settings.AudioCacheMinOpenCount} audioChunkSamples={settings.AudioCacheReadChunkSamples} audioDecoders={settings.AudioCacheMaxConcurrentDecodes} " +
            $"imageCache={settings.EnableImageBitmapCache} nativeImage={settings.EnableNativeImageDecoder} imageCpuCache={settings.EnableImageCpuDecodeCache} imageMemMB={settings.ImageCacheMaxMemoryMB} imageSingleMB={settings.ImageCacheMaxSingleFileMB} imageCpuMemMB={settings.ImageCpuDecodeCacheMaxMemoryMB} imageCpuSingleMB={settings.ImageCpuDecodeCacheMaxSingleFileMB} " +
            $"preferMFVideo={settings.PreferMediaFoundationVideo} adaptiveVideo={settings.EnableAdaptiveVideoBackend} adaptiveVideoMinMB={settings.AdaptiveVideoMinFileMB} adaptiveVideoJumpMs={settings.AdaptiveVideoLargeJumpMs:F1} adaptiveVideoSlowMs={settings.AdaptiveVideoSlowUpdateMs:F1} adaptiveVideoSlowJumps={settings.AdaptiveVideoSlowJumpCount} adaptiveVideoPreferenceSec={settings.AdaptiveVideoPreferenceSeconds} videoSourceCache={settings.EnableVideoSourceCache} videoEntries={settings.VideoSourceCacheMaxEntries} videoProbeEntries={settings.VideoSourceCacheMaxProbeEntries} " +
            $"videoTtlSec={settings.VideoSourceCacheTtlSeconds} videoMinUpdates={settings.VideoSourceCacheMinUpdatesToKeep} videoSlowKeepMs={settings.VideoSourceCacheMinSlowUpdateToKeepMs:F1} videoLargeMB={settings.VideoSourceCachePreferLargeFileMB} " +
            $"videoMaxFirstSeekJumpSec={settings.VideoSourceCacheMaxFirstSeekJumpSeconds:F1} videoMinReuseAgeMs={settings.VideoSourceCacheMinReuseAgeMs} videoDeviceKey={settings.VideoSourceCacheUseDeviceContextKey} videoWaitWarmupMs={settings.VideoSourceCacheWaitForWarmupMs} " +
            $"warmup={settings.EnableProjectWarmup} warmAudio={settings.EnableAudioWarmup} warmImage={settings.EnableImageWarmup} warmVideoFile={settings.EnableVideoFileWarmup} imageDecodeWarmup={settings.EnableImageDecodeWarmup} imageDecodeTasks={settings.ImageDecodeWarmupMaxConcurrent} videoDecodeWarmup={settings.EnableVideoDecodeWarmup} videoDecodeTasks={settings.VideoDecodeWarmupMaxConcurrent} videoDecodeFrames={settings.VideoDecodeWarmupFrames} videoInitialFrameWarmup={settings.EnableVideoInitialFrameWarmup} videoDecodeQueuedPerCall={settings.VideoDecodeWarmupMaxQueuedPerCall} warmFiles={settings.WarmupMaxFiles} warmTasks={settings.WarmupMaxConcurrentTasks} warmImageMB={settings.WarmupMaxImageFileMB} warmVideoMB={settings.WarmupMaxVideoFileMB} warmBufferMB={settings.WarmupReadBufferMB} " +
            $"timelineToolWarmup={settings.EnableTimelineToolWarmup} autoProjectWarmup={settings.EnableAutoProjectWarmup} internalProbe={settings.EnableInternalApiProbe} " +
            $"injectionProfiler={settings.EnableInjectionProfiler} injectionArgs={settings.EnableInjectionArgumentLog} injectionSlowMs={settings.InjectionSlowThresholdMs:F1} injectionRenderSlowMs={settings.InjectionRenderSlowThresholdMs:F1} injectionSummaryInterval={settings.InjectionSummaryInterval} " +
            $"slowArgLog={settings.EnableSlowArgumentPropertyLog} slowArgThresholdMs={settings.SlowArgumentPropertyLogThresholdMs:F1} slowArgMaxPerType={settings.SlowArgumentPropertyMaxPerType} slowArgWarmup={settings.EnableSlowArgumentPathWarmup} " +
            $"renderSceneWarmup={settings.EnableRenderScenePathWarmup} renderScenePaths={settings.RenderScenePathWarmupMaxPaths} renderSceneDepth={settings.RenderScenePathWarmupMaxDepth} renderSceneCollectionItems={settings.RenderScenePathWarmupMaxCollectionItems} " +
            $"psdStateCache={settings.EnablePsdStateCache} psdStateLogInterval={settings.PsdStateCacheLogInterval} psdManifest={settings.EnablePsdStateManifest} psdManifestFileWarmup={settings.EnablePsdManifestFileWarmup} psdManifestStates={settings.PsdManifestMaxStates} psdManifestCandidates={settings.PsdManifestCandidateLogCount} psdManifestMinPrepareMs={settings.PsdManifestCandidateMinPrepareMs:F1} psdProbe={settings.EnablePsdInternalApiProbe} psdProbeTypes={settings.PsdInternalApiProbeMaxTypes} psdFieldCache={settings.EnableExperimentalPsdFieldCache} psdFieldReplaceMs={settings.PsdFieldCacheReplaceMinPrepareMs:F1} psdParallelPreload={settings.EnableExperimentalPsdParallelPreload} psdParallelEntries={settings.PsdParallelPreloadMaxEntries} psdParallelTasks={settings.PsdParallelPreloadMaxConcurrent} psdParallelWaitMs={settings.PsdParallelPreloadWaitMs} psdLayerPredecode={settings.EnableExperimentalPsdLayerPredecode} psdLayerPredecodeMax={settings.PsdLayerPredecodeMaxLayers} " +
            $"experimentalParallelInjection={settings.EnableExperimentalParallelInjection}");
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
        };
    }
}
