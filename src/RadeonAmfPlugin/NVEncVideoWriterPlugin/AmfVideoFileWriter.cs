using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace RadeonAmfVideoWriterPlugin;

internal sealed class AmfVideoFileWriter : IVideoFileWriter2, IDisposable
{
    private readonly string _outputPath;
    private readonly VideoInfo _videoInfo;
    private readonly AmfSettings _settings;
    private IntPtr _encoderHandle = IntPtr.Zero;
    private volatile bool _disposed;
    private volatile bool _hasFatalError;
    private readonly object _encodeLock = new();
    private readonly object _profileLock = new();
    private readonly object _d3dLock = new();
    private BlockingCollection<QueuedVideoFrame>? _videoQueue;
    private Task? _videoWorker;
    private volatile Exception? _workerException;
    private PropertyInfo? _resolvedChannelsProp;
    private bool _channelsPropResolved;
    private long _lastVideoStartTick;
    private long _videoFrameIndex;
    private int _lastGc0Count;
    private int _lastGc1Count;
    private int _lastGc2Count;
    private long _profileWindowStartTick;
    private long _profileFrames;
    private double _profileIntervalMs;
    private double _profileTextureMs;
    private double _profileCopyMs;
    private double _profileEnqueueWaitMs;
    private double _profileNativeMs;
    private double _profileTotalMs;
    private double _profileMaxIntervalMs;
    private double _profileMaxTextureMs;
    private double _profileMaxCopyMs;
    private double _profileMaxEnqueueWaitMs;
    private double _profileMaxNativeMs;
    private double _profileMaxTotalMs;
    private int _profileMaxQueueCount;
    private long _profileSlowManagedFrames;
    private long _profileAudioCalls;
    private long _profileAudioSamples;
    private double _profileAudioNativeMs;
    private double _profileMaxAudioNativeMs;

    public AmfVideoFileWriter(string outputPath, VideoInfo videoInfo, AmfSettings settings)
    {
        _outputPath = outputPath;
        _videoInfo = videoInfo;
        _settings = settings;
    }

    public VideoFileWriterSupportedStreams SupportedStreams => VideoFileWriterSupportedStreams.Audio | VideoFileWriterSupportedStreams.Video;

    private readonly List<float> _pendingAudio = new();

    public void WriteAudio(float[] samples)
    {
        EnsureNotDisposed();
        ThrowWorkerExceptionIfAny();
        if (samples == null || samples.Length == 0)
        {
            return;
        }

        lock (_encodeLock)
        {
            if (_encoderHandle == IntPtr.Zero)
            {
                _pendingAudio.AddRange(samples);
                return;
            }

            WriteAudioInternal(samples);
        }
    }

    public void WriteVideo(byte[] frame)
    {
        // Not used in IVideoFileWriter2 mode.
    }

    public void WriteVideo(ID2D1Bitmap1 frame)
    {
        EnsureNotDisposed();
        ThrowWorkerExceptionIfAny();

        if (_videoInfo.HasErrors || _videoInfo.Width <= 0 || _videoInfo.Height <= 0)
        {
            return;
        }

        var videoStartTick = Stopwatch.GetTimestamp();
        var frameIndex = ++_videoFrameIndex;
        var intervalMs = 0.0;
        if (_lastVideoStartTick != 0)
        {
            intervalMs = ElapsedMs(_lastVideoStartTick, videoStartTick);
        }
        _lastVideoStartTick = videoStartTick;
        LogManagedIntervalSpike(frameIndex, intervalMs);

        var textureStartTick = Stopwatch.GetTimestamp();
        using var surface = frame.Surface;
        using var texture = surface.QueryInterface<ID3D11Texture2D>();
        var textureMs = ElapsedMs(textureStartTick, Stopwatch.GetTimestamp());
        if (texture is null)
        {
            throw new InvalidOperationException("D3D11 テクスチャを取得できませんでした。");
        }

        var copyStartTick = Stopwatch.GetTimestamp();
        ID3D11Texture2D queuedTexture;
        lock (_encodeLock)
        {
            if (_encoderHandle == IntPtr.Zero)
            {
                InitializeEncoder(texture);
            }

            EnsureVideoWorkerStarted();
            queuedTexture = CopyTextureForQueue(texture);
        }
        var copyMs = ElapsedMs(copyStartTick, Stopwatch.GetTimestamp());

        var enqueueStartTick = Stopwatch.GetTimestamp();
        var queued = false;
        try
        {
            var queue = _videoQueue ?? throw new InvalidOperationException("Radeon AMF 非同期キューが初期化されていません。");
            while (!queued)
            {
                ThrowWorkerExceptionIfAny();
                queued = queue.TryAdd(new QueuedVideoFrame(frameIndex, queuedTexture), 100);
                if (!queued && queue.IsAddingCompleted)
                {
                    throw new ObjectDisposedException(nameof(AmfVideoFileWriter));
                }
            }
        }
        finally
        {
            if (!queued)
            {
                queuedTexture.Dispose();
            }
        }

        var enqueueWaitMs = ElapsedMs(enqueueStartTick, Stopwatch.GetTimestamp());
        AddManagedProfile(
            intervalMs,
            textureMs,
            copyMs,
            enqueueWaitMs,
            0.0,
            ElapsedMs(videoStartTick, Stopwatch.GetTimestamp()),
            _videoQueue?.Count ?? 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CompleteVideoWorker();

        lock (_encodeLock)
        {
            if (_encoderHandle != IntPtr.Zero)
            {
                if (!_hasFatalError)
                {
                    AmfNativeMethods.AmfFinalize(_encoderHandle);
                }
                AmfNativeMethods.AmfDestroy(_encoderHandle);
                _encoderHandle = IntPtr.Zero;
            }
        }

        ThrowWorkerExceptionIfAny();
    }

    private void InitializeEncoder(ID3D11Texture2D texture)
    {
        var device = texture.Device;
        if (device is null)
        {
            throw new InvalidOperationException("D3D11 デバイスを取得できませんでした。");
        }

        if ((_videoInfo.Width & 1) != 0 || (_videoInfo.Height & 1) != 0)
        {
            throw new InvalidOperationException("Radeon AMF は偶数サイズの解像度が必要です。");
        }

        var fps = Math.Max(1, _videoInfo.FPS);
        var bitrate = GetTargetBitrateKbps();
        var codec = _settings.Codec == AmfCodec.H265 ? 1 : 0;
        var quality = (int)_settings.Quality;
        var rateControl = _settings.RateControl == AmfRateControl.Variable ? 1 : 0;
        if (_settings.RateControl == AmfRateControl.YouTubeRecommended)
        {
            rateControl = 1;
        }
        var maxBitrate = rateControl == 1
            ? Math.Clamp((int)(bitrate * 1.2), 100, 300000)
            : bitrate;

        _encoderHandle = AmfNativeMethods.AmfCreate(
            device.NativePointer,
            texture.NativePointer,
            _videoInfo.Width,
            _videoInfo.Height,
            fps,
            bitrate,
            codec,
            quality,
            rateControl,
            maxBitrate,
            _settings.QueueDepth,
            _settings.EnablePreAnalysis ? 1 : 0,
            _settings.EnableDebugLog ? 1 : 0,
            _outputPath);

        if (_encoderHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Radeon AMF 初期化に失敗しました。");
        }

        var error = GetNativeError();
        if (!string.IsNullOrWhiteSpace(error))
        {
            AmfNativeMethods.AmfDestroy(_encoderHandle);
            _encoderHandle = IntPtr.Zero;
            throw new InvalidOperationException(error);
        }

        if (_pendingAudio.Count > 0)
        {
            var buffer = _pendingAudio.ToArray();
            _pendingAudio.Clear();
            WriteAudioInternal(buffer);
        }
    }

    private ID3D11Texture2D CopyTextureForQueue(ID3D11Texture2D source)
    {
        if (_encoderHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Radeon AMF エンコーダが初期化されていません。");
        }

        var poolTexPtr = AmfNativeMethods.AmfAcquireTexture(_encoderHandle);
        if (poolTexPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Radeon AMF テクスチャプールからテクスチャを取得できませんでした。");
        }

        Marshal.AddRef(poolTexPtr);
        ID3D11Texture2D? copy = null;

        try
        {
            copy = new ID3D11Texture2D(poolTexPtr);
            var device = source.Device ?? throw new InvalidOperationException("D3D11 デバイスを取得できませんでした。");
            var context = device.ImmediateContext ?? throw new InvalidOperationException("D3D11 ImmediateContext を取得できませんでした。");

            lock (_d3dLock)
            {
                context.CopyResource(copy, source);
            }

            return copy;
        }
        catch
        {
            copy?.Dispose();
            AmfNativeMethods.AmfReleaseTexture(_encoderHandle, poolTexPtr);
            throw;
        }
    }

    private void EnsureVideoWorkerStarted()
    {
        if (_videoQueue is not null)
        {
            return;
        }

        var queueDepth = Math.Clamp(_settings.QueueDepth, 2, 256);
        _videoQueue = new BlockingCollection<QueuedVideoFrame>(queueDepth);
        _videoWorker = Task.Run(ProcessVideoQueue);
        LogManagedLine("managed async queue start depth=" + queueDepth.ToString(CultureInfo.InvariantCulture));
    }

    private void ProcessVideoQueue()
    {
        try
        {
            var queue = _videoQueue;
            if (queue is null)
            {
                return;
            }

            foreach (var item in queue.GetConsumingEnumerable())
            {
                var texture = item.Texture;
                var texturePointer = texture.NativePointer;
                try
                {
                    var nativeStartTick = Stopwatch.GetTimestamp();

                    if (_encoderHandle == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Radeon AMF エンコーダが初期化されていません。");
                    }

                    var result = AmfNativeMethods.AmfEncode(_encoderHandle, texturePointer);

                    var nativeMs = ElapsedMs(nativeStartTick, Stopwatch.GetTimestamp());
                    AddManagedNativeWorkerProfile(item.FrameIndex, nativeMs, queue.Count);
                    if (result == 0)
                    {
                        throw new InvalidOperationException(GetNativeError());
                    }
                }
                finally
                {
                    if (_encoderHandle != IntPtr.Zero)
                    {
                        AmfNativeMethods.AmfReleaseTexture(_encoderHandle, texturePointer);
                    }
                    texture.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _workerException = ex;
            _hasFatalError = true;
            try
            {
                _videoQueue?.CompleteAdding();
            }
            catch
            {
                // Ignore shutdown races.
            }
        }
    }

    private void CompleteVideoWorker()
    {
        var queue = _videoQueue;
        if (queue is null)
        {
            return;
        }

        try
        {
            queue.CompleteAdding();
        }
        catch
        {
            // Already completed.
        }

        try
        {
            _videoWorker?.Wait();
        }
        catch (AggregateException ex)
        {
            _workerException ??= ex.Flatten().InnerException ?? ex;
            _hasFatalError = true;
        }
        finally
        {
            queue.Dispose();
            _videoQueue = null;
            _videoWorker = null;
        }

    }

    private void ThrowWorkerExceptionIfAny()
    {
        if (_workerException is not null)
        {
            throw new InvalidOperationException("Radeon AMF 非同期エンコードに失敗しました。", _workerException);
        }
    }

    private void WriteAudioInternal(float[] samples)
    {
        var sampleRate = Math.Max(8000, _videoInfo.Hz);
        var channels = ResolveAudioChannels();
        var nativeStartTick = Stopwatch.GetTimestamp();
        var result = AmfNativeMethods.AmfWriteAudio(_encoderHandle, samples, samples.Length, sampleRate, channels);
        var nativeMs = ElapsedMs(nativeStartTick, Stopwatch.GetTimestamp());
        AddManagedAudioProfile(samples.Length, nativeMs);
        if (result == 0)
        {
            ThrowFatalError();
        }
    }

    private void ThrowFatalError()
    {
        _hasFatalError = true;
        throw new InvalidOperationException(GetNativeError());
    }

    private void LogManagedIntervalSpike(long frameIndex, double intervalMs)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var gc0Delta = gc0 - _lastGc0Count;
        var gc1Delta = gc1 - _lastGc1Count;
        var gc2Delta = gc2 - _lastGc2Count;
        _lastGc0Count = gc0;
        _lastGc1Count = gc1;
        _lastGc2Count = gc2;

        if (intervalMs < 50.0)
        {
            return;
        }

        var memoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        LogManagedLine(
            "managed interval spike frame="
            + frameIndex.ToString(CultureInfo.InvariantCulture)
            + " intervalMs=" + intervalMs.ToString("F3", CultureInfo.InvariantCulture)
            + " gc0Delta=" + gc0Delta.ToString(CultureInfo.InvariantCulture)
            + " gc1Delta=" + gc1Delta.ToString(CultureInfo.InvariantCulture)
            + " gc2Delta=" + gc2Delta.ToString(CultureInfo.InvariantCulture)
            + " totalMemoryMB=" + memoryMb.ToString("F1", CultureInfo.InvariantCulture));
    }

    private void AddManagedProfile(
        double intervalMs,
        double textureMs,
        double copyMs,
        double enqueueWaitMs,
        double nativeMs,
        double totalMs,
        int queueCount)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        lock (_profileLock)
        {
            if (_profileWindowStartTick == 0)
            {
                _profileWindowStartTick = Stopwatch.GetTimestamp();
            }

            _profileFrames++;
            _profileIntervalMs += intervalMs;
            _profileTextureMs += textureMs;
            _profileCopyMs += copyMs;
            _profileEnqueueWaitMs += enqueueWaitMs;
            _profileNativeMs += nativeMs;
            _profileTotalMs += totalMs;
            _profileMaxIntervalMs = Math.Max(_profileMaxIntervalMs, intervalMs);
            _profileMaxTextureMs = Math.Max(_profileMaxTextureMs, textureMs);
            _profileMaxCopyMs = Math.Max(_profileMaxCopyMs, copyMs);
            _profileMaxEnqueueWaitMs = Math.Max(_profileMaxEnqueueWaitMs, enqueueWaitMs);
            _profileMaxNativeMs = Math.Max(_profileMaxNativeMs, nativeMs);
            _profileMaxTotalMs = Math.Max(_profileMaxTotalMs, totalMs);
            _profileMaxQueueCount = Math.Max(_profileMaxQueueCount, queueCount);
            if (totalMs >= 5.0 || enqueueWaitMs >= 3.0 || copyMs >= 2.0 || textureMs >= 2.0)
            {
                _profileSlowManagedFrames++;
                LogManagedLine(
                    "managed slow frame="
                    + _profileFrames.ToString(CultureInfo.InvariantCulture)
                    + " intervalMs=" + intervalMs.ToString("F3", CultureInfo.InvariantCulture)
                    + " textureQueryMs=" + textureMs.ToString("F3", CultureInfo.InvariantCulture)
                    + " copyMs=" + copyMs.ToString("F3", CultureInfo.InvariantCulture)
                    + " enqueueWaitMs=" + enqueueWaitMs.ToString("F3", CultureInfo.InvariantCulture)
                    + " totalMs=" + totalMs.ToString("F3", CultureInfo.InvariantCulture)
                    + " queueCount=" + queueCount.ToString(CultureInfo.InvariantCulture));
            }

            if (_profileFrames < 120)
            {
                return;
            }

            var wallMs = ElapsedMs(_profileWindowStartTick, Stopwatch.GetTimestamp());
            var frames = Math.Max(1, _profileFrames);
            var fps = wallMs > 0 ? frames * 1000.0 / wallMs : 0.0;
            LogManagedLine(
                "managed async profile frames=" + frames.ToString(CultureInfo.InvariantCulture)
                + " fps=" + fps.ToString("F3", CultureInfo.InvariantCulture)
                + " avgFrameIntervalMs=" + (_profileIntervalMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " textureQueryMs=" + (_profileTextureMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " copyMs=" + (_profileCopyMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " enqueueWaitMs=" + (_profileEnqueueWaitMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " workerNativeMs=" + (_profileNativeMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " writeVideoTotalMs=" + (_profileTotalMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " maxIntervalMs=" + _profileMaxIntervalMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maxTextureMs=" + _profileMaxTextureMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maxCopyMs=" + _profileMaxCopyMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maxEnqueueWaitMs=" + _profileMaxEnqueueWaitMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maxWorkerNativeMs=" + _profileMaxNativeMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maxTotalMs=" + _profileMaxTotalMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maxQueueCount=" + _profileMaxQueueCount.ToString(CultureInfo.InvariantCulture)
                + " slowFrames=" + _profileSlowManagedFrames.ToString(CultureInfo.InvariantCulture)
                + " audioCalls=" + _profileAudioCalls.ToString(CultureInfo.InvariantCulture)
                + " audioSamples=" + _profileAudioSamples.ToString(CultureInfo.InvariantCulture)
                + " audioNativeMs=" + (_profileAudioCalls == 0 ? 0 : _profileAudioNativeMs / _profileAudioCalls).ToString("F3", CultureInfo.InvariantCulture)
                + " maxAudioNativeMs=" + _profileMaxAudioNativeMs.ToString("F3", CultureInfo.InvariantCulture));

            _profileWindowStartTick = 0;
            _profileFrames = 0;
            _profileIntervalMs = 0;
            _profileTextureMs = 0;
            _profileCopyMs = 0;
            _profileEnqueueWaitMs = 0;
            _profileNativeMs = 0;
            _profileTotalMs = 0;
            _profileMaxIntervalMs = 0;
            _profileMaxTextureMs = 0;
            _profileMaxCopyMs = 0;
            _profileMaxEnqueueWaitMs = 0;
            _profileMaxNativeMs = 0;
            _profileMaxTotalMs = 0;
            _profileMaxQueueCount = 0;
            _profileSlowManagedFrames = 0;
            _profileAudioCalls = 0;
            _profileAudioSamples = 0;
            _profileAudioNativeMs = 0;
            _profileMaxAudioNativeMs = 0;
        }
    }

    private void AddManagedAudioProfile(int sampleCount, double nativeMs)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        lock (_profileLock)
        {
            _profileAudioCalls++;
            _profileAudioSamples += sampleCount;
            _profileAudioNativeMs += nativeMs;
            _profileMaxAudioNativeMs = Math.Max(_profileMaxAudioNativeMs, nativeMs);
            if (nativeMs >= 3.0)
            {
                LogManagedLine(
                    "managed slow audio samples="
                    + sampleCount.ToString(CultureInfo.InvariantCulture)
                    + " nativeMs=" + nativeMs.ToString("F3", CultureInfo.InvariantCulture));
            }
        }
    }

    private void AddManagedNativeWorkerProfile(long frameIndex, double nativeMs, int queueCount)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        lock (_profileLock)
        {
            _profileNativeMs += nativeMs;
            _profileMaxNativeMs = Math.Max(_profileMaxNativeMs, nativeMs);
            _profileMaxQueueCount = Math.Max(_profileMaxQueueCount, queueCount);
            if (nativeMs >= 3.0)
            {
                LogManagedLine(
                    "managed async slow encode frame="
                    + frameIndex.ToString(CultureInfo.InvariantCulture)
                    + " nativeMs=" + nativeMs.ToString("F3", CultureInfo.InvariantCulture)
                    + " queueCount=" + queueCount.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private void LogManagedLine(string message)
    {
        try
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [managed] " + message + Environment.NewLine;
            using var stream = new FileStream(
                _outputPath + ".amf_log.txt",
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        catch
        {
            // Debug logging must never break export.
        }
    }

    private static double ElapsedMs(long startTick, long endTick)
    {
        return (endTick - startTick) * 1000.0 / Stopwatch.Frequency;
    }

    private string GetNativeError()
    {
        if (_encoderHandle == IntPtr.Zero)
        {
            return string.Empty;
        }
        var ptr = AmfNativeMethods.AmfGetLastError(_encoderHandle);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(ptr) ?? string.Empty;
    }

    private int ResolveAudioChannels()
    {
        if (!_channelsPropResolved)
        {
            var type = _videoInfo.GetType();
            _resolvedChannelsProp = type.GetProperty("Channels")
                ?? type.GetProperty("ChannelCount")
                ?? type.GetProperty("AudioChannels")
                ?? type.GetProperty("AudioChannelCount");
            _channelsPropResolved = true;
        }

        if (_resolvedChannelsProp?.GetValue(_videoInfo) is int value && value > 0)
        {
            return value;
        }

        return 2;
    }

    private int GetTargetBitrateKbps()
    {
        if (_settings.RateControl != AmfRateControl.YouTubeRecommended)
        {
            return Math.Clamp(_settings.BitrateKbps, 100, 200000);
        }

        var height = Math.Max(1, _videoInfo.Height);
        var highFps = _videoInfo.FPS >= 48;
        return height switch
        {
            >= 2160 => (highFps ? 60 : 40) * 1000,
            >= 1440 => (highFps ? 24 : 16) * 1000,
            >= 1080 => (highFps ? 12 : 8) * 1000,
            >= 720 => highFps ? 7500 : 5000,
            _ => (highFps ? 4 : 3) * 1000,
        };
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AmfVideoFileWriter));
        }
    }

    private sealed record QueuedVideoFrame(long FrameIndex, ID3D11Texture2D Texture);
}
