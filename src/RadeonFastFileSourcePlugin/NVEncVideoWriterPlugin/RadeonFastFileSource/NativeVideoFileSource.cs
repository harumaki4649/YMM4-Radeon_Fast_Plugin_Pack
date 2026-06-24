using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.MediaFoundation;

namespace RadeonFastFileSourcePlugin;

internal static class NativeVideoFileSource
{
    private static bool loggedNativeUnavailable;
    private static bool loggedManagedFallbackDisabled;

    public static IVideoFileSource? TryCreate(IGraphicsDevicesAndContext devices, string filePath)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableNativeVideoDecoder)
            return null;

        if (settings.EnableAmfVideoDecoder)
        {
            var amfSource = TryCreateAmf(devices, filePath);
            if (amfSource is not null)
                return amfSource;
        }

        if (settings.EnableFfmpegNativeVideoDecoder)
        {
            var ffmpegSource = TryCreateFfmpeg(devices, filePath);
            if (ffmpegSource is not null)
                return ffmpegSource;
        }

        if (!settings.NativeVideoDecoderFallbackToManaged)
        {
            if (!loggedManagedFallbackDisabled)
            {
                loggedManagedFallbackDisabled = true;
                FastFileSourceLog.Write("Native video decoder fallback to managed video sources is disabled");
            }

            throw new InvalidOperationException("Native video decoder is enabled but no native backend is available.");
        }

        return null;
    }

    private static IVideoFileSource? TryCreateAmf(IGraphicsDevicesAndContext devices, string filePath)
    {
        var probe = NativeVideoProbe.TryProbe(filePath, preferHardware: true);
        if (!probe.Success)
        {
            LogNativeUnavailableOnce(probe, filePath, "AMF");
            return null;
        }

        if (probe.PreferredBackend != NativeVideoBackend.AmdHardware)
        {
            FastFileSourceLog.WriteDetailed(
                $"Native AMF video skipped reason=no-d3d-hardware probe={probe.Message} hw={probe.HardwareName} path=\"{filePath}\"");
            return null;
        }

        try
        {
            using var _ = FastFileSourceLog.Measure("Video AMF/D3D hardware create");
            var source = new MFVideoFileSourcePlugin().CreateVideoFileSource(devices, filePath);
            if (source is null)
            {
                FastFileSourceLog.Write($"Video AMF/D3D hardware MediaFoundation returned null probe={probe.Message} path=\"{filePath}\"");
                return null;
            }

            FastFileSourceLog.Write(
                $"Video accepted backend=AMF-D3D fallbackLayer=MediaFoundation hw={probe.HardwareName} stream={probe.StreamIndex} duration={source.Duration} path=\"{filePath}\"");
            return new TimingVideoFileSource(source, devices, filePath, "AMF-D3D", fromCache: false, cachedLastUpdateTime: null, cachedUpdateCount: 0, cachedMaxUpdateMs: 0, recreate: null);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Video AMF/D3D create failed: {ex.GetType().Name}: {ex.Message} probe={probe.Message} path=\"{filePath}\"");
            return null;
        }
    }

    private static IVideoFileSource? TryCreateFfmpeg(IGraphicsDevicesAndContext devices, string filePath)
    {
        var probe = NativeVideoProbe.TryProbe(filePath, preferHardware: false);
        if (!probe.Success)
        {
            LogNativeUnavailableOnce(probe, filePath, "FFmpeg");
            return null;
        }

        try
        {
            using var _ = FastFileSourceLog.Measure("Video native-probed FFmpeg create");
            var source = new FFmpegVideoFileSource(devices, filePath);
            FastFileSourceLog.Write(
                $"Video accepted backend=NativeProbe-FFmpeg stream={probe.StreamIndex} hw={probe.HardwareName} duration={source.Duration} path=\"{filePath}\"");
            return new TimingVideoFileSource(source, devices, filePath, "NativeProbe-FFmpeg", fromCache: false, cachedLastUpdateTime: null, cachedUpdateCount: 0, cachedMaxUpdateMs: 0, recreate: null);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Video native-probed FFmpeg create failed: {ex.GetType().Name}: {ex.Message} probe={probe.Message} path=\"{filePath}\"");
            return null;
        }
    }

    private static void LogNativeUnavailableOnce(NativeVideoProbeResult probe, string filePath, string requestedBackend)
    {
        if (loggedNativeUnavailable)
            return;

        loggedNativeUnavailable = true;
        FastFileSourceLog.Write(
            $"Native video probe unavailable requested={requestedBackend} result={probe.Status} message=\"{probe.Message}\" ffmpegDir=\"{probe.FfmpegDirectory}\" path=\"{filePath}\"");
    }
}

internal enum NativeVideoBackend
{
    None = 0,
    AmdHardware = 1,
    Ffmpeg = 2,
}

internal readonly record struct NativeVideoProbeResult(
    bool Success,
    int Status,
    int StreamIndex,
    NativeVideoBackend PreferredBackend,
    string CodecName,
    string HardwareName,
    string Message,
    string FfmpegDirectory);

internal static class NativeVideoProbe
{
    private static readonly ConcurrentDictionary<string, NativeVideoProbeResult> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool nativeUnavailable;

    public static NativeVideoProbeResult TryProbe(string filePath, bool preferHardware)
    {
        if (nativeUnavailable)
            return new NativeVideoProbeResult(false, -1003, -1, NativeVideoBackend.None, "", "", "native video probe disabled after load failure", "");

        var cacheKey = MakeCacheKey(filePath, preferHardware);
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var ffmpegDirectory = FindFfmpegDirectory();
        try
        {
            var native = new NativeProbeResult();
            var status = NativeMethods.Probe(filePath, ffmpegDirectory, preferHardware ? 1 : 0, ref native);
            var result = new NativeVideoProbeResult(
                status == 0 && native.Status == 0,
                status == 0 ? native.Status : status,
                native.StreamIndex,
                (NativeVideoBackend)native.PreferredBackend,
                native.CodecName ?? "",
                native.HardwareName ?? "",
                native.Message ?? NativeMethods.GetLastError(),
                ffmpegDirectory ?? "");

            FastFileSourceLog.WriteDetailed(
                $"Native video probe status={result.Status} success={result.Success} backend={result.PreferredBackend} stream={result.StreamIndex} codec={result.CodecName} hw={result.HardwareName} message=\"{result.Message}\" path=\"{filePath}\"");
            CacheResult(cacheKey, result);
            return result;
        }
        catch (DllNotFoundException ex)
        {
            nativeUnavailable = true;
            return new NativeVideoProbeResult(false, -1000, -1, NativeVideoBackend.None, "", "", ex.Message, ffmpegDirectory ?? "");
        }
        catch (EntryPointNotFoundException ex)
        {
            nativeUnavailable = true;
            return new NativeVideoProbeResult(false, -1001, -1, NativeVideoBackend.None, "", "", ex.Message, ffmpegDirectory ?? "");
        }
        catch (Exception ex)
        {
            return new NativeVideoProbeResult(false, -1002, -1, NativeVideoBackend.None, "", "", $"{ex.GetType().Name}: {ex.Message}", ffmpegDirectory ?? "");
        }
    }

    private static string MakeCacheKey(string filePath, bool preferHardware)
    {
        try
        {
            var info = new FileInfo(filePath);
            return string.Join("|", Path.GetFullPath(filePath), preferHardware, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }
        catch
        {
            return $"{filePath}|{preferHardware}";
        }
    }

    private static void CacheResult(string cacheKey, NativeVideoProbeResult result)
    {
        if (Cache.Count > 512)
            Cache.Clear();

        Cache[cacheKey] = result;
    }

    private static string? FindFfmpegDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "bin", "x64", "ffmpeg"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            Path.Combine(Path.GetDirectoryName(typeof(NativeVideoProbe).Assembly.Location) ?? AppContext.BaseDirectory, "ffmpeg"),
            Path.Combine(Environment.CurrentDirectory, "Resources", "bin", "x64", "ffmpeg"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "avformat-62.dll")) &&
                File.Exists(Path.Combine(candidate, "avcodec-62.dll")) &&
                File.Exists(Path.Combine(candidate, "avutil-60.dll")))
                return candidate;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeProbeResult
    {
        public int Status;
        public int StreamIndex;
        public int HasHardwareDevice;
        public int PreferredBackend;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? CodecName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? HardwareName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Message;
    }

    private static partial class NativeMethods
    {
        [DllImport("RadeonFastNativeVideo.dll", EntryPoint = "rf_video_probe", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Probe(string path, string? ffmpegDirectory, int preferHardware, ref NativeProbeResult result);

        [DllImport("RadeonFastNativeVideo.dll", EntryPoint = "rf_video_last_error", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LastError();

        public static string GetLastError()
        {
            var ptr = LastError();
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ptr) ?? "";
        }
    }
}
