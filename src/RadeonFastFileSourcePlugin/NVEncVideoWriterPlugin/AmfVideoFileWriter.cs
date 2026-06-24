using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    private PropertyInfo? _resolvedChannelsProp;
    private bool _channelsPropResolved;
    private long _lastVideoStartTick;
    private long _profileWindowStartTick;
    private long _profileFrames;
    private double _profileIntervalMs;
    private double _profileTextureMs;
    private double _profileNativeMs;
    private double _profileTotalMs;

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

        if (_videoInfo.HasErrors || _videoInfo.Width <= 0 || _videoInfo.Height <= 0)
        {
            return;
        }

        var videoStartTick = Stopwatch.GetTimestamp();
        var intervalMs = 0.0;
        if (_lastVideoStartTick != 0)
        {
            intervalMs = ElapsedMs(_lastVideoStartTick, videoStartTick);
        }
        _lastVideoStartTick = videoStartTick;

        var textureStartTick = Stopwatch.GetTimestamp();
        using var surface = frame.Surface;
        using var texture = surface.QueryInterface<ID3D11Texture2D>();
        var textureMs = ElapsedMs(textureStartTick, Stopwatch.GetTimestamp());
        if (texture is null)
        {
            throw new InvalidOperationException("D3D11 テクスチャを取得できませんでした。");
        }

        double nativeMs;
        lock (_encodeLock)
        {
            if (_encoderHandle == IntPtr.Zero)
            {
                InitializeEncoder(texture);
            }

            var nativeStartTick = Stopwatch.GetTimestamp();
            var result = AmfNativeMethods.AmfEncode(_encoderHandle, texture.NativePointer);
            nativeMs = ElapsedMs(nativeStartTick, Stopwatch.GetTimestamp());
            if (result == 0)
            {
                ThrowFatalError();
            }
        }
        AddManagedProfile(intervalMs, textureMs, nativeMs, ElapsedMs(videoStartTick, Stopwatch.GetTimestamp()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

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

    private void WriteAudioInternal(float[] samples)
    {
        var sampleRate = Math.Max(8000, _videoInfo.Hz);
        var channels = ResolveAudioChannels();
        var result = AmfNativeMethods.AmfWriteAudio(_encoderHandle, samples, samples.Length, sampleRate, channels);
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

    private void AddManagedProfile(double intervalMs, double textureMs, double nativeMs, double totalMs)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        lock (_encodeLock)
        {
            if (_profileWindowStartTick == 0)
            {
                _profileWindowStartTick = Stopwatch.GetTimestamp();
            }

            _profileFrames++;
            _profileIntervalMs += intervalMs;
            _profileTextureMs += textureMs;
            _profileNativeMs += nativeMs;
            _profileTotalMs += totalMs;

            if (_profileFrames < 120)
            {
                return;
            }

            var wallMs = ElapsedMs(_profileWindowStartTick, Stopwatch.GetTimestamp());
            var frames = Math.Max(1, _profileFrames);
            var fps = wallMs > 0 ? frames * 1000.0 / wallMs : 0.0;
            LogManagedLine(
                "managed profile frames=" + frames.ToString(CultureInfo.InvariantCulture)
                + " fps=" + fps.ToString("F3", CultureInfo.InvariantCulture)
                + " avgFrameIntervalMs=" + (_profileIntervalMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " textureQueryMs=" + (_profileTextureMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " nativeEncodeMs=" + (_profileNativeMs / frames).ToString("F3", CultureInfo.InvariantCulture)
                + " writeVideoTotalMs=" + (_profileTotalMs / frames).ToString("F3", CultureInfo.InvariantCulture));

            _profileWindowStartTick = 0;
            _profileFrames = 0;
            _profileIntervalMs = 0;
            _profileTextureMs = 0;
            _profileNativeMs = 0;
            _profileTotalMs = 0;
        }
    }

    private void LogManagedLine(string message)
    {
        try
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [managed] " + message + Environment.NewLine;
            File.AppendAllText(_outputPath + ".amf_log.txt", line, Encoding.UTF8);
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
}
