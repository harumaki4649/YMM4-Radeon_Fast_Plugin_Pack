using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.FileSource.WIC;

namespace RadeonFastFileSourcePlugin;

public sealed class RadeonFastImageFileSourcePlugin : IImageFileSourcePlugin
{
    private readonly WICImageFileSourcePlugin wic = new();

    public RadeonFastImageFileSourcePlugin()
    {
        FastFileSourceLog.Write("Image plugin constructed");
    }

    public string Name => "Radeon 高速画像読み込み";

    public ID2D1Bitmap CreateBitmap(IGraphicsDevicesAndContext devices, string filePath)
    {
        var isExport = RuntimeContextDetector.IsExportCallStack();
        WarmupManager.Record("image", filePath, queueWarmup: !isExport);
        WarmupManager.EnsureImageWarmup(
            devices,
            path => CreateBitmapCore(devices, path),
            allowBackgroundWarmup: !isExport);
        return ImageBitmapCache.CreateOrGet(devices, filePath, () => CreateBitmapCore(devices, filePath));
    }

    private ID2D1Bitmap CreateBitmapCore(IGraphicsDevicesAndContext devices, string filePath)
    {
        var fileSize = TryGetFileSize(filePath);
        FastFileSourceLog.Write($"Image create begin ext=\"{Path.GetExtension(filePath)}\" bytes={fileSize} path=\"{filePath}\"");

        var native = NativeImageBitmapFactory.TryCreate(devices, filePath);
        if (native is not null)
            return native;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var bitmap = wic.CreateBitmap(devices, filePath);
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        FastFileSourceLog.Write(
            $"Image create done elapsed={elapsedMs:F3} ms size={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} path=\"{filePath}\"");

        return bitmap;
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
