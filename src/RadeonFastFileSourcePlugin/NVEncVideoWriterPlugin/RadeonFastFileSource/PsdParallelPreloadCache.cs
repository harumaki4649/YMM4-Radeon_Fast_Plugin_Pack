using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace RadeonFastFileSourcePlugin;

internal static class PsdParallelPreloadCache
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object GateLock = new();
    private static SemaphoreSlim? gate;
    private static int gateSize;
    private static long queuedCount;
    private static long readyCount;
    private static long hitCount;
    private static long missCount;
    private static long failCount;
    private static long skipCount;

    public static void QueueFromStateKey(string? stateKey, string? keyHash, string reason)
    {
        if (string.IsNullOrWhiteSpace(stateKey))
            return;

        var parts = stateKey.Split('|');
        if (parts.Length < 2)
            return;

        QueueFile(parts[0], parts[1], keyHash, reason);
    }

    public static void QueueFile(string? filePath, string? fileStamp, string? keyHash, string reason)
    {
        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
            if (!settings.EnableExperimentalPsdParallelPreload || settings.PsdParallelPreloadMaxEntries <= 0)
                return;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath) ||
            !Path.GetExtension(filePath).Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch
        {
            return;
        }

        if (!File.Exists(fullPath))
            return;

        var stamp = string.IsNullOrWhiteSpace(fileStamp) ? GetFileStamp(fullPath) : fileStamp;
        var fileKey = $"{fullPath}|{stamp}";

        if (!Entries.ContainsKey(fileKey) && Entries.Count >= settings.PsdParallelPreloadMaxEntries)
        {
            var skipped = Interlocked.Increment(ref skipCount);
            if (skipped <= 16 || skipped % 100 == 0)
                FastFileSourceLog.Write($"PSD parallel preload skipped reason=max-entries entries={Entries.Count} max={settings.PsdParallelPreloadMaxEntries} path=\"{fullPath}\"");
            return;
        }

        var entry = Entries.GetOrAdd(fileKey, key =>
        {
            var queued = Interlocked.Increment(ref queuedCount);
            FastFileSourceLog.Write($"PSD parallel preload queued reason={reason} queued={queued} entries={Entries.Count + 1} hash={keyHash} path=\"{fullPath}\"");
            return new Entry(Task.Run(() => BuildSnapshot(settings, key, fullPath, keyHash ?? "")));
        });

        if (entry.Task.IsFaulted)
            Entries.TryRemove(fileKey, out _);
    }

    public static bool TryGetSnapshot(string fileKey, string? keyHash, out PreloadedPsdSnapshot snapshot)
    {
        snapshot = default;
        if (!IsEnabled())
            return false;

        if (!Entries.TryGetValue(fileKey, out var entry))
        {
            CountMiss("not-queued", fileKey, keyHash);
            return false;
        }

        if (!entry.Task.IsCompletedSuccessfully)
        {
            var waitMs = GetWaitMs();
            if (waitMs > 0 && !entry.Task.IsCompleted)
            {
                var waitStart = Stopwatch.GetTimestamp();
                try
                {
                    entry.Task.Wait(waitMs);
                }
                catch
                {
                    // The result path below records the failed preload without surfacing the background exception.
                }

                var waitedMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
                if (entry.Task.IsCompletedSuccessfully)
                {
                    FastFileSourceLog.Write($"PSD parallel preload wait-hit hash={keyHash} waited={waitedMs:F3} ms fileKeyHash={CreateShortHash(fileKey)}");
                }
                else
                {
                    CountMiss($"not-ready-wait:{waitedMs:F0}ms", fileKey, keyHash);
                    return false;
                }
            }
            else
            {
                CountMiss("not-ready", fileKey, keyHash);
                return false;
            }
        }

        var result = entry.Task.Result;
        if (result is null)
        {
            CountMiss("failed", fileKey, keyHash);
            return false;
        }

        snapshot = result.Value;
        var hits = Interlocked.Increment(ref hitCount);
        if (hits <= 32 || hits % 100 == 0)
            FastFileSourceLog.Write($"PSD parallel preload hit hash={keyHash} fileKeyHash={snapshot.FileKeyHash} hits={hits} ready={Volatile.Read(ref readyCount)} path=\"{snapshot.FilePath}\"");
        return true;
    }

    private static PreloadedPsdSnapshot? BuildSnapshot(FastFileSourceSettings settings, string fileKey, string filePath, string keyHash)
    {
        var semaphore = GetGate(settings);
        semaphore.Wait();
        var start = Stopwatch.GetTimestamp();
        try
        {
            var psdFileType = ResolveType("PsdParser.PsdFile, PsdParser");
            var psdSettingsType = ResolveType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdFileSettings, YukkuriMovieMaker.Plugin.Tachie.Psd");
            var psdFolderType = ResolveType("YukkuriMovieMaker.Plugin.FileSource.Psd.PsdFolder, YukkuriMovieMaker.Plugin.FileSource.Psd");
            if (psdFileType is null || psdSettingsType is null || psdFolderType is null)
                throw new InvalidOperationException($"PSD preload types missing psdFile={psdFileType is not null} settings={psdSettingsType is not null} folder={psdFolderType is not null}");

            var psdFile = Activator.CreateInstance(psdFileType, filePath)
                ?? throw new InvalidOperationException("PsdFile create returned null");

            var loadSettings = psdSettingsType.GetMethod("LoadFromPsdFilePath", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(psdSettingsType.FullName, "LoadFromPsdFilePath");
            var psdSettings = loadSettings.Invoke(null, new object[] { filePath });

            var parse = psdFolderType.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(psdFolderType.FullName, "Parse");
            var root = parse.Invoke(null, new[] { psdFile })
                ?? throw new InvalidOperationException("PsdFolder.Parse returned null");

            var defaultLayers = CreateDefaultLayers(root);
            var predecode = PredecodeLayerBuffers(settings, root);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var ready = Interlocked.Increment(ref readyCount);
            var snapshot = new PreloadedPsdSnapshot(
                fileKey,
                CreateShortHash(fileKey),
                keyHash,
                filePath,
                psdFile,
                psdSettings,
                root,
                defaultLayers);
            FastFileSourceLog.Write(
                $"PSD parallel preload ready hash={keyHash} fileKeyHash={snapshot.FileKeyHash} ready={ready} queued={Volatile.Read(ref queuedCount)} elapsed={elapsedMs:F3} ms layers={defaultLayers.Count} predecodeLayers={predecode.Layers} predecodeBytes={predecode.Bytes} predecodeMs={predecode.ElapsedMs:F3} path=\"{filePath}\"");
            return snapshot;
        }
        catch (Exception ex)
        {
            var failed = Interlocked.Increment(ref failCount);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write($"PSD parallel preload failed fails={failed} elapsed={elapsedMs:F3} ms path=\"{filePath}\": {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static int GetWaitMs()
    {
        try
        {
            return FastFileSourceSettingsStore.Current.PsdParallelPreloadWaitMs;
        }
        catch
        {
            return 0;
        }
    }

    private static ImmutableList<string> CreateDefaultLayers(object root)
    {
        try
        {
            var method = root.GetType().GetMethod("GetEnableItems", BindingFlags.Instance | BindingFlags.Public);
            var result = method?.Invoke(root, null);
            if (result is System.Collections.IEnumerable enumerable)
            {
                var list = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item is not null)
                        list.Add(item.ToString() ?? "");
                }

                return list.Where(x => !string.IsNullOrWhiteSpace(x)).ToImmutableList();
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD parallel preload default layer read failed: {ex.GetType().Name}: {ex.Message}");
        }

        return ImmutableList<string>.Empty;
    }

    private static PredecodeStats PredecodeLayerBuffers(FastFileSourceSettings settings, object root)
    {
        if (!settings.EnableExperimentalPsdLayerPredecode || settings.PsdLayerPredecodeMaxLayers <= 0)
            return default;

        var start = Stopwatch.GetTimestamp();
        var layers = 0;
        var bytes = 0L;
        var visited = new HashSet<int>();

        try
        {
            foreach (var item in EnumeratePsdItems(root, visited))
            {
                if (layers >= settings.PsdLayerPredecodeMaxLayers)
                    break;

                var imageBuffer = GetProperty(item, "LayerImageBuffer");
                if (imageBuffer is null)
                    continue;

                var imageBytes = InvokeToBytes(imageBuffer);
                if (imageBytes > 0)
                {
                    layers++;
                    bytes += imageBytes;
                }

                var maskBuffer = GetProperty(item, "LayerMaskBuffer");
                if (maskBuffer is not null)
                    bytes += InvokeToBytes(maskBuffer);
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD layer predecode failed layers={layers} bytes={bytes}: {ex.GetType().Name}: {ex.Message}");
        }

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (layers > 0 || elapsedMs >= 5)
            FastFileSourceLog.Write($"PSD layer predecode done layers={layers} bytes={bytes} elapsed={elapsedMs:F3} ms maxLayers={settings.PsdLayerPredecodeMaxLayers}");

        return new PredecodeStats(layers, bytes, elapsedMs);
    }

    private static IEnumerable<object> EnumeratePsdItems(object root, HashSet<int> visited)
    {
        var key = RuntimeHelpers.GetHashCode(root);
        if (!visited.Add(key))
            yield break;

        yield return root;

        var items = GetProperty(root, "Items") as System.Collections.IEnumerable;
        if (items is null)
            yield break;

        foreach (var item in items)
        {
            if (item is null)
                continue;

            foreach (var child in EnumeratePsdItems(item, visited))
                yield return child;
        }
    }

    private static object? GetProperty(object target, string name)
    {
        try
        {
            return target.GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static long InvokeToBytes(object buffer)
    {
        try
        {
            var method = buffer.GetType().GetMethod("ToBytes", BindingFlags.Instance | BindingFlags.Public);
            if (method?.Invoke(buffer, null) is byte[] bytes)
                return bytes.LongLength;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"PSD layer predecode ToBytes failed buffer={buffer.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    private static SemaphoreSlim GetGate(FastFileSourceSettings settings)
    {
        var size = Math.Clamp(settings.PsdParallelPreloadMaxConcurrent, 1, 16);
        lock (GateLock)
        {
            if (gate is not null && gateSize == size)
                return gate;

            gate = new SemaphoreSlim(size, size);
            gateSize = size;
            return gate;
        }
    }

    private static bool IsEnabled()
    {
        try
        {
            return FastFileSourceSettingsStore.Current.EnableExperimentalPsdParallelPreload;
        }
        catch
        {
            return false;
        }
    }

    private static void CountMiss(string reason, string fileKey, string? keyHash)
    {
        var misses = Interlocked.Increment(ref missCount);
        if (misses <= 16 || misses % 100 == 0)
            FastFileSourceLog.Write($"PSD parallel preload miss reason={reason} hash={keyHash} misses={misses} fileKeyHash={CreateShortHash(fileKey)}");
    }

    private static Type? ResolveType(string assemblyQualifiedName)
    {
        return Type.GetType(assemblyQualifiedName, throwOnError: false) ??
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(assemblyQualifiedName.Split(',')[0].Trim(), throwOnError: false))
                .FirstOrDefault(type => type is not null);
    }

    private static string GetFileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "";
        }
    }

    private static string CreateShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    private sealed record Entry(Task<PreloadedPsdSnapshot?> Task);

    private readonly record struct PredecodeStats(int Layers, long Bytes, double ElapsedMs);

    public readonly record struct PreloadedPsdSnapshot(
        string FileKey,
        string FileKeyHash,
        string KeyHash,
        string FilePath,
        object PsdFile,
        object? PsdFileSettings,
        object PsdRoot,
        object? DefaultEnableLayers);
}
