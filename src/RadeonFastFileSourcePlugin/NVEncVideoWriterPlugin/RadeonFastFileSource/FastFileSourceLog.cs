using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace RadeonFastFileSourcePlugin;

internal static class FastFileSourceLog
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory,
        "user",
        "log",
        "radeon_fast_filesource_log.txt");

    private static readonly Channel<string> LogChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    static FastFileSourceLog()
    {
        _ = Task.Run(RunLogWorker);
    }

    [ModuleInitializer]
    public static void ModuleInit()
    {
        var nativeResolverStatus = NativeDllResolver.Initialize();
        Write($"Plugin module loaded assembly={Assembly.GetExecutingAssembly().Location} baseDir={AppContext.BaseDirectory}");
        Write($"Native DLL resolver ready dirs={nativeResolverStatus}");
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
            var line = $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            LogChannel.Writer.TryWrite(line);
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

    private static async Task RunLogWorker()
    {
        var reader = LogChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                using var stream = new FileStream(
                    LogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096);
                while (reader.TryRead(out var line))
                {
                    stream.Write(Encoding.UTF8.GetBytes(line));
                }
            }
        }
        catch
        {
            // Background logging must never crash the host process.
        }
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
