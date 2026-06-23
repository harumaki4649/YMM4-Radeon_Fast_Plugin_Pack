using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace RadeonFastFileSourcePlugin;

internal static class NativeImageBitmapFactory
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".bmp",
        ".heic",
        ".heif",
        ".jfif",
        ".jpeg",
        ".jpg",
        ".jxl",
        ".png",
        ".tif",
        ".tiff",
        ".webp",
    };

    public static bool IsSupported(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    public static ID2D1Bitmap? TryCreate(IGraphicsDevicesAndContext devices, string filePath)
    {
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableNativeImageDecoder || !IsSupported(filePath))
            return null;

        if (settings.EnableImageCpuDecodeCache &&
            NativeImageCpuCache.TryGetOrDecode(filePath, "create", out var decodedImage))
        {
            return TryCreateFromCpu(devices, decodedImage, filePath, "cpu-cache");
        }

        return TryCreateDirect(devices, filePath);
    }

    public static bool TryDecodeToCpu(string filePath, out DecodedNativeImage decoded)
    {
        decoded = default;
        var settings = FastFileSourceSettingsStore.Current;
        if (!settings.EnableNativeImageDecoder || !IsSupported(filePath))
            return false;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        NativeImage image = default;
        try
        {
            var result = NativeMethods.Decode(filePath, ref image);
            if (result != 0 || image.Data == IntPtr.Zero || image.Width <= 0 || image.Height <= 0 || image.Stride <= 0)
            {
                FastFileSourceLog.Write($"Image native decode failed result={result} error=\"{NativeMethods.GetLastError()}\" path=\"{filePath}\"");
                return false;
            }

            if (image.Bytes > int.MaxValue)
            {
                FastFileSourceLog.Write($"Image native decode skip reason=too-large bytes={image.Bytes} path=\"{filePath}\"");
                return false;
            }

            // Large image buffers otherwise land on the movable LOH and amplify full-GC pauses.
            // Keeping cached pixels on the pinned object heap makes repeated bitmap uploads cheaper
            // and avoids pinning movable objects during each upload.
            var pixels = GC.AllocateUninitializedArray<byte>((int)image.Bytes, pinned: true);
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            decoded = new DecodedNativeImage(image.Width, image.Height, image.Stride, pixels);
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Image native decode done elapsed={elapsedMs:F3} ms bytes={image.Bytes} size={image.Width}x{image.Height} path=\"{filePath}\"");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            FastFileSourceLog.Write($"Image native unavailable: {ex.Message}");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            FastFileSourceLog.Write($"Image native entry missing: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Image native decode failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return false;
        }
        finally
        {
            if (image.Data != IntPtr.Zero)
                NativeMethods.Free(image.Data);
        }
    }

    public static ID2D1Bitmap? TryCreateFromCpu(
        IGraphicsDevicesAndContext devices,
        DecodedNativeImage image,
        string filePath,
        string source)
    {
        if (!image.IsValid)
            return null;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        GCHandle handle = default;
        try
        {
            var props = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));

            handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
            var bitmap = devices.DeviceContext.CreateBitmap(
                new SizeI(image.Width, image.Height),
                handle.AddrOfPinnedObject(),
                image.Stride,
                props);

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Image native bitmap create source={source} elapsed={elapsedMs:F3} ms bytes={image.Bytes} size={image.Width}x{image.Height} path=\"{filePath}\"");
            return bitmap;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Image native bitmap create failed source={source}: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    private static ID2D1Bitmap? TryCreateDirect(IGraphicsDevicesAndContext devices, string filePath)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        NativeImage image = default;
        try
        {
            var result = NativeMethods.Decode(filePath, ref image);
            if (result != 0 || image.Data == IntPtr.Zero || image.Width <= 0 || image.Height <= 0 || image.Stride <= 0)
            {
                FastFileSourceLog.Write($"Image native decode failed result={result} error=\"{NativeMethods.GetLastError()}\" path=\"{filePath}\"");
                return null;
            }

            var props = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));

            var bitmap = devices.DeviceContext.CreateBitmap(
                new SizeI(image.Width, image.Height),
                image.Data,
                image.Stride,
                props);

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"Image native create done elapsed={elapsedMs:F3} ms bytes={image.Bytes} size={image.Width}x{image.Height} path=\"{filePath}\"");
            return bitmap;
        }
        catch (DllNotFoundException ex)
        {
            FastFileSourceLog.Write($"Image native unavailable: {ex.Message}");
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            FastFileSourceLog.Write($"Image native entry missing: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Image native create failed: {ex.GetType().Name}: {ex.Message} path=\"{filePath}\"");
            return null;
        }
        finally
        {
            if (image.Data != IntPtr.Zero)
                NativeMethods.Free(image.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage
    {
        public int Width;
        public int Height;
        public int Stride;
        public ulong Bytes;
        public IntPtr Data;
    }

    private static partial class NativeMethods
    {
        [DllImport("RadeonFastNativeImage.dll", EntryPoint = "rf_image_decode_bgra", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Decode(string path, ref NativeImage image);

        [DllImport("RadeonFastNativeImage.dll", EntryPoint = "rf_image_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Free(IntPtr data);

        [DllImport("RadeonFastNativeImage.dll", EntryPoint = "rf_image_last_error", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LastError();

        public static string GetLastError()
        {
            var ptr = LastError();
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ptr) ?? "";
        }
    }
}

internal readonly record struct DecodedNativeImage(int Width, int Height, int Stride, byte[] Pixels)
{
    public long Bytes => Pixels?.LongLength ?? 0;

    public bool IsValid => Width > 0 && Height > 0 && Stride > 0 && Pixels is { Length: > 0 };
}
