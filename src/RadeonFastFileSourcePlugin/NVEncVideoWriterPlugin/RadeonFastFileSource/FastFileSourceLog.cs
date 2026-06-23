using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RadeonFastFileSourcePlugin;

internal static class FastFileSourceLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory,
        "user",
        "log",
        "radeon_fast_filesource_log.txt");

    [ModuleInitializer]
    public static void ModuleInit()
    {
        Write($"Plugin module loaded assembly={Assembly.GetExecutingAssembly().Location} baseDir={AppContext.BaseDirectory}");
        ThreadPoolTuner.TryApply();
        RadeonFastAudioFileSourcePlugin.EnsureManifestAudioPcmWarmup();
        WarmupManager.EnsureManifestImageCpuWarmup();
        PsdStateManifest.LogCandidatesAndQueueWarmup("module-load");
        InternalInjectionProfiler.TryInitialize();
        PsdInternalApiProbe.RunOnce("module-load");
    }

    public static void Write(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            lock (Gate)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break YMM4 export.
        }
    }

    public static void WriteDetailed(string message)
    {
        try
        {
            if (!FastFileSourceSettingsStore.Current.EnableDetailedLog)
                return;
        }
        catch
        {
            return;
        }

        Write(message);
    }

    public static IDisposable Measure(string scope)
    {
        return new ScopeTimer(scope);
    }

    private sealed class ScopeTimer(string scope) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsedMs >= 1.0)
                Write($"{scope} {elapsedMs:F3} ms");
        }
    }
}
