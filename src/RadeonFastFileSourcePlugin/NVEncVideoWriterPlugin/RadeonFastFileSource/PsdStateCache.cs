using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace RadeonFastFileSourcePlugin;

internal static class PsdStateCache
{
    private static readonly ConcurrentDictionary<int, SourceState> SourceStates = new();
    private static readonly ConcurrentDictionary<string, byte> KnownStates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, StateStats> StateStatsMap = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ExportStateRecord> ExportStates = new(StringComparer.Ordinal);
    private static long hitCount;
    private static long missCount;
    private static long preparedCount;
    private static long crossSourceCandidateCount;

    public static CacheSnapshot Snapshot
    {
        get
        {
            var hits = Volatile.Read(ref hitCount);
            var misses = Volatile.Read(ref missCount);
            var prepared = Volatile.Read(ref preparedCount);
            return new CacheSnapshot(hits, misses, prepared, KnownStates.Count, SourceStates.Count);
        }
    }

    public static string CreateStatusText()
    {
        var snapshot = Snapshot;
        var manifest = PsdStateManifest.Snapshot;
        return $"PSDキャッシュ: ヒット率 {snapshot.HitRate:P1} / hit {snapshot.Hits} / miss {snapshot.Misses} / 状態 {snapshot.States} / 書き出し状態 {ExportStates.Count} / 次回候補 {manifest.SlowCandidates}/{manifest.States}";
    }

    public static bool TrySkip(MethodBase method, object? instance, object[]? args, out PsdCacheDecision decision)
    {
        decision = PsdCacheDecision.Empty;
        if (!FastFileSourceSettingsStore.Current.EnablePsdStateCache ||
            instance is null ||
            args is null ||
            args.Length == 0 ||
            !IsPsdUpdate(method))
        {
            return false;
        }

        if (RuntimeContextDetector.IsPreviewCallStack())
        {
            FastFileSourceLog.WriteDetailed("PSD state cache bypass reason=preview");
            return false;
        }

        var stateKey = TryCreateStateKey(args[0]);
        if (string.IsNullOrWhiteSpace(stateKey))
        {
            decision = new PsdCacheDecision(null, null, "key-unavailable", null, null, false, false);
            return false;
        }

        var wasKnownState = !KnownStates.TryAdd(stateKey, 0);
        var sourceKey = RuntimeHelpers.GetHashCode(instance);
        var usage = GetProperty(args[0], "Usage")?.ToString();
        var keyHash = CreateShortHash(stateKey);
        var manifestKnown = PsdStateManifest.IsKnown(stateKey);
        PsdParallelPreloadCache.QueueFromStateKey(stateKey, keyHash, manifestKnown ? "psd-state-known" : "psd-state-seen");
        var stateStats = StateStatsMap.GetOrAdd(stateKey, _ => new StateStats());
        stateStats.Seen(sourceKey);
        if (usage?.Equals("Exporting", StringComparison.OrdinalIgnoreCase) == true)
            RecordExportState(stateKey, keyHash, sourceKey, stateStats);

        if (SourceStates.TryGetValue(sourceKey, out var state) && state.LastKey == stateKey && state.HasOutput)
        {
            var hits = Interlocked.Increment(ref hitCount);
            stateStats.Hit();
            decision = new PsdCacheDecision(stateKey, keyHash, "hit-same-source", usage, sourceKey, true, manifestKnown);
            LogSummaryIfNeeded("hit", hits);
            return true;
        }

        var reason = CreateMissReason(wasKnownState, sourceKey, state);
        var misses = Interlocked.Increment(ref missCount);
        stateStats.Miss();
        if (reason.Contains("known-state", StringComparison.Ordinal))
            Interlocked.Increment(ref crossSourceCandidateCount);

        decision = new PsdCacheDecision(stateKey, keyHash, reason, usage, sourceKey, false, manifestKnown);
        LogMissIfNeeded(misses, decision, stateStats);
        return false;
    }

    public static void MarkUpdated(MethodBase method, object? instance, PsdCacheDecision decision, double elapsedMs, Exception? exception)
    {
        if (instance is null ||
            string.IsNullOrWhiteSpace(decision.StateKey) ||
            exception is not null ||
            !IsPsdUpdate(method))
        {
            return;
        }

        var sourceKey = RuntimeHelpers.GetHashCode(instance);
        SourceStates[sourceKey] = new SourceState(decision.StateKey, true);
        var prepared = Interlocked.Increment(ref preparedCount);
        var stateStats = StateStatsMap.GetOrAdd(decision.StateKey, _ => new StateStats());
        stateStats.Prepared(elapsedMs);
        PsdStateManifest.RecordPrepared(decision.StateKey, decision.KeyHash ?? "", elapsedMs, decision.Reason, decision.Usage);

        if (elapsedMs >= FastFileSourceSettingsStore.Current.InjectionSlowThresholdMs)
        {
            FastFileSourceLog.Write(
                $"PSD state prepared elapsed={elapsedMs:F3} ms hash={decision.KeyHash} reason={decision.Reason} usage={decision.Usage} manifestKnown={decision.ManifestKnown} stateHits={stateStats.Hits} stateMisses={stateStats.Misses} statePrepared={stateStats.PreparedCount} stateSources={stateStats.SourceCount} key=\"{TrimKey(decision.StateKey)}\"");
        }

        LogSummaryIfNeeded("prepared", prepared);
    }

    public static void NoteSkipped(PsdCacheDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.StateKey))
            return;

        var stateStats = StateStatsMap.GetOrAdd(decision.StateKey, _ => new StateStats());
        if (stateStats.Hits is <= 5 or 25 or 100 || stateStats.Hits % 500 == 0)
        {
            FastFileSourceLog.Write(
                $"PSD state skip hash={decision.KeyHash} usage={decision.Usage} stateHits={stateStats.Hits} stateMisses={stateStats.Misses} statePrepared={stateStats.PreparedCount} stateSources={stateStats.SourceCount}");
        }
    }

    private static void LogSummaryIfNeeded(string reason, long count)
    {
        var interval = FastFileSourceSettingsStore.Current.PsdStateCacheLogInterval;
        if (interval <= 0)
            return;

        if (count is 1 || count % interval == 0)
        {
            FastFileSourceLog.Write(
                $"PSD state cache {reason} hits={Volatile.Read(ref hitCount)} misses={Volatile.Read(ref missCount)} prepared={Volatile.Read(ref preparedCount)} states={KnownStates.Count} sources={SourceStates.Count} crossSourceCandidates={Volatile.Read(ref crossSourceCandidateCount)}");
        }
    }

    private static string CreateMissReason(bool wasKnownState, int sourceKey, SourceState? sourceState)
    {
        if (sourceState is null)
            return wasKnownState ? "known-state-new-source" : "new-state-new-source";

        if (!sourceState.HasOutput)
            return wasKnownState ? "known-state-source-no-output" : "new-state-source-no-output";

        return wasKnownState ? "known-state-source-state-change" : "new-state-source-state-change";
    }

    private static void LogMissIfNeeded(long misses, PsdCacheDecision decision, StateStats stateStats)
    {
        var shouldLog = misses <= 24 ||
            misses % 100 == 0 ||
            decision.Reason?.Contains("known-state", StringComparison.Ordinal) == true;
        if (!shouldLog)
            return;

        FastFileSourceLog.Write(
            $"PSD state miss reason={decision.Reason} hash={decision.KeyHash} usage={decision.Usage} source={decision.SourceKey} manifestKnown={decision.ManifestKnown} misses={misses} stateHits={stateStats.Hits} stateMisses={stateStats.Misses} statePrepared={stateStats.PreparedCount} stateSources={stateStats.SourceCount}");
    }

    private static void RecordExportState(string stateKey, string keyHash, int sourceKey, StateStats stateStats)
    {
        PsdStateManifest.RecordExportSeen(stateKey, keyHash, sourceKey);
        var record = ExportStates.GetOrAdd(stateKey, _ => new ExportStateRecord(keyHash));
        var count = record.Seen(sourceKey);
        if (count is 1 or 2 or 5 or 10 || count % 100 == 0)
        {
            FastFileSourceLog.Write(
                $"PSD export state seen hash={keyHash} count={count} exportStates={ExportStates.Count} stateHits={stateStats.Hits} stateMisses={stateStats.Misses} statePrepared={stateStats.PreparedCount} stateSources={stateStats.SourceCount}");
        }
    }

    private static bool IsPsdUpdate(MethodBase method)
    {
        return method.Name == "Update" &&
            method.DeclaringType?.FullName?.Equals("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource", StringComparison.Ordinal) == true;
    }

    private static string? TryCreateStateKey(object desc)
    {
        try
        {
            var tachie = GetProperty(desc, "Tachie");
            if (tachie is null)
                return null;

            var characterParameter = GetProperty(tachie, "CharacterParameter");
            var itemParameter = GetProperty(tachie, "ItemParameter");
            var filePath = GetStringProperty(itemParameter, "FilePath");
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = GetStringProperty(characterParameter, "FilePath");
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var fullPath = Path.GetFullPath(filePath);
            var fileStamp = GetFileStamp(fullPath);
            var mouthShape = GetProperty(desc, "MouthShape")?.ToString() ?? "";
            var voiceVolume = GetVolumeBucket(GetProperty(desc, "VoiceVolume"), mouthShape);
            var timelinePosition = GetProperty(desc, "TimelinePosition")?.ToString() ?? "";
            var itemPosition = GetProperty(desc, "ItemPosition")?.ToString() ?? "";
            var layer = GetProperty(desc, "Layer")?.ToString() ?? "";
            var itemLayers = JoinEnumerable(GetProperty(itemParameter, "EnableLayers"));
            var faces = BuildFacesKey(GetProperty(tachie, "Faces"));

            return string.Join(
                "|",
                fullPath,
                fileStamp,
                mouthShape,
                voiceVolume,
                timelinePosition,
                itemPosition,
                layer,
                itemLayers,
                faces);
        }
        catch
        {
            return null;
        }
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

    private static string GetVolumeBucket(object? value, string mouthShape)
    {
        if (mouthShape.Equals("Silent", StringComparison.OrdinalIgnoreCase))
            return "silent";

        if (value is not IConvertible convertible)
            return "";

        try
        {
            var volume = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
            return Math.Round(volume * 50, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static string BuildFacesKey(object? faces)
    {
        if (faces is not System.Collections.IEnumerable enumerable)
            return "";

        var builder = new StringBuilder();
        var count = 0;
        foreach (var face in enumerable)
        {
            if (count++ >= 16)
                break;

            if (builder.Length > 0)
                builder.Append(',');

            var layer = GetProperty(face, "Layer")?.ToString() ?? "";
            var itemPosition = GetProperty(face, "ItemPosition")?.ToString() ?? "";
            var itemDuration = GetProperty(face, "ItemDuration")?.ToString() ?? "";
            var parameter = GetProperty(face, "FaceParameter");
            builder.Append(layer).Append(':').Append(itemPosition).Append(':').Append(itemDuration).Append(':').Append(parameter?.GetHashCode() ?? 0);
        }

        return builder.ToString();
    }

    private static string JoinEnumerable(object? value)
    {
        if (value is not System.Collections.IEnumerable enumerable || value is string)
            return "";

        var values = new List<string>();
        foreach (var item in enumerable)
        {
            if (item is null)
                continue;
            values.Add(item.ToString() ?? "");
            if (values.Count >= 256)
                break;
        }

        values.Sort(StringComparer.Ordinal);
        return string.Join(",", values);
    }

    private static object? GetProperty(object? target, string name)
    {
        if (target is null)
            return null;

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

    private static string? GetStringProperty(object? target, string name)
    {
        return GetProperty(target, name) as string;
    }

    private static string TrimKey(string key)
    {
        return key.Length <= 360 ? key : key[..360] + "...";
    }

    private static string CreateShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    private sealed record SourceState(string LastKey, bool HasOutput);

    private sealed class StateStats
    {
        private readonly ConcurrentDictionary<int, byte> sources = new();
        private long hits;
        private long misses;
        private long prepared;
        private double maxPrepareMs;

        public long Hits => Volatile.Read(ref hits);

        public long Misses => Volatile.Read(ref misses);

        public long PreparedCount => Volatile.Read(ref prepared);

        public int SourceCount => sources.Count;

        public double MaxPrepareMs => Volatile.Read(ref maxPrepareMs);

        public void Seen(int sourceKey)
        {
            sources.TryAdd(sourceKey, 0);
        }

        public void Hit()
        {
            Interlocked.Increment(ref hits);
        }

        public void Miss()
        {
            Interlocked.Increment(ref misses);
        }

        public void Prepared(double elapsedMs)
        {
            Interlocked.Increment(ref prepared);
            UpdateMax(elapsedMs);
        }

        private void UpdateMax(double value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxPrepareMs);
                if (value <= current)
                    return;

                if (Interlocked.CompareExchange(ref maxPrepareMs, value, current) == current)
                    return;
            }
        }
    }

    private sealed class ExportStateRecord(string keyHash)
    {
        private readonly ConcurrentDictionary<int, byte> sources = new();
        private long seenCount;

        public string KeyHash { get; } = keyHash;

        public long Seen(int sourceKey)
        {
            sources.TryAdd(sourceKey, 0);
            return Interlocked.Increment(ref seenCount);
        }
    }

    public readonly record struct PsdCacheDecision(
        string? StateKey,
        string? KeyHash,
        string? Reason,
        string? Usage,
        int? SourceKey,
        bool Skipped,
        bool ManifestKnown)
    {
        public static PsdCacheDecision Empty { get; } = new(null, null, null, null, null, false, false);
    }

    public readonly record struct CacheSnapshot(long Hits, long Misses, long Prepared, int States, int Sources)
    {
        public double HitRate
        {
            get
            {
                var total = Hits + Misses;
                return total <= 0 ? 0 : (double)Hits / total;
            }
        }
    }
}
