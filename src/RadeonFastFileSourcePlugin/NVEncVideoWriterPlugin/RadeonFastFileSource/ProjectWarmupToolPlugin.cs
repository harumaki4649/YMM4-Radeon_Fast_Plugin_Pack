using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using YukkuriMovieMaker.Plugin;

namespace RadeonFastFileSourcePlugin;

public sealed class RadeonProjectWarmupToolPlugin : IToolPlugin
{
    public string Name => "Radeon 解析/先読み";

    public Type ViewModelType => typeof(RadeonProjectWarmupToolViewModel);

    public Type ViewType => typeof(RadeonProjectWarmupToolView);

    public bool AllowMultipleInstances => false;
}

public sealed class RadeonProjectWarmupToolView : UserControl
{
    private readonly TextBlock statusTextBlock;
    private readonly TextBlock lastScanTextBlock;
    private readonly TextBlock settingsTextBlock;
    private readonly TextBlock cacheTextBlock;
    private readonly TextBlock logPathTextBlock;
    private readonly Button warmupButton;
    private readonly Button refreshButton;
    private RadeonProjectWarmupToolViewModel? currentViewModel;

    public RadeonProjectWarmupToolView()
    {
        var panel = new StackPanel
        {
            Margin = new System.Windows.Thickness(8),
            Orientation = Orientation.Vertical,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Radeon 解析/先読み",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        statusTextBlock = CreateTextBlock();
        lastScanTextBlock = CreateTextBlock();
        settingsTextBlock = CreateTextBlock();
        cacheTextBlock = CreateTextBlock();
        logPathTextBlock = CreateTextBlock();
        warmupButton = new Button
        {
            Content = "一括解析/先読み",
            Margin = new Thickness(0, 4, 0, 6),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        refreshButton = new Button
        {
            Content = "更新",
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        warmupButton.Click += (_, _) => currentViewModel?.RunWarmupNow();
        refreshButton.Click += (_, _) => currentViewModel?.RefreshStatus();

        panel.Children.Add(statusTextBlock);
        panel.Children.Add(lastScanTextBlock);
        panel.Children.Add(settingsTextBlock);
        panel.Children.Add(cacheTextBlock);
        panel.Children.Add(warmupButton);
        panel.Children.Add(refreshButton);
        panel.Children.Add(logPathTextBlock);

        Content = panel;
        DataContextChanged += OnDataContextChanged;
    }

    private static TextBlock CreateTextBlock()
    {
        return new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (currentViewModel is not null)
            currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        currentViewModel = e.NewValue as RadeonProjectWarmupToolViewModel;
        if (currentViewModel is not null)
            currentViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Refresh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        if (currentViewModel is null)
            return;

        statusTextBlock.Text = currentViewModel.StatusText;
        lastScanTextBlock.Text = currentViewModel.LastScanText;
        settingsTextBlock.Text = currentViewModel.SettingsText;
        cacheTextBlock.Text = currentViewModel.CacheText;
        logPathTextBlock.Text = currentViewModel.LogPathText;
    }
}

public sealed class RadeonProjectWarmupToolViewModel : IToolViewModel, ITimelineToolViewModel
{
    private string lastSignature = string.Empty;
    private string statusText = "状態: 安定優先のため、プロジェクト先読みは現在OFFです。";
    private string lastScanText = "最終解析: まだタイムライン情報を受け取っていません。";
    private string settingsText = CreateSettingsText();
    private string cacheText = PsdStateCache.CreateStatusText();
    private string logPathText = $"ログ: {Path.Combine(AppContext.BaseDirectory, "user", "log", "radeon_fast_filesource_log.txt")}";
    private TimelineToolInfo? latestTimelineInfo;

    public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanSuspend => true;

    public string Title => "Radeon 解析/先読み";

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value, nameof(StatusText));
    }

    public string LastScanText
    {
        get => lastScanText;
        private set => SetProperty(ref lastScanText, value, nameof(LastScanText));
    }

    public string SettingsText
    {
        get => settingsText;
        private set => SetProperty(ref settingsText, value, nameof(SettingsText));
    }

    public string CacheText
    {
        get => cacheText;
        private set => SetProperty(ref cacheText, value, nameof(CacheText));
    }

    public string LogPathText
    {
        get => logPathText;
        private set => SetProperty(ref logPathText, value, nameof(LogPathText));
    }

    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        latestTimelineInfo = info;
        var settings = FastFileSourceSettingsStore.Current;
        SettingsText = CreateSettingsText(settings);
        CacheText = CreateCacheText();

        if (!settings.EnableTimelineToolWarmup)
        {
            StatusText = "状態: 解析ツールは読み込まれています。先読みは安定優先でOFFです。";
            LastScanText = $"受信中: {info.Timeline.Name} / アイテム {info.Timeline.Items.Count} / 長さ {info.Timeline.Length}";
            return;
        }

        try
        {
            var signature = $"{info.Timeline.ID}|{info.Timeline.Items.Count}|{info.Timeline.Length}|{info.Scenes.Timelines.Count}";
            if (signature == lastSignature)
                return;

            lastSignature = signature;
            FastFileSourceLog.Write(
                $"Timeline tool info received timeline=\"{info.Timeline.Name}\" id={info.Timeline.ID} items={info.Timeline.Items.Count} length={info.Timeline.Length} scenes={info.Scenes.Timelines.Count}");

            ProjectWarmupAnalyzer.Scan(info);
            PsdStateManifest.LogCandidatesAndQueueWarmup("timeline-tool");
            StatusText = "状態: タイムライン解析を実行しました。";
            LastScanText = $"最終解析: {info.Timeline.Name} / アイテム {info.Timeline.Items.Count} / 長さ {info.Timeline.Length}";
            InternalApiProbe.RunOnce();
            PsdInternalApiProbe.RunOnce("timeline-tool");
            CacheText = CreateCacheText();
        }
        catch (Exception ex)
        {
            StatusText = $"状態: 解析でエラーが出ました。{ex.GetType().Name}: {ex.Message}";
            FastFileSourceLog.Write($"Timeline tool scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void RunWarmupNow()
    {
        if (latestTimelineInfo is null)
        {
            StatusText = "状態: まだタイムライン情報がありません。";
            CacheText = CreateCacheText();
            return;
        }

        try
        {
            StatusText = "状態: 一括解析/先読みを実行中です。";
            FastFileSourceLog.Write("Manual project warmup requested from Radeon tool");
            ProjectWarmupAnalyzer.Scan(latestTimelineInfo);
            PsdStateManifest.LogCandidatesAndQueueWarmup("manual-tool");
            PsdInternalApiProbe.RunOnce("manual-tool");
            LastScanText = $"手動解析: {latestTimelineInfo.Timeline.Name} / アイテム {latestTimelineInfo.Timeline.Items.Count} / 長さ {latestTimelineInfo.Timeline.Length}";
            StatusText = "状態: 一括解析/先読みを実行しました。";
        }
        catch (Exception ex)
        {
            StatusText = $"状態: 一括解析でエラーが出ました。{ex.GetType().Name}: {ex.Message}";
            FastFileSourceLog.Write($"Manual project warmup failed: {ex.GetType().Name}: {ex.Message}");
        }

        CacheText = CreateCacheText();
    }

    public void RefreshStatus()
    {
        SettingsText = CreateSettingsText(FastFileSourceSettingsStore.Current);
        CacheText = CreateCacheText();
        if (latestTimelineInfo is not null)
            LastScanText = $"受信中: {latestTimelineInfo.Timeline.Name} / アイテム {latestTimelineInfo.Timeline.Items.Count} / 長さ {latestTimelineInfo.Timeline.Length}";
    }

    public ToolState SaveState()
    {
        return new ToolState
        {
            Title = Title,
            SavedState = string.Empty,
        };
    }

    public void LoadState(ToolState stateData)
    {
    }

    private static string CreateSettingsText()
    {
        return CreateSettingsText(FastFileSourceSettingsStore.Current);
    }

    private static string CreateSettingsText(FastFileSourceSettings settings)
    {
        return $"設定: 動画ソースキャッシュ={OnOff(settings.EnableVideoSourceCache)} / 一時音声ネイティブ={OnOff(settings.EnableNativeTempAudioDecoder)} / 先読み={OnOff(settings.EnableProjectWarmup)} / PSD記録={OnOff(settings.EnablePsdStateManifest)}";
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string CreateCacheText()
    {
        return $"{WarmupManager.GetImageWarmupStatusText()} / {PsdStateCache.CreateStatusText()}";
    }

    private void SetProperty(ref string field, string value, string propertyName)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SuppressUnusedEventWarning()
    {
        CreateNewToolViewRequested?.Invoke(this, new CreateNewToolViewRequestedEventArgs(SaveState()));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    }
}

internal static class ProjectWarmupAnalyzer
{
    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> AutoScanSignatures = new(StringComparer.OrdinalIgnoreCase);

    public static void TryScanFromInjection(string methodName, object[]? args)
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

        if (!settings.EnableProjectWarmup || !settings.EnableAutoProjectWarmup || args is null || args.Length == 0)
            return;

        if (!IsAutoWarmupMethod(methodName))
            return;

        foreach (var arg in args)
        {
            if (arg is null)
                continue;

            TryScanObject(arg, methodName);
        }
    }

    public static void Scan(TimelineToolInfo info)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var fileCount = 0;
        var itemCount = 0;
        var sceneCount = 0;

        foreach (var scene in info.Scenes.AllScenes)
        {
            sceneCount++;
            fileCount += ScanTimeline(scene.Timeline, $"scene:{scene.Name}");
        }

        fileCount += ScanTimeline(info.Timeline, $"active:{info.Timeline.Name}");
        itemCount += info.Scenes.AllScenes.Sum(scene => scene.Timeline.Items.Count);

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        FastFileSourceLog.Write($"Project warmup scan done scenes={sceneCount} items={itemCount} files={fileCount} elapsed={elapsedMs:F3} ms");
    }

    private static bool TryScanObject(object value, string reason)
    {
        if (value is TimelineToolInfo info)
        {
            var timelineSignature = CreateTimelineToolSignature(info);
            if (!AutoScanSignatures.TryAdd(timelineSignature, 0))
                return false;

            Task.Run(() =>
            {
                try
                {
                    FastFileSourceLog.Write($"Auto project warmup start reason=\"{reason}\" source=TimelineToolInfo signature=\"{timelineSignature}\"");
                    Scan(info);
                }
                catch (Exception ex)
                {
                    FastFileSourceLog.Write($"Auto project warmup failed source=TimelineToolInfo: {ex.GetType().Name}: {ex.Message}");
                }
            });
            return true;
        }

        var typeName = value.GetType().FullName ?? value.GetType().Name;
        if (!typeName.Equals("YukkuriMovieMaker.Project.Project", StringComparison.Ordinal) &&
            !typeName.Equals("YukkuriMovieMaker.Project.Scenes", StringComparison.Ordinal) &&
            !typeName.Equals("YukkuriMovieMaker.Project.Timeline", StringComparison.Ordinal))
        {
            return false;
        }

        var signature = CreateObjectSignature(value);
        if (!AutoScanSignatures.TryAdd(signature, 0))
            return false;

        Task.Run(() =>
        {
            try
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var files = ScanObjectGraph(value, $"auto:{typeName}");
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                FastFileSourceLog.Write($"Auto project warmup done reason=\"{reason}\" source={typeName} files={files} elapsed={elapsedMs:F3} ms signature=\"{signature}\"");
            }
            catch (Exception ex)
            {
                FastFileSourceLog.Write($"Auto project warmup failed source={typeName}: {ex.GetType().Name}: {ex.Message}");
            }
        });
        return true;
    }

    private static int ScanObjectGraph(object value, string scope)
    {
        var typeName = value.GetType().FullName ?? value.GetType().Name;
        if (typeName.Equals("YukkuriMovieMaker.Project.Timeline", StringComparison.Ordinal))
            return ScanTimeline((YukkuriMovieMaker.Project.Timeline)value, scope);

        var files = 0;
        var scenes = GetPropertyValue(value, "Scenes");
        if (scenes is not null && !ReferenceEquals(scenes, value))
            files += ScanObjectGraph(scenes, scope);

        var timeline = GetPropertyValue(value, "Timeline");
        if (timeline is YukkuriMovieMaker.Project.Timeline typedTimeline)
            files += ScanTimeline(typedTimeline, scope);

        var allScenes = GetPropertyValue(value, "AllScenes") as System.Collections.IEnumerable;
        if (allScenes is not null)
        {
            foreach (var scene in allScenes)
            {
                var sceneTimeline = GetPropertyValue(scene, "Timeline");
                if (sceneTimeline is YukkuriMovieMaker.Project.Timeline sceneTypedTimeline)
                    files += ScanTimeline(sceneTypedTimeline, $"{scope}:scene");
            }
        }

        return files;
    }

    private static object? GetPropertyValue(object value, string name)
    {
        try
        {
            var property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(value);
        }
        catch
        {
            return null;
        }
    }

    private static string CreateTimelineToolSignature(TimelineToolInfo info)
    {
        return $"timeline-info|{info.Timeline.ID}|{info.Timeline.Items.Count}|{info.Timeline.Length}|{info.Scenes.Timelines.Count}";
    }

    private static string CreateObjectSignature(object value)
    {
        var typeName = value.GetType().FullName ?? value.GetType().Name;
        var scenes = GetPropertyValue(value, "Scenes");
        var timeline = GetPropertyValue(value, "Timeline");
        if (timeline is YukkuriMovieMaker.Project.Timeline typedTimeline)
            return $"{typeName}|timeline|{typedTimeline.ID}|{typedTimeline.Items.Count}|{typedTimeline.Length}";

        if (value is YukkuriMovieMaker.Project.Timeline directTimeline)
            return $"{typeName}|timeline|{directTimeline.ID}|{directTimeline.Items.Count}|{directTimeline.Length}";

        var scenesHash = scenes is null ? RuntimeHelpers.GetHashCode(value) : RuntimeHelpers.GetHashCode(scenes);
        var filePath = GetPropertyValue(value, "FilePath") as string ?? "";
        return $"{typeName}|scenes|{scenesHash}|{filePath}";
    }

    private static bool IsAutoWarmupMethod(string methodName)
    {
        return methodName.Contains("MainModel.OpenProjectAsync", StringComparison.OrdinalIgnoreCase) ||
            methodName.Contains("ToolAreaViewModel.SetTimelineToolInfo", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScanTimeline(YukkuriMovieMaker.Project.Timeline timeline, string scope)
    {
        var fileCount = 0;
        foreach (var item in timeline.Items)
        {
            foreach (var path in GetItemFiles(item))
            {
                if (RecordPath(path, item.GetType().Name, scope))
                    fileCount++;
            }
        }

        return fileCount;
    }

    private static IEnumerable<string> GetItemFiles(object item)
    {
        foreach (var path in GetStringEnumerable(item, "GetFiles"))
            yield return path;

        foreach (var path in GetFilePathProperties(item))
            yield return path;
    }

    private static IEnumerable<string> GetStringEnumerable(object item, string methodName)
    {
        MethodInfo? method;
        try
        {
            method = item.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        }
        catch
        {
            yield break;
        }

        if (method is null || method.GetParameters().Length != 0)
            yield break;

        object? result;
        try
        {
            result = method.Invoke(item, null);
        }
        catch (Exception ex)
        {
            FastFileSourceLog.WriteDetailed($"Project warmup {methodName} failed item={item.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            yield break;
        }

        if (result is not IEnumerable<string> paths)
            yield break;

        foreach (var path in paths)
            yield return path;
    }

    private static IEnumerable<string> GetFilePathProperties(object item)
    {
        PropertyInfo[] properties;
        try
        {
            properties = item.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        }
        catch
        {
            yield break;
        }

        foreach (var property in properties)
        {
            if (property.PropertyType != typeof(string))
                continue;

            if (!property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Contains("File", StringComparison.OrdinalIgnoreCase))
                continue;

            string? value;
            try
            {
                value = property.GetValue(item) as string;
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static bool RecordPath(string path, string itemType, string scope)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!File.Exists(fullPath))
            return false;

        var type = Classify(fullPath);
        if (type is null)
            return false;

        var key = $"{type}|{fullPath}";
        if (!Seen.TryAdd(key, 0))
            return false;

        WarmupManager.Record(type, fullPath);
        if (type == "image" && Path.GetExtension(fullPath).Equals(".psd", StringComparison.OrdinalIgnoreCase))
            PsdParallelPreloadCache.QueueFile(fullPath, null, null, $"project-warmup:{scope}");
        FastFileSourceLog.Write($"Project warmup record type={type} item={itemType} scope={scope} path=\"{fullPath}\"");
        return true;
    }

    private static string? Classify(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".aac" or ".flac" or ".m4a" or ".mp3" or ".ogg" or ".opus" or ".wav" => "audio",
            ".avif" or ".bmp" or ".gif" or ".jfif" or ".jpeg" or ".jpg" or ".jxl" or ".png" or ".psd" or ".svg" or ".tif" or ".tiff" or ".webp" => "image",
            ".avi" or ".m2ts" or ".m4v" or ".mkv" or ".mov" or ".mp4" or ".mpeg" or ".mpg" or ".mts" or ".ts" or ".webm" or ".wmv" => "video",
            _ => null,
        };
    }
}

internal static class InternalApiProbe
{
    private static bool logged;

    public static void RunOnce()
    {
        if (logged || !FastFileSourceSettingsStore.Current.EnableInternalApiProbe)
            return;

        logged = true;
        Probe("YukkuriMovieMaker.VideoFileWriter.VideoFileWriter", "CreateFileAsync", "Render");
        Probe("YukkuriMovieMaker.Player.Video.TimelineSource", "Update", "UpdateResources", "DrawResource");
        Probe("YukkuriMovieMaker.Player.Audio.TimelineSource", "read", "OpenCloseResources", "ReadResources");
        Probe("YukkuriMovieMaker.Player.Video.Items.TachieSource", "Update", "GetMouthShape", "GetVolume");
        Probe("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource", "Update");
    }

    private static void Probe(string typeName, params string[] methodNames)
    {
        var type = Type.GetType($"{typeName}, YukkuriMovieMaker") ??
                   Type.GetType($"{typeName}, YukkuriMovieMaker.Plugin.Tachie.Psd") ??
                   AppDomain.CurrentDomain.GetAssemblies()
                       .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                       .FirstOrDefault(type => type is not null);

        if (type is null)
        {
            FastFileSourceLog.Write($"Internal probe type missing type={typeName}");
            return;
        }

        FastFileSourceLog.Write($"Internal probe type found type={type.FullName} assembly={type.Assembly.GetName().Name}");
        foreach (var methodName in methodNames)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == methodName)
                .ToArray();
            if (methods.Length == 0)
            {
                FastFileSourceLog.Write($"Internal probe method missing type={type.FullName} method={methodName}");
                continue;
            }

            foreach (var method in methods)
            {
                var parameters = string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
                FastFileSourceLog.Write($"Internal probe method found type={type.FullName} method={method.Name} visibility={Visibility(method)} return={method.ReturnType.Name} params=({parameters})");
            }
        }
    }

    private static string Visibility(MethodBase method)
    {
        if (method.IsPublic)
            return "public";
        if (method.IsPrivate)
            return "private";
        if (method.IsAssembly)
            return "internal";
        if (method.IsFamily)
            return "protected";
        return "nonpublic";
    }
}
