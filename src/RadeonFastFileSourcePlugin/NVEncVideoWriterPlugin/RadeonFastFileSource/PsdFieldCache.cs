using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RadeonFastFileSourcePlugin;

internal static class PsdFieldCache
{
    private static readonly ConcurrentDictionary<string, FieldSnapshot> Snapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, AppliedState> AppliedSources = new();
    private static readonly object InitGate = new();
    private static FieldInfo? filePathField;
    private static FieldInfo? psdFileField;
    private static FieldInfo? psdFileSettingsField;
    private static FieldInfo? psdRootField;
    private static FieldInfo? defaultEnableLayersField;
    private static FieldInfo? isFirstField;
    private static FieldInfo? isClearedField;
    private static bool initialized;
    private static long applyCount;
    private static long captureCount;
    private static long clearCount;

    public static bool TryApplyBeforeUpdate(MethodBase method, object? instance, string stateKey, string? keyHash)
    {
        if (!IsEnabled() || instance is null || !IsPsdUpdate(method))
            return false;

        if (!TryEnsureFields(instance.GetType()))
            return false;

        var fileKey = CreateFileKey(stateKey);
        if (string.IsNullOrWhiteSpace(fileKey))
            return false;

        var sourceKey = RuntimeHelpers.GetHashCode(instance);
        if (!Snapshots.TryGetValue(fileKey, out var snapshot))
        {
            if (!PsdParallelPreloadCache.TryGetSnapshot(fileKey, keyHash, out var preloaded))
                return false;

            return ApplySnapshot(
                instance,
                sourceKey,
                keyHash ?? preloaded.KeyHash,
                preloaded.FileKeyHash,
                preloaded.FilePath,
                preloaded.PsdFile,
                preloaded.PsdFileSettings,
                preloaded.PsdRoot,
                preloaded.DefaultEnableLayers,
                ownerSourceKey: 0,
                source: "parallel-preload");
        }

        return ApplySnapshot(
            instance,
            sourceKey,
            keyHash ?? snapshot.KeyHash,
            snapshot.FileKeyHash,
            snapshot.FilePath,
            snapshot.PsdFile,
            snapshot.PsdFileSettings,
            snapshot.PsdRoot,
            snapshot.DefaultEnableLayers,
            snapshot.OwnerSourceKey,
            source: "field-cache");
    }

    private static bool ApplySnapshot(
        object instance,
        int sourceKey,
        string? keyHash,
        string fileKeyHash,
        string filePath,
        object psdFile,
        object? psdFileSettings,
        object psdRoot,
        object? defaultEnableLayers,
        int ownerSourceKey,
        string source)
    {
        try
        {
            filePathField?.SetValue(instance, filePath);
            psdFileField?.SetValue(instance, psdFile);
            psdFileSettingsField?.SetValue(instance, psdFileSettings);
            psdRootField?.SetValue(instance, psdRoot);
            defaultEnableLayersField?.SetValue(instance, defaultEnableLayers);
            isFirstField?.SetValue(instance, false);
            isClearedField?.SetValue(instance, false);

            AppliedSources[sourceKey] = new AppliedState(fileKeyHash, keyHash ?? "", ownerSourceKey);
            var applied = Interlocked.Increment(ref applyCount);
            if (applied <= 32 || applied % 100 == 0)
            {
                FastFileSourceLog.Write(
                    $"PSD field cache apply source={source} hash={keyHash} fileKeyHash={fileKeyHash} sourceKey={sourceKey} owner={ownerSourceKey} applies={applied} captures={Volatile.Read(ref captureCount)} path=\"{filePath}\"");
            }

            return true;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD field cache apply failed source={source} hash={keyHash}: {ex.GetType().Name}: {ex.Message}");
            AppliedSources.TryRemove(sourceKey, out _);
            return false;
        }
    }

    public static void CaptureAfterUpdate(MethodBase method, object? instance, string stateKey, string? keyHash, double elapsedMs)
    {
        if (!IsEnabled() || instance is null || !IsPsdUpdate(method))
            return;

        if (!TryEnsureFields(instance.GetType()))
            return;

        var fileKey = CreateFileKey(stateKey);
        if (string.IsNullOrWhiteSpace(fileKey))
            return;

        try
        {
            var psdFile = psdFileField?.GetValue(instance);
            var psdRoot = psdRootField?.GetValue(instance);
            if (psdFile is null || psdRoot is null)
                return;

            var sourceKey = RuntimeHelpers.GetHashCode(instance);
            var snapshot = new FieldSnapshot(
                fileKey,
                CreateShortHash(fileKey),
                keyHash ?? "",
                GetFilePath(fileKey),
                psdFile,
                psdFileSettingsField?.GetValue(instance),
                psdRoot,
                defaultEnableLayersField?.GetValue(instance),
                sourceKey);

            var added = Snapshots.TryAdd(fileKey, snapshot);
            if (!added && elapsedMs >= FastFileSourceSettingsStore.Current.PsdFieldCacheReplaceMinPrepareMs)
            {
                Snapshots[fileKey] = snapshot;
                added = true;
            }

            if (!added)
                return;

            AppliedSources[sourceKey] = new AppliedState(fileKey, keyHash ?? "", sourceKey);
            var captures = Interlocked.Increment(ref captureCount);
            FastFileSourceLog.Write(
                $"PSD field cache capture hash={keyHash} fileKeyHash={snapshot.FileKeyHash} source={sourceKey} captures={captures} elapsed={elapsedMs:F3} ms path=\"{snapshot.FilePath}\"");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD field cache capture failed hash={keyHash}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void BeforeDispose(MethodBase method, object? instance)
    {
        if (instance is null ||
            method.Name != "Dispose" ||
            method.DeclaringType?.FullName?.Equals("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource", StringComparison.Ordinal) != true)
        {
            return;
        }

        if (!TryEnsureFields(instance.GetType()))
            return;

        var sourceKey = RuntimeHelpers.GetHashCode(instance);
        if (!AppliedSources.TryRemove(sourceKey, out var state))
            return;

        try
        {
            psdFileField?.SetValue(instance, null);
            psdFileSettingsField?.SetValue(instance, null);
            psdRootField?.SetValue(instance, null);
            defaultEnableLayersField?.SetValue(instance, null);
            var clears = Interlocked.Increment(ref clearCount);
            if (clears <= 32 || clears % 100 == 0)
            {
                FastFileSourceLog.Write(
                    $"PSD field cache detach-before-dispose hash={state.KeyHash} source={sourceKey} owner={state.OwnerSourceKey} clears={clears} snapshots={Snapshots.Count}");
            }
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD field cache detach failed source={sourceKey}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsEnabled()
    {
        try
        {
            return FastFileSourceSettingsStore.Current.EnableExperimentalPsdFieldCache;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPsdUpdate(MethodBase method)
    {
        return method.Name == "Update" &&
            method.DeclaringType?.FullName?.Equals("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource", StringComparison.Ordinal) == true;
    }

    private static bool TryEnsureFields(Type type)
    {
        if (initialized)
            return psdFileField is not null && psdRootField is not null;

        lock (InitGate)
        {
            if (initialized)
                return psdFileField is not null && psdRootField is not null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            filePathField = type.GetField("filePath", flags);
            psdFileField = type.GetField("psdFile", flags);
            psdFileSettingsField = type.GetField("psdFileSettings", flags);
            psdRootField = type.GetField("psdRoot", flags);
            defaultEnableLayersField = type.GetField("defaultEnableLayers", flags);
            isFirstField = type.GetField("isFirst", flags);
            isClearedField = type.GetField("isCleared", flags);
            initialized = true;

            FastFileSourceLog.Write(
                $"PSD field cache fields ready filePath={filePathField is not null} psdFile={psdFileField is not null} settings={psdFileSettingsField is not null} root={psdRootField is not null} defaultLayers={defaultEnableLayersField is not null} isFirst={isFirstField is not null} isCleared={isClearedField is not null}");

            return psdFileField is not null && psdRootField is not null;
        }
    }

    private static string CreateFileKey(string stateKey)
    {
        var parts = stateKey.Split('|');
        return parts.Length >= 2 ? $"{parts[0]}|{parts[1]}" : "";
    }

    private static string GetFilePath(string fileKey)
    {
        var separator = fileKey.IndexOf('|');
        return separator <= 0 ? fileKey : fileKey[..separator];
    }

    private static string CreateShortHash(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 4));
    }

    private sealed record FieldSnapshot(
        string FileKey,
        string FileKeyHash,
        string KeyHash,
        string FilePath,
        object PsdFile,
        object? PsdFileSettings,
        object PsdRoot,
        object? DefaultEnableLayers,
        int OwnerSourceKey);

    private readonly record struct AppliedState(string FileKey, string KeyHash, int OwnerSourceKey);
}
