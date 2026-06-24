using System.Runtime.InteropServices;
using YukkuriMovieMaker.Plugin.FileSource;

namespace RadeonFastFileSourcePlugin;

internal static class NativeAudioFileSourceFactory
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".wav",
    };

    public static IAudioFileSource? TryCreate(string filePath, int audioTrackIndex)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableNativeAudioDecoder || audioTrackIndex != 0)
            return null;

        var extension = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(extension))
            return null;

        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && !settings.EnableNativeTempAudioDecoder)
            return null;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var source = new NativeAudioFileSource(filePath);
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Audio accepted backend=miniaudio create={elapsedMs:F3} ms hz={source.Hz} duration={source.Duration} path=\"{filePath}\"");
            return new TimingAudioFileSource(source, filePath, "miniaudio", elapsedMs);
        }
        catch (DllNotFoundException ex)
        {
            FastFileSourceLog.Write($"Audio miniaudio unavailable: {ex.Message}");
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            FastFileSourceLog.Write($"Audio miniaudio entry missing: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Audio miniaudio create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
    }
}

internal sealed class NativeAudioFileSource : IAudioFileSource
{
    private IntPtr handle;
    private readonly string filePath;
    private float[]? _scratch;

    public NativeAudioFileSource(string filePath)
    {
        this.filePath = filePath;
        var result = NativeMethods.Open(filePath, out handle, out var hz, out var lengthFrames);
        if (result != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException($"miniaudio open failed ({result}): {NativeMethods.GetLastError()}");

        if (hz <= 0)
        {
            Dispose();
            throw new InvalidOperationException($"miniaudio invalid sample rate: {filePath}");
        }

        Hz = hz;
        LengthFrames = lengthFrames;
        Duration = lengthFrames == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(lengthFrames / (double)hz);
    }

    public TimeSpan Duration { get; }

    public int Hz { get; }

    private ulong LengthFrames { get; }

    public int Read(float[] destBuffer, int offset, int count)
    {
        if (handle == IntPtr.Zero || count <= 0)
            return 0;

        if (offset < 0 || count < 0 || offset + count > destBuffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (offset == 0)
        {
            var result = NativeMethods.Read(handle, destBuffer, count, out var read);
            if (result != 0)
                throw new InvalidOperationException($"miniaudio read failed ({result}): {NativeMethods.GetLastError()}");
            return read;
        }

        // Reuse scratch buffer to avoid per-call heap allocation when reading into a non-zero offset.
        if (_scratch is null || _scratch.Length < count)
            _scratch = new float[count];

        var result2 = NativeMethods.Read(handle, _scratch, count, out var read2);
        if (result2 != 0)
            throw new InvalidOperationException($"miniaudio read failed ({result2}): {NativeMethods.GetLastError()}");

        if (read2 > 0)
            Array.Copy(_scratch, 0, destBuffer, offset, read2);

        return read2;
    }

    public void Seek(TimeSpan time)
    {
        if (handle == IntPtr.Zero)
            return;

        var maxFrame = LengthFrames == 0 ? double.MaxValue : LengthFrames;
        var frame = (ulong)Math.Clamp(Math.Round(time.TotalSeconds * Hz), 0, maxFrame);
        var result = NativeMethods.Seek(handle, frame);
        if (result != 0)
            throw new InvalidOperationException($"miniaudio seek failed ({result}): {NativeMethods.GetLastError()}");
    }

    public void Dispose()
    {
        var oldHandle = handle;
        handle = IntPtr.Zero;
        if (oldHandle != IntPtr.Zero)
        {
            NativeMethods.Close(oldHandle);
            FastFileSourceLog.WriteDetailed($"Audio miniaudio native closed path=\"{filePath}\"");
        }
    }

    private static partial class NativeMethods
    {
        [DllImport("RadeonFastNativeAudio.dll", EntryPoint = "rf_audio_open", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Open(string path, out IntPtr handle, out int hz, out ulong lengthFrames);

        [DllImport("RadeonFastNativeAudio.dll", EntryPoint = "rf_audio_read", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Read(IntPtr handle, [Out] float[] destination, int samples, out int samplesRead);

        [DllImport("RadeonFastNativeAudio.dll", EntryPoint = "rf_audio_seek", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Seek(IntPtr handle, ulong frame);

        [DllImport("RadeonFastNativeAudio.dll", EntryPoint = "rf_audio_close", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Close(IntPtr handle);

        [DllImport("RadeonFastNativeAudio.dll", EntryPoint = "rf_audio_last_error", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LastError();

        public static string GetLastError()
        {
            var ptr = LastError();
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ptr) ?? "";
        }
    }
}
