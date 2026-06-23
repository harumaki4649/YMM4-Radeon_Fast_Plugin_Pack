using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RadeonFastFileSourcePlugin;

internal static class InternalInjectionProfiler
{
    private static readonly object Gate = new();
    private static bool initialized;
    private static object? harmony;
    private static readonly ConcurrentDictionary<string, MethodStats> Stats = new(StringComparer.Ordinal);

    public static void TryInitialize()
    {
        lock (Gate)
        {
            if (initialized)
                return;

            initialized = true;
        }

        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Injection profiler skipped reason=settings-failed error={ex.GetType().Name}: {ex.Message}");
            return;
        }

        var enablePsdHookOnly = !settings.EnableInjectionProfiler &&
            (settings.EnablePsdStateCache ||
             settings.EnableExperimentalPsdFieldCache ||
             settings.EnableExperimentalPsdParallelPreload);

        if (!settings.EnableInjectionProfiler && !enablePsdHookOnly)
        {
            FastFileSourceLog.Write("Injection profiler disabled by settings");
            return;
        }

        try
        {
            var harmonyAssembly = LoadHarmonyAssembly();
            if (harmonyAssembly is null)
            {
                FastFileSourceLog.Write("Injection profiler unavailable reason=0Harmony.dll-not-found");
                return;
            }

            var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", throwOnError: false);
            var harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", throwOnError: false);
            if (harmonyType is null || harmonyMethodType is null)
            {
                FastFileSourceLog.Write($"Injection profiler unavailable reason=harmony-types-missing assembly=\"{harmonyAssembly.Location}\"");
                return;
            }

            harmony = Activator.CreateInstance(harmonyType, "radeon.fastfilesource.injection.profiler");
            if (harmony is null)
            {
                FastFileSourceLog.Write("Injection profiler unavailable reason=harmony-create-failed");
                return;
            }

            var prefix = CreateHarmonyMethod(harmonyMethodType, nameof(ProfilePrefix));
            var postfix = CreateHarmonyMethod(harmonyMethodType, nameof(ProfilePostfix));
            var finalizer = CreateHarmonyMethod(harmonyMethodType, nameof(ProfileFinalizer));
            var patchMethod = FindPatchMethod(harmonyType);
            if (patchMethod is null)
            {
                FastFileSourceLog.Write("Injection profiler unavailable reason=patch-method-missing");
                return;
            }

            var patched = 0;
            foreach (var target in ResolveTargets(settings, enablePsdHookOnly))
            {
                try
                {
                    patchMethod.Invoke(harmony, new[] { target.Method, prefix, postfix, null, finalizer });
                    FastFileSourceLog.Write($"Injection profiler patched name={target.Name} method=\"{FormatMethod(target.Method)}\"");
                    patched++;
                }
                catch (Exception ex)
                {
                    FastFileSourceLog.Write($"Injection profiler patch failed name={target.Name}: {Unwrap(ex).GetType().Name}: {Unwrap(ex).Message}");
                }
            }

            FastFileSourceLog.Write($"Injection profiler ready patched={patched} mode={(enablePsdHookOnly ? "psd-hook-only" : "full-profiler")} harmony=\"{harmonyAssembly.Location}\" experimentalParallel={settings.EnableExperimentalParallelInjection}");
            if (settings.EnableExperimentalParallelInjection)
                FastFileSourceLog.Write("Experimental parallel injection requested. PSD parallel preload is controlled by EnableExperimentalPsdParallelPreload.");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Injection profiler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool ProfilePrefix(MethodBase __originalMethod, object? __instance, object[] __args, out ProfileState __state)
    {
        PsdFieldCache.BeforeDispose(__originalMethod, __instance);
        var skipped = PsdStateCache.TrySkip(__originalMethod, __instance, __args, out var psdDecision);
        if (!skipped && !string.IsNullOrWhiteSpace(psdDecision.StateKey))
            PsdFieldCache.TryApplyBeforeUpdate(__originalMethod, __instance, psdDecision.StateKey, psdDecision.KeyHash);

        __state = new ProfileState(Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, SummarizeArgs(__args), skipped, psdDecision);
        return !skipped;
    }

    public static void ProfilePostfix(MethodBase __originalMethod, object? __instance, object[] __args, ProfileState __state)
    {
        LogCompletion(__originalMethod, __instance, __args, __state, exception: null);
    }

    public static Exception? ProfileFinalizer(MethodBase __originalMethod, object? __instance, object[] __args, ProfileState __state, Exception? __exception)
    {
        if (__exception is not null)
            LogCompletion(__originalMethod, __instance, __args, __state, __exception);
        return __exception;
    }

    private static void LogCompletion(MethodBase method, object? instance, object[]? args, ProfileState state, Exception? exception)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(state.StartTimestamp).TotalMilliseconds;
        var settings = FastFileSourceSettingsStore.Current;
        var name = FormatMethod(method);
        if (!state.Skipped)
        {
            PsdStateCache.MarkUpdated(method, instance, state.PsdDecision, elapsedMs, exception);
            if (exception is null && !string.IsNullOrWhiteSpace(state.PsdDecision.StateKey))
                PsdFieldCache.CaptureAfterUpdate(method, instance, state.PsdDecision.StateKey, state.PsdDecision.KeyHash, elapsedMs);
        }
        else
        {
            PsdStateCache.NoteSkipped(state.PsdDecision);
        }
        ProjectWarmupAnalyzer.TryScanFromInjection(name, args);
        SlowArgumentInspector.TryStartRenderSceneWarmup(name, args);
        SlowArgumentInspector.TryInspect(method, name, elapsedMs, args);
        var stats = Stats.GetOrAdd(name, _ => new MethodStats());
        var snapshot = stats.Add(elapsedMs);
        var slowThresholdMs = name.EndsWith(".Render", StringComparison.Ordinal)
            ? Math.Max(settings.InjectionSlowThresholdMs, settings.InjectionRenderSlowThresholdMs)
            : settings.InjectionSlowThresholdMs;
        var summaryInterval = settings.InjectionSummaryInterval;
        var shouldLog = exception is not null
            || elapsedMs >= slowThresholdMs
            || snapshot.Count is 1 or 10 or 50
            || (summaryInterval > 0 && snapshot.Count % summaryInterval == 0);

        if (!shouldLog)
            return;

        var argText = settings.EnableInjectionArgumentLog && !string.IsNullOrWhiteSpace(state.Args)
            ? $" args=[{state.Args}]"
            : "";
        var ex = exception is null
            ? ""
            : $" exception={exception.GetType().Name}: {exception.Message}";
        var skipped = state.Skipped ? " skipped=True" : "";
        FastFileSourceLog.Write(
            $"Injection profile method=\"{name}\" elapsed={elapsedMs:F3} ms thread={state.ThreadId} count={snapshot.Count} avg={snapshot.AverageMs:F3} ms max={snapshot.MaxMs:F3} ms{skipped}{argText}{ex}");
    }

    private static IEnumerable<TargetMethod> ResolveTargets(FastFileSourceSettings settings, bool psdHookOnly)
    {
        if (psdHookOnly)
        {
            yield return Required("psd-tachie-update", "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource, YukkuriMovieMaker.Plugin.Tachie.Psd", "Update", isPublic: true, parameterCount: 1);
            yield return Required("psd-tachie-dispose", "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource, YukkuriMovieMaker.Plugin.Tachie.Psd", "Dispose", isPublic: true, parameterCount: 0);
            yield break;
        }

        yield return Required("render", "YukkuriMovieMaker.VideoFileWriter.VideoFileWriter, YukkuriMovieMaker", "Render", isPublic: false, parameterCount: 4);
        yield return Required("video-timeline-update", "YukkuriMovieMaker.Player.Video.TimelineSource, YukkuriMovieMaker", "Update", isPublic: true, parameterCount: 2);
        yield return Required("video-update-resources", "YukkuriMovieMaker.Player.Video.TimelineSource, YukkuriMovieMaker", "UpdateResources", isPublic: false, parameterCount: 1);
        yield return Required("video-draw-resource", "YukkuriMovieMaker.Player.Video.TimelineSource, YukkuriMovieMaker", "DrawResource", isPublic: false, parameterCount: 2);
        yield return Required("effected-item-update", "YukkuriMovieMaker.Player.Video.EffectedItemSource, YukkuriMovieMaker", "Update", isPublic: true, parameterCount: 1);
        yield return Required("effected-source-output-update", "YukkuriMovieMaker.Player.Video.EffectedSourceOutput, YukkuriMovieMaker", "Update", isPublic: true, parameterCount: 5);
        yield return Required("tachie-update", "YukkuriMovieMaker.Player.Video.Items.TachieSource, YukkuriMovieMaker", "Update", isPublic: true, parameterCount: 1);
        yield return Required("audio-timeline-read", "YukkuriMovieMaker.Player.Audio.TimelineSource, YukkuriMovieMaker", "read", isPublic: false, parameterCount: 3);
        yield return Required("audio-open-close", "YukkuriMovieMaker.Player.Audio.TimelineSource, YukkuriMovieMaker", "OpenCloseResources", isPublic: false, parameterCount: 2);
        yield return Required("audio-read-resources", "YukkuriMovieMaker.Player.Audio.TimelineSource, YukkuriMovieMaker", "ReadResources", isPublic: false, parameterCount: 3);
        yield return Required("psd-tachie-update", "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource, YukkuriMovieMaker.Plugin.Tachie.Psd", "Update", isPublic: true, parameterCount: 1);
        yield return Required("psd-tachie-dispose", "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource, YukkuriMovieMaker.Plugin.Tachie.Psd", "Dispose", isPublic: true, parameterCount: 0);

        var psdFileSettingsLoad = Optional("psd-settings-load", "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdFileSettings, YukkuriMovieMaker.Plugin.Tachie.Psd", "LoadFromPsdFilePath", isPublic: true, parameterCount: 1);
        if (psdFileSettingsLoad is not null)
            yield return psdFileSettingsLoad;

        var psdFolderParse = Optional("psd-folder-parse", "YukkuriMovieMaker.Plugin.FileSource.Psd.PsdFolder, YukkuriMovieMaker.Plugin.FileSource.Psd", "Parse", isPublic: true, parameterCount: 1);
        if (psdFolderParse is not null)
            yield return psdFolderParse;

        var psdLayerImageRead = Optional("psd-layer-image-read", "PsdParser.LayerImage, PsdParser", "Read", isPublic: true, parameterCount: 0);
        if (psdLayerImageRead is not null)
            yield return psdLayerImageRead;

        var openProject = Optional("project-open", "YukkuriMovieMaker.Project.MainModel, YukkuriMovieMaker", "OpenProjectAsync", isPublic: true, parameterCount: 3);
        if (openProject is not null)
            yield return openProject;

        var timelineToolInfo = Optional("timeline-tool-info", "YukkuriMovieMaker.ViewModels.ToolAreaViewModel, YukkuriMovieMaker", "SetTimelineToolInfo", isPublic: true, parameterCount: 1);
        if (timelineToolInfo is not null)
            yield return timelineToolInfo;
    }

    private static TargetMethod Required(string name, string typeName, string methodName, bool isPublic, int parameterCount)
    {
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is null)
            throw new InvalidOperationException($"Target type missing: {typeName}");

        var flags = BindingFlags.Instance | BindingFlags.Static | (isPublic ? BindingFlags.Public : BindingFlags.NonPublic);
        var candidates = type
            .GetMethods(flags)
            .Where(m => m.Name == methodName)
            .ToArray();
        var method = candidates.FirstOrDefault(m => m.GetParameters().Length == parameterCount);
        if (method is null && candidates.Length > 0)
        {
            method = candidates
                .OrderBy(m => Math.Abs(m.GetParameters().Length - parameterCount))
                .ThenByDescending(m => m.GetParameters().Length)
                .First();
            FastFileSourceLog.Write(
                $"Injection profiler compatible target name={name} expectedParams={parameterCount} actualParams={method.GetParameters().Length} method=\"{FormatMethod(method)}\"");
        }

        if (method is null)
            throw new InvalidOperationException($"Target method missing: {typeName}.{methodName}/{parameterCount}");

        return new TargetMethod(name, method);
    }

    private static TargetMethod? Optional(string name, string typeName, string methodName, bool isPublic, int parameterCount)
    {
        try
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type is null)
            {
                FastFileSourceLog.Write($"Injection profiler optional type missing name={name} type={typeName}");
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Static | (isPublic ? BindingFlags.Public : BindingFlags.NonPublic);
            var method = type
                .GetMethods(flags)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == parameterCount);
            if (method is null)
            {
                FastFileSourceLog.Write($"Injection profiler optional method missing name={name} method={typeName}.{methodName}/{parameterCount}");
                return null;
            }

            return new TargetMethod(name, method);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"Injection profiler optional resolve failed name={name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static Assembly? LoadHarmonyAssembly()
    {
        var paths = GetHarmonyCandidatePaths().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var assembly = Assembly.LoadFrom(path);
                FastFileSourceLog.Write($"Injection profiler Harmony loaded path=\"{path}\"");
                return assembly;
            }
            catch (Exception ex)
            {
                FastFileSourceLog.Write($"Injection profiler Harmony load failed: {ex.GetType().Name}: {ex.Message} path=\"{path}\"");
            }
        }

        FastFileSourceLog.Write($"Injection profiler Harmony searched paths={string.Join(";", paths)}");
        return null;
    }

    private static IEnumerable<string> GetHarmonyCandidatePaths()
    {
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(pluginDir))
            yield return Path.Combine(pluginDir, "0Harmony.dll");

        yield return Path.Combine(AppContext.BaseDirectory, "0Harmony.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "user", "plugin", "RadeonFastFileSourcePlugin", "0Harmony.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "user", "plugin", "RadeonFastFileSourcePlugin", "lib", "0Harmony.dll");

        var baseParent = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(baseParent))
            yield return Path.Combine(baseParent, "0Harmony.dll");

        yield return Path.Combine(AppContext.BaseDirectory, "..", "0Harmony.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "一時ファイル", "0Harmony.dll");
    }

    private static object CreateHarmonyMethod(Type harmonyMethodType, string methodName)
    {
        var method = typeof(InternalInjectionProfiler).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(InternalInjectionProfiler), methodName);
        return Activator.CreateInstance(harmonyMethodType, method)
            ?? throw new InvalidOperationException($"HarmonyMethod create failed: {methodName}");
    }

    private static MethodInfo? FindPatchMethod(Type harmonyType)
    {
        return harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Patch")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 5 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
            });
    }

    private static string FormatMethod(MethodBase method)
    {
        var declaring = method.DeclaringType?.FullName ?? "<unknown>";
        return $"{declaring}.{method.Name}";
    }

    private static string SummarizeArgs(object[]? args)
    {
        if (args is null || args.Length == 0)
            return "";

        return string.Join(", ", args.Select((arg, index) => $"{index}:{SummarizeArg(arg)}"));
    }

    private static string SummarizeArg(object? arg)
    {
        if (arg is null)
            return "null";
        if (arg is int or long or float or double or bool or string or TimeSpan)
            return arg.ToString() ?? "";

        var type = arg.GetType();
        if (type.FullName?.Contains("FrameTime", StringComparison.OrdinalIgnoreCase) == true)
            return arg.ToString() ?? type.Name;
        if (type.FullName?.Contains("TimelineItemSourceDescription", StringComparison.OrdinalIgnoreCase) == true)
            return type.Name;

        return type.Name;
    }

    private static Exception Unwrap(Exception ex)
    {
        return ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
    }

    public readonly record struct ProfileState(long StartTimestamp, int ThreadId, string Args, bool Skipped, PsdStateCache.PsdCacheDecision PsdDecision);

    private sealed record TargetMethod(string Name, MethodBase Method);

    private sealed class MethodStats
    {
        private readonly object gate = new();
        private long count;
        private double totalMs;
        private double maxMs;

        public Snapshot Add(double elapsedMs)
        {
            lock (gate)
            {
                count++;
                totalMs += elapsedMs;
                if (elapsedMs > maxMs)
                    maxMs = elapsedMs;

                return new Snapshot(count, totalMs / count, maxMs);
            }
        }
    }

    private readonly record struct Snapshot(long Count, double AverageMs, double MaxMs);
}
