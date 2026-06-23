using System.Diagnostics;

namespace RadeonFastFileSourcePlugin;

internal static class RuntimeContextDetector
{
    public static bool IsExportCallStack()
    {
        try
        {
            var stackTrace = new StackTrace(fNeedFileInfo: false);
            for (var i = 0; i < stackTrace.FrameCount; i++)
            {
                var method = stackTrace.GetFrame(i)?.GetMethod();
                var typeName = method?.DeclaringType?.FullName;
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                if (typeName.Contains("YukkuriMovieMaker.VideoFileWriter", StringComparison.Ordinal) ||
                    typeName.Contains("CreateFileAsync", StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool IsPreviewCallStack()
    {
        try
        {
            var stackTrace = new StackTrace(fNeedFileInfo: false);
            for (var i = 0; i < stackTrace.FrameCount; i++)
            {
                var method = stackTrace.GetFrame(i)?.GetMethod();
                var typeName = method?.DeclaringType?.FullName;
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                if (typeName.Contains("YukkuriMovieMaker.VideoFileWriter", StringComparison.Ordinal))
                    return false;

                if (typeName.Contains("TimelineVideoPlayer", StringComparison.Ordinal) ||
                    typeName.Contains(".Preview", StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }
}
