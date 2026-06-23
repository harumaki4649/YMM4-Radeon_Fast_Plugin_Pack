using System;
using System.Runtime.InteropServices;

namespace RadeonAmfVideoWriterPlugin;

internal static class AmfNativeMethods
{
    [DllImport("AmfNative.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AmfCreate(
        IntPtr device,
        IntPtr firstTexture,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        int codec,
        int quality,
        int rateControlMode,
        int maxBitrateKbps,
        int queueDepth,
        int enablePreAnalysis,
        int enableDebugLog,
        string outputPath);

    [DllImport("AmfNative.dll")]
    public static extern int AmfEncode(IntPtr handle, IntPtr texture);

    [DllImport("AmfNative.dll")]
    public static extern int AmfWriteAudio(IntPtr handle, float[] samples, int sampleCount, int sampleRate, int channels);

    [DllImport("AmfNative.dll")]
    public static extern int AmfFinalize(IntPtr handle);

    [DllImport("AmfNative.dll")]
    public static extern void AmfDestroy(IntPtr handle);

    [DllImport("AmfNative.dll")]
    public static extern IntPtr AmfGetLastError(IntPtr handle);
}
