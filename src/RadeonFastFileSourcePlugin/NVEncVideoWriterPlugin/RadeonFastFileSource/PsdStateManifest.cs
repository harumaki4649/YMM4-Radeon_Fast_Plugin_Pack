using System.Text.Json;

namespace RadeonFastFileSourcePlugin;

internal static class PsdStateManifest
{
    private const int ManifestVersion = 1;
    private static readonly object Gate = new();
    private static PsdManifest? cached;
    private static DateTime lastSaveUtc;
    private static int dirtyCount;
    private static long exportOrder;

    private static string ManifestPath => Path.Combine(
        AppContext.BaseDirectory,
        "user",
        "RadeonFastFileSourcePlugin",
        "psd_state_manifest.json");

    public static ManifestSnapshot Snapshot
    {
        get
        {
            try
            {
                lock (Gate)
                {
                    var manifest = LoadLocked();
                    var slow = manifest.States.Count(x => x.MaxPrepareMs >= FastFileSourceSettingsStore.Current.PsdManifestCandidateMinPrepareMs);
                    return new ManifestSnapshot(manifest.States.Count, slow, manifest.States.Sum(x => x.SeenCount), ManifestPath);
                }
            }
            catch
            {
                return new ManifestSnapshot(0, 0, 0, ManifestPath);
            }
        }
    }

    public static bool IsKnown(string stateKey)
    {
        try
        {
            if (!FastFileSourceSettingsStore.Current.EnablePsdStateManifest)
                return false;

            lock (Gate)
            {
                return FindLocked(LoadLocked(), stateKey) is not null;
            }
        }
        catch
        {
            return false;
        }
    }

    public static void RecordExportSeen(string stateKey, string keyHash, int sourceKey)
    {
        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
            if (!settings.EnablePsdStateManifest)
                return;
        }
        catch
        {
            return;
        }

        try
        {
            string? filePathToWarm = null;
            lock (Gate)
            {
                var manifest = LoadLocked();
                var entry = GetOrAddLocked(manifest, stateKey, keyHash);
                var firstSeen = entry.SeenCount == 0;
                var order = Interlocked.Increment(ref exportOrder);
                entry.SeenCount++;
                entry.SourceSeenCount = Math.Max(entry.SourceSeenCount, entry.SourceKeys.Add(sourceKey) ? entry.SourceKeys.Count : entry.SourceSeenCount);
                entry.LastSeenUtc = DateTime.UtcNow;
                entry.LastOrder = order;
                if (entry.FirstOrder == 0)
                    entry.FirstOrder = order;
                if (firstSeen && settings.EnablePsdManifestFileWarmup && !string.IsNullOrWhiteSpace(entry.FilePath))
                    filePathToWarm = entry.FilePath;

                dirtyCount++;
                TrimLocked(manifest, settings);
                SaveIfNeededLocked(manifest, force: firstSeen, reason: "seen");
            }

            if (!string.IsNullOrWhiteSpace(filePathToWarm) && File.Exists(filePathToWarm))
            {
                WarmupManager.Record("image", filePathToWarm);
                PsdParallelPreloadCache.QueueFile(filePathToWarm, null, keyHash, "manifest-first-seen");
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"PSD manifest seen failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void RecordPrepared(string stateKey, string keyHash, double elapsedMs, string? reason, string? usage)
    {
        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
            if (!settings.EnablePsdStateManifest)
                return;
        }
        catch
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var manifest = LoadLocked();
                var entry = GetOrAddLocked(manifest, stateKey, keyHash);
                entry.PreparedCount++;
                entry.LastPreparedUtc = DateTime.UtcNow;
                entry.LastPrepareMs = elapsedMs;
                entry.TotalPrepareMs += elapsedMs;
                entry.MaxPrepareMs = Math.Max(entry.MaxPrepareMs, elapsedMs);
                entry.LastReason = reason ?? "";
                entry.LastUsage = usage ?? "";
                dirtyCount++;

                var force = elapsedMs >= settings.PsdManifestCandidateMinPrepareMs;
                SaveIfNeededLocked(manifest, force, "prepared");
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"PSD manifest prepared failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void LogCandidatesAndQueueWarmup(string reason)
    {
        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
            if (!settings.EnablePsdStateManifest)
                return;
        }
        catch
        {
            return;
        }

        List<PsdManifestEntry> candidates;
        try
        {
            lock (Gate)
            {
                candidates = LoadLocked()
                    .States
                    .Where(x => x.MaxPrepareMs >= settings.PsdManifestCandidateMinPrepareMs)
                    .OrderByDescending(x => x.MaxPrepareMs)
                    .ThenByDescending(x => x.SeenCount)
                    .Take(settings.PsdManifestCandidateLogCount)
                    .Select(x => x.CloneForRead())
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD manifest candidate load failed reason={reason}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        FastFileSourceLog.Write(
            $"PSD manifest candidates reason={reason} count={candidates.Count} thresholdMs={settings.PsdManifestCandidateMinPrepareMs:F1} manifest=\"{ManifestPath}\"");

        for (var i = 0; i < candidates.Count; i++)
        {
            var entry = candidates[i];
            FastFileSourceLog.Write(
                $"PSD manifest candidate rank={i + 1} hash={entry.Hash} seen={entry.SeenCount} prepared={entry.PreparedCount} avgPrepare={entry.AveragePrepareMs:F3} ms maxPrepare={entry.MaxPrepareMs:F3} ms firstOrder={entry.FirstOrder} lastOrder={entry.LastOrder} file=\"{entry.FilePath}\"");

            if (settings.EnablePsdManifestFileWarmup && !string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath))
            {
                WarmupManager.Record("image", entry.FilePath);
                PsdParallelPreloadCache.QueueFile(entry.FilePath, entry.FileStamp, entry.Hash, $"manifest-candidate:{reason}");
            }
        }
    }

    private static PsdManifest LoadLocked()
    {
        if (cached is not null)
            return cached;

        try
        {
            if (File.Exists(ManifestPath))
            {
                var json = File.ReadAllText(ManifestPath);
                cached = JsonSerializer.Deserialize<PsdManifest>(json, JsonOptions()) ?? new PsdManifest();
                cached.States ??= new List<PsdManifestEntry>();
                foreach (var state in cached.States)
                    state.SourceKeys ??= new HashSet<int>();
                FastFileSourceLog.Write($"PSD manifest loaded states={cached.States.Count} path=\"{ManifestPath}\"");
            }
            else
            {
                cached = new PsdManifest();
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD manifest load failed: {ex.GetType().Name}: {ex.Message}");
            cached = new PsdManifest();
        }

        return cached;
    }

    private static PsdManifestEntry GetOrAddLocked(PsdManifest manifest, string stateKey, string keyHash)
    {
        var entry = FindLocked(manifest, stateKey);
        if (entry is not null)
            return entry;

        var (filePath, fileStamp) = ExtractStateParts(stateKey);
        entry = new PsdManifestEntry
        {
            Key = stateKey,
            Hash = keyHash,
            FilePath = filePath,
            FileStamp = fileStamp,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        };
        manifest.States.Add(entry);
        FastFileSourceLog.Write($"PSD manifest new state hash={keyHash} file=\"{filePath}\" states={manifest.States.Count}");
        return entry;
    }

    private static PsdManifestEntry? FindLocked(PsdManifest manifest, string stateKey)
    {
        return manifest.States.FirstOrDefault(x => x.Key == stateKey);
    }

    private static void TrimLocked(PsdManifest manifest, FastFileSourceSettings settings)
    {
        var max = Math.Max(settings.PsdManifestMaxStates, 16);
        if (manifest.States.Count <= max)
            return;

        manifest.States = manifest
            .States
            .OrderByDescending(x => x.LastSeenUtc)
            .ThenByDescending(x => x.MaxPrepareMs)
            .ThenByDescending(x => x.SeenCount)
            .Take(max)
            .ToList();
    }

    private static void SaveIfNeededLocked(PsdManifest manifest, bool force, string reason)
    {
        var now = DateTime.UtcNow;
        if (!force && dirtyCount < 512 && now - lastSaveUtc < TimeSpan.FromSeconds(10))
            return;

        try
        {
            var dir = Path.GetDirectoryName(ManifestPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            manifest.Version = ManifestVersion;
            manifest.UpdatedUtc = now;
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions()));
            FastFileSourceLog.Write($"PSD manifest saved reason={reason} states={manifest.States.Count} dirty={dirtyCount} path=\"{ManifestPath}\"");
            dirtyCount = 0;
            lastSaveUtc = now;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD manifest save failed reason={reason}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (string FilePath, string FileStamp) ExtractStateParts(string stateKey)
    {
        var parts = stateKey.Split('|');
        return parts.Length >= 2 ? (parts[0], parts[1]) : ("", "");
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
        };
    }

    private sealed class PsdManifest
    {
        public int Version { get; set; } = ManifestVersion;

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public List<PsdManifestEntry> States { get; set; } = new();
    }

    private sealed class PsdManifestEntry
    {
        public string Key { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public string FileStamp { get; set; } = string.Empty;

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public DateTime LastPreparedUtc { get; set; }

        public long FirstOrder { get; set; }

        public long LastOrder { get; set; }

        public long SeenCount { get; set; }

        public long PreparedCount { get; set; }

        public int SourceSeenCount { get; set; }

        public HashSet<int> SourceKeys { get; set; } = new();

        public double LastPrepareMs { get; set; }

        public double TotalPrepareMs { get; set; }

        public double MaxPrepareMs { get; set; }

        public string LastReason { get; set; } = string.Empty;

        public string LastUsage { get; set; } = string.Empty;

        public double AveragePrepareMs => PreparedCount <= 0 ? 0 : TotalPrepareMs / PreparedCount;

        public PsdManifestEntry CloneForRead()
        {
            return new PsdManifestEntry
            {
                Key = Key,
                Hash = Hash,
                FilePath = FilePath,
                FileStamp = FileStamp,
                FirstSeenUtc = FirstSeenUtc,
                LastSeenUtc = LastSeenUtc,
                LastPreparedUtc = LastPreparedUtc,
                FirstOrder = FirstOrder,
                LastOrder = LastOrder,
                SeenCount = SeenCount,
                PreparedCount = PreparedCount,
                SourceSeenCount = SourceSeenCount,
                LastPrepareMs = LastPrepareMs,
                TotalPrepareMs = TotalPrepareMs,
                MaxPrepareMs = MaxPrepareMs,
                LastReason = LastReason,
                LastUsage = LastUsage,
            };
        }
    }

    public readonly record struct ManifestSnapshot(int States, int SlowCandidates, long Seen, string Path);
}
