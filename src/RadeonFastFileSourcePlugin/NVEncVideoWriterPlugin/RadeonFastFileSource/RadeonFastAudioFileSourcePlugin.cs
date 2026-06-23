using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.MediaFoundation;
using YukkuriMovieMaker.Plugin.FileSource.Mp3;

namespace RadeonFastFileSourcePlugin;

public sealed class RadeonFastAudioFileSourcePlugin : IAudioFileSourcePlugin
{
    private static readonly HashSet<string> Mp3Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
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

    public string Name => "Radeon 高速音声読み込み";

    public RadeonFastAudioFileSourcePlugin()
    {
        FastFileSourceLog.Write("Audio plugin constructed");
    }

    internal static void EnsureManifestAudioPcmWarmup()
    {
        WarmupManager.EnsureAudioWarmup(QueueWarmupPreload);
    }

    public IAudioFileSource CreateAudioFileSource(string filePath, int audioTrackIndex)
    {
        var extension = Path.GetExtension(filePath);
        var fileSize = TryGetFileSize(filePath);
        FastFileSourceLog.Write($"Audio create begin track={audioTrackIndex} ext=\"{extension}\" bytes={fileSize} path=\"{filePath}\"");
        EnsureManifestAudioPcmWarmup();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            FastFileSourceLog.Write("Audio no-track empty path");
            return new NoAudioFileSource(string.Empty, "empty-path");
        }

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio source file was not found.", filePath);

        if (ImageExtensions.Contains(extension))
        {
            FastFileSourceLog.Write($"Audio no-track image ext=\"{extension}\" path=\"{filePath}\"");
            return new NoAudioFileSource(filePath, "image");
        }

        WarmupManager.Record("audio", filePath, audioTrackIndex);

        var native = NativeAudioFileSourceFactory.TryCreate(filePath, audioTrackIndex);
        if (native is not null)
            return native;

        if (Mp3Extensions.Contains(extension))
        {
            try
            {
                return CreateMeasured("MP3", filePath, () => new Mp3FileSource(filePath), audioTrackIndex);
            }
            catch (Exception ex)
            {
                FastFileSourceLog.Write($"Audio MP3 create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            }
        }

        try
        {
            return CreateMeasured("FFmpeg", filePath, () => new FFmpegAudioFileSource(filePath, audioTrackIndex), audioTrackIndex);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Audio FFmpeg create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
        }

        try
        {
            return CreateMeasured("MediaFoundation", filePath, () => new MFAudioFileSource(filePath, audioTrackIndex), audioTrackIndex);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Audio MediaFoundation create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return new NoAudioFileSource(filePath, "backend-failed");
        }
    }

    private static void QueueWarmupPreload(string filePath, int audioTrackIndex)
    {
        var extension = Path.GetExtension(filePath);
        if (Mp3Extensions.Contains(extension))
        {
            AudioPcmCache.QueuePreload("MP3", filePath, audioTrackIndex, () => new Mp3FileSource(filePath), "manifest");
            return;
        }

        AudioPcmCache.QueuePreload("FFmpeg", filePath, audioTrackIndex, () => new FFmpegAudioFileSource(filePath, audioTrackIndex), "manifest");
    }

    private static IAudioFileSource CreateMeasured(string backend, string filePath, Func<IAudioFileSource> create, int audioTrackIndex)
    {
        var cached = AudioPcmCache.TryCreate(backend, filePath, audioTrackIndex, create);
        if (cached is not null)
            return cached;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var source = create();
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return CreateValidated(source, filePath, backend, elapsedMs);
    }

    private static IAudioFileSource CreateValidated(IAudioFileSource source, string filePath, string backend, double createElapsedMs)
    {
        if (source.Hz <= 0)
        {
            source.Dispose();
            FastFileSourceLog.Write($"Audio rejected invalid Hz backend={backend} hz={source.Hz} path=\"{filePath}\"");
            throw new InvalidOperationException($"Audio source has invalid sample rate: {filePath}");
        }

        FastFileSourceLog.Write($"Audio accepted backend={backend} create={createElapsedMs:F3} ms hz={source.Hz} duration={source.Duration} path=\"{filePath}\"");
        return new TimingAudioFileSource(source, filePath, backend, createElapsedMs);
    }

    private static long TryGetFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return -1;
        }
    }
}

internal sealed class NoAudioFileSource(string filePath, string reason) : IAudioFileSource
{
    private int readCount;
    private int seekCount;

    public TimeSpan Duration => TimeSpan.Zero;

    public int Hz => 48000;

    public int Read(float[] destBuffer, int offset, int count)
    {
        readCount++;
        if (readCount == 1)
            FastFileSourceLog.Write($"Audio no-track first Read reason={reason} samples={count} path=\"{filePath}\"");

        return 0;
    }

    public void Seek(TimeSpan time)
    {
        seekCount++;
        if (seekCount == 1)
            FastFileSourceLog.Write($"Audio no-track first Seek reason={reason} time={time} path=\"{filePath}\"");
    }

    public void Dispose()
    {
        FastFileSourceLog.Write($"Audio no-track dispose reason={reason} reads={readCount} seeks={seekCount} path=\"{filePath}\"");
    }
}

internal sealed class TimingAudioFileSource(IAudioFileSource inner, string filePath, string backend, double createElapsedMs) : IAudioFileSource
{
    private int readCount;
    private int seekCount;
    private int slowReadCount;
    private int slowSeekCount;
    private double totalReadMs;
    private double maxReadMs;
    private double totalSeekMs;
    private double maxSeekMs;

    public TimeSpan Duration => inner.Duration;

    public int Hz => inner.Hz;

    public int Read(float[] destBuffer, int offset, int count)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var read = inner.Read(destBuffer, offset, count);
        readCount++;

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        totalReadMs += elapsedMs;
        maxReadMs = Math.Max(maxReadMs, elapsedMs);

        if (elapsedMs >= 2.0)
        {
            slowReadCount++;
            FastFileSourceLog.Write($"Audio Read slow backend={backend} count={readCount} slow={slowReadCount} samples={count} read={read} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
        }
        else if (readCount == 1)
        {
            FastFileSourceLog.Write($"Audio first Read backend={backend} samples={count} read={read} elapsed={elapsedMs:F3} ms hz={Hz} duration={Duration} path=\"{filePath}\"");
        }
        else if (readCount % 500 == 0)
        {
            FastFileSourceLog.Write($"Audio Read stats backend={backend} count={readCount} avg={totalReadMs / readCount:F3} ms max={maxReadMs:F3} ms slow={slowReadCount} path=\"{filePath}\"");
        }

        return read;
    }

    public void Seek(TimeSpan time)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        inner.Seek(time);
        seekCount++;

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        totalSeekMs += elapsedMs;
        maxSeekMs = Math.Max(maxSeekMs, elapsedMs);

        if (elapsedMs >= 2.0)
        {
            slowSeekCount++;
            FastFileSourceLog.Write($"Audio Seek slow backend={backend} count={seekCount} slow={slowSeekCount} time={time} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
        }
        else if (seekCount == 1)
        {
            FastFileSourceLog.Write($"Audio first Seek backend={backend} time={time} elapsed={elapsedMs:F3} ms path=\"{filePath}\"");
        }
    }

    public void Dispose()
    {
        var avgReadMs = readCount == 0 ? 0 : totalReadMs / readCount;
        var avgSeekMs = seekCount == 0 ? 0 : totalSeekMs / seekCount;
        FastFileSourceLog.Write(
            $"Audio dispose backend={backend} create={createElapsedMs:F3} ms reads={readCount} avgRead={avgReadMs:F3} ms maxRead={maxReadMs:F3} ms slowReads={slowReadCount} seeks={seekCount} avgSeek={avgSeekMs:F3} ms maxSeek={maxSeekMs:F3} ms slowSeeks={slowSeekCount} path=\"{filePath}\"");
        inner.Dispose();
    }
}
