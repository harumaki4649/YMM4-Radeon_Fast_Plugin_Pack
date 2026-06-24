using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace RadeonFastFileSourcePlugin;

internal static class NativeVideoFileSource
{
    private static bool loggedAmfUnavailable;
    private static bool loggedFfmpegUnavailable;
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
        if (!loggedAmfUnavailable)
        {
            loggedAmfUnavailable = true;
            FastFileSourceLog.Write(
                $"Native AMF video decoder requested backend=AMF status=not-wired fallback=next path=\"{filePath}\"");
        }

        return null;
    }

    private static IVideoFileSource? TryCreateFfmpeg(IGraphicsDevicesAndContext devices, string filePath)
    {
        if (!loggedFfmpegUnavailable)
        {
            loggedFfmpegUnavailable = true;
            FastFileSourceLog.Write(
                $"Native FFmpeg video decoder requested backend=FFmpeg status=not-wired fallback=managed path=\"{filePath}\"");
        }

        return null;
    }
}
