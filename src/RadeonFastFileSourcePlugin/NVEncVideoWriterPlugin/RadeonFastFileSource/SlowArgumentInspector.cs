using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RadeonFastFileSourcePlugin;

internal static class SlowArgumentInspector
{
    private static readonly ConcurrentDictionary<string, int> TypeLogCounts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> WarmedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LoggedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, byte> RenderSceneWarmups = new();

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".bmp",
        ".gif",
        ".jfif",
        ".jpeg",
        ".jpg",
        ".jxl",
        ".png",
        ".psd",
        ".svg",
        ".tif",
        ".tiff",
        ".webp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi",
        ".m2ts",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".mts",
        ".ts",
        ".webm",
        ".wmv",
    };

    public static void TryInspect(MethodBase method, string methodName, double elapsedMs, object[]? args)
    {
        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
        }
        catch
        {
            return;
        }

        if ((!settings.EnableSlowArgumentPropertyLog && !settings.EnableSlowArgumentPathWarmup) ||
            elapsedMs < settings.SlowArgumentPropertyLogThresholdMs ||
            args is null ||
            args.Length == 0 ||
            !IsInterestingMethod(methodName))
        {
            return;
        }

        foreach (var arg in args)
        {
            if (arg is null)
                continue;

            if (settings.EnableSlowArgumentPathWarmup)
                CollectAndRecordPaths(methodName, elapsedMs, arg);

            var type = arg.GetType();
            if (!IsInterestingType(type) && !IsYmmType(type))
                continue;

            InspectObject(settings, methodName, elapsedMs, arg, type, depth: 0, prefix: "");
        }
    }

    public static void TryStartRenderSceneWarmup(string methodName, object[]? args)
    {
        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
        }
        catch
        {
            return;
        }

        if (!settings.EnableRenderScenePathWarmup ||
            !settings.EnableProjectWarmup ||
            !settings.EnableSlowArgumentPathWarmup ||
            settings.RenderScenePathWarmupMaxPaths <= 0 ||
            args is null ||
            args.Length == 0 ||
            !methodName.Contains("VideoFileWriter.VideoFileWriter.Render", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var root = args[0];
        if (root is null)
            return;

        var key = RuntimeHelpers.GetHashCode(root);
        if (!RenderSceneWarmups.TryAdd(key, 0))
            return;

        _ = Task.Run(() => RenderSceneWarmupCore(settings, methodName, root));
    }

    private static void RenderSceneWarmupCore(FastFileSourceSettings settings, string methodName, object root)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var paths = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var budget = new PathScanBudget(
            Math.Max(1, settings.RenderScenePathWarmupMaxPaths),
            Math.Max(1, settings.RenderScenePathWarmupMaxCollectionItems));

        try
        {
            CollectExistingPaths(
                root,
                paths,
                visited,
                depth: 0,
                maxDepth: Math.Max(1, settings.RenderScenePathWarmupMaxDepth),
                collectionLimit: Math.Max(1, settings.RenderScenePathWarmupMaxCollectionItems),
                budget);

            var recorded = 0;
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(settings.RenderScenePathWarmupMaxPaths))
            {
                if (RecordWarmupPath(path))
                    recorded++;
            }

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FastFileSourceLog.Write(
                $"Render scene warmup method=\"{methodName}\" discovered={paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()} recorded={recorded} elapsed={elapsedMs:F3} ms visited={visited.Count} pathsLimit={settings.RenderScenePathWarmupMaxPaths}");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Render scene warmup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CollectAndRecordPaths(string methodName, double elapsedMs, object root)
    {
        var paths = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CollectExistingPaths(root, paths, visited, depth: 0);

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _ = RecordWarmupPath(path);
            if (LoggedPaths.TryAdd($"{methodName}|{path}", 0))
                FastFileSourceLog.Write($"Slow arg path method=\"{methodName}\" elapsed={elapsedMs:F3} ms path=\"{path}\"");
        }
    }

    private static void CollectExistingPaths(object? value, List<string> paths, HashSet<object> visited, int depth)
    {
        CollectExistingPaths(value, paths, visited, depth, maxDepth: 5, collectionLimit: 12, new PathScanBudget(128, 512));
    }

    private static void CollectExistingPaths(
        object? value,
        List<string> paths,
        HashSet<object> visited,
        int depth,
        int maxDepth,
        int collectionLimit,
        PathScanBudget budget)
    {
        if (value is null || depth > maxDepth || budget.IsExhausted)
            return;

        if (value is string text)
        {
            AddPathIfExists(text, paths);
            if (paths.Count >= budget.MaxPaths)
                budget.IsExhausted = true;
            return;
        }

        var type = value.GetType();
        if (IsSimple(type))
            return;

        if (!type.IsValueType && !visited.Add(value))
            return;

        if (value is IEnumerable enumerable)
        {
            var count = 0;
            foreach (var item in enumerable)
            {
                CollectExistingPaths(item, paths, visited, depth + 1, maxDepth, collectionLimit, budget);
                count++;
                budget.ItemsVisited++;
                if (count >= collectionLimit || budget.ItemsVisited >= budget.MaxItems || budget.IsExhausted)
                    break;
            }

            return;
        }

        var typeName = type.FullName ?? type.Name;
        if (!IsInterestingType(type) &&
            !IsYmmType(type) &&
            !typeName.Contains("Parameter", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Contains("Face", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Contains("Item", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var member in EnumerateReadableMembers(type))
        {
            object? memberValue;
            try
            {
                memberValue = member.GetValue(value);
            }
            catch
            {
                continue;
            }

            CollectExistingPaths(memberValue, paths, visited, depth + 1, maxDepth, collectionLimit, budget);
            if (budget.IsExhausted)
                return;
        }
    }

    private static void InspectObject(FastFileSourceSettings settings, string methodName, double elapsedMs, object target, Type type, int depth, string prefix)
    {
        if (depth > 1)
            return;

        var fields = new List<string>();
        var paths = new List<string>();

        foreach (var member in EnumerateReadableMembers(type))
        {
            object? value;
            try
            {
                value = member.GetValue(target);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            var name = string.IsNullOrEmpty(prefix) ? member.Name : $"{prefix}.{member.Name}";
            AddValue(settings, methodName, elapsedMs, type, name, value, fields, paths, depth);

            if (fields.Count >= 80)
                break;
        }

        if (settings.EnableSlowArgumentPathWarmup)
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                RecordWarmupPath(path);
        }

        if (!settings.EnableSlowArgumentPropertyLog || fields.Count == 0)
            return;

        var key = $"{methodName}|{type.FullName}";
        var count = TypeLogCounts.AddOrUpdate(key, 1, (_, old) => old + 1);
        if (count > settings.SlowArgumentPropertyMaxPerType)
            return;

        FastFileSourceLog.Write(
            $"Slow arg inspect method=\"{methodName}\" elapsed={elapsedMs:F3} ms argType=\"{type.FullName}\" sample={count}/{settings.SlowArgumentPropertyMaxPerType} values=[{string.Join("; ", fields)}]");
    }

    private static void AddValue(
        FastFileSourceSettings settings,
        string methodName,
        double elapsedMs,
        Type ownerType,
        string name,
        object value,
        List<string> fields,
        List<string> paths,
        int depth)
    {
        switch (value)
        {
            case string text:
                if (string.IsNullOrWhiteSpace(text))
                    return;

                fields.Add($"{name}=\"{Trim(text, 180)}\"");
                AddPathIfExists(text, paths);
                return;

            case TimeSpan time:
                fields.Add($"{name}={time}");
                return;

            case DateTime dateTime:
                fields.Add($"{name}={dateTime:O}");
                return;

            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                fields.Add($"{name}={value}");
                return;
        }

        var valueType = value.GetType();
        if (valueType.IsEnum)
        {
            fields.Add($"{name}={value}");
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            fields.Add($"{name}=<{valueType.Name} count={TryCount(enumerable)}>");
            return;
        }

        if (depth == 0 && (IsInterestingType(valueType) || IsYmmType(valueType)))
        {
            fields.Add($"{name}=<{valueType.FullName}>");
            InspectNested(settings, methodName, elapsedMs, value, valueType, fields, paths, name);
            return;
        }

        if (name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("File", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Source", StringComparison.OrdinalIgnoreCase) ||
            IsInterestingType(ownerType))
        {
            fields.Add($"{name}=<{valueType.FullName ?? valueType.Name}>");
        }
    }

    private static void InspectNested(
        FastFileSourceSettings settings,
        string methodName,
        double elapsedMs,
        object target,
        Type type,
        List<string> fields,
        List<string> paths,
        string prefix)
    {
        foreach (var member in EnumerateReadableMembers(type))
        {
            object? value;
            try
            {
                value = member.GetValue(target);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            AddValue(settings, methodName, elapsedMs, type, $"{prefix}.{member.Name}", value, fields, paths, depth: 1);
            if (fields.Count >= 80)
                return;
        }
    }

    private static IEnumerable<ReadableMember> EnumerateReadableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var property in type.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            var getter = property.GetGetMethod(nonPublic: true);
            if (getter is null || getter.GetParameters().Length != 0 || getter.IsStatic)
                continue;

            yield return new ReadableMember(property.Name, property.GetValue);
        }

        foreach (var field in type.GetFields(flags))
        {
            if (field.IsStatic)
                continue;

            yield return new ReadableMember(field.Name, field.GetValue);
        }
    }

    private static void AddPathIfExists(string value, List<string> paths)
    {
        var candidate = value.Trim().Trim('"');
        if (candidate.Length < 3)
            return;

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
                return;

            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
                paths.Add(fullPath);
        }
        catch
        {
        }
    }

    private static bool RecordWarmupPath(string path)
    {
        if (!WarmedPaths.TryAdd(path, 0))
            return false;

        var type = Classify(path);
        if (type is null)
            return false;

        WarmupManager.Record(type, path);
        FastFileSourceLog.WriteDetailed($"Slow arg warmup recorded type={type} path=\"{path}\"");
        return true;
    }

    private static string? Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (AudioExtensions.Contains(extension))
            return "audio";
        if (ImageExtensions.Contains(extension))
            return "image";
        if (VideoExtensions.Contains(extension))
            return "video";
        return null;
    }

    private static bool IsInterestingMethod(string methodName)
    {
        return methodName.Contains("Tachie", StringComparison.OrdinalIgnoreCase) ||
            methodName.Contains("TimelineSource.Update", StringComparison.OrdinalIgnoreCase) ||
            methodName.Contains("EffectedItemSource.Update", StringComparison.OrdinalIgnoreCase) ||
            methodName.Contains("VideoFileWriter.VideoFileWriter.Render", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInterestingType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Contains("Tachie", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Psd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TimelineItemSourceDescription", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SourceDescription", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VideoItem", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AudioItem", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ImageItem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimple(Type type)
    {
        return type.IsPrimitive ||
            type.IsEnum ||
            type == typeof(decimal) ||
            type == typeof(DateTime) ||
            type == typeof(TimeSpan) ||
            type == typeof(Guid);
    }

    private static bool IsYmmType(Type type)
    {
        var name = type.FullName ?? "";
        return name.StartsWith("YukkuriMovieMaker.", StringComparison.Ordinal);
    }

    private static string TryCount(IEnumerable enumerable)
    {
        if (enumerable is ICollection collection)
            return collection.Count.ToString();

        return "?";
    }

    private static string Trim(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private readonly record struct ReadableMember(string Name, Func<object, object?> GetValue);

    private sealed class PathScanBudget(int maxPaths, int maxItems)
    {
        public int MaxPaths { get; } = maxPaths;

        public int MaxItems { get; } = maxItems;

        public int ItemsVisited { get; set; }

        public bool IsExhausted { get; set; }
    }
}
