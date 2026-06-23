using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Tachie;
using YukkuriMovieMaker.Plugin.Tachie.Psd;

namespace RadeonFastPsdTachiePlugin;

public sealed class RadeonFastPsdTachiePlugin : ITachiePlugin
{
    private readonly PsdTachiePlugin inner = new();

    public string Name => "Radeon 高速PSD立ち絵";

    public bool HasScriptFile => inner.HasScriptFile;

    public ITachieCharacterParameter CreateCharacterParameter() => inner.CreateCharacterParameter();

    public ITachieItemParameter CreateItemParameter() => inner.CreateItemParameter();

    public ITachieFaceParameter CreateFaceParameter() => inner.CreateFaceParameter();

    public ITachieSource CreateTachieSource(IGraphicsDevicesAndContext devices)
    {
        return new RadeonFastPsdTachieSource(devices);
    }

    public void CreateScriptFile(string scriptFilePath) => inner.CreateScriptFile(scriptFilePath);

    public IEnumerable<ExoItem> CreateExoItems(
        int FPS,
        IEnumerable<TachieItemExoDescription> items,
        IEnumerable<TachieFaceItemExoDescription> faceItems,
        IEnumerable<TachieVoiceItemExoDescription> voiceItems)
        => inner.CreateExoItems(FPS, items, faceItems, voiceItems);
}

internal sealed class RadeonFastPsdTachieSource : ITachieSource2
{
    private readonly PsdTachieSource inner;
    private string? lastKey;
    private long updateCount;
    private long skippedCount;
    private long slowCount;
    private double totalUpdateMs;
    private double maxUpdateMs;

    public RadeonFastPsdTachieSource(IGraphicsDevicesAndContext devices)
    {
        inner = new PsdTachieSource(devices);
        FastPsdTachieLog.Write("Fast PSD tachie source created");
    }

    public ID2D1Image Output => inner.Output;

    public void Update(TachieSourceDescription desc)
    {
        updateCount++;

        // プレビュー/編集中は同一状態スキップを行わない。
        // 内部の PsdTachieSource.Update は元々アクティブレイヤー差分を自前で検出して
        // 重い D2D 再構築を避けるため、ここでのスキップによる高速化効果はわずかしかない。
        // 一方で編集・コピペ中はソースインスタンスが生かされたまま内部の描画リソースが
        // 作り替えられる/無効化されることがあり、lastKey が一致して inner.Update を
        // スキップすると、空または古い Output が表示され続ける不具合が起きる
        // （表情変化でキーが変わるか再起動するまで復帰しない）。
        // 連続描画されるエンコード時は顕在化しないため、スキップはエンコード時のみに限定する。
        if (desc.Usage == TimelineSourceUsage.Preview)
        {
            inner.Update(desc);
            lastKey = null;
            return;
        }

        var key = TachieStateKey.Create(desc);
        if (lastKey == key)
        {
            skippedCount++;
            if (skippedCount <= 5 || skippedCount % 300 == 0)
                FastPsdTachieLog.Write($"PSD tachie skip same-state updates={updateCount} skipped={skippedCount}");
            return;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        inner.Update(desc);
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        totalUpdateMs += elapsedMs;
        maxUpdateMs = Math.Max(maxUpdateMs, elapsedMs);
        lastKey = key;

        if (elapsedMs >= 3.0)
        {
            slowCount++;
            FastPsdTachieLog.Write(
                $"PSD tachie update slow count={updateCount} slow={slowCount} elapsed={elapsedMs:F3} ms keyHash={key.GetHashCode():X8} skipped={skippedCount}");
        }
        else if (updateCount == 1)
        {
            FastPsdTachieLog.Write($"PSD tachie first update elapsed={elapsedMs:F3} ms keyHash={key.GetHashCode():X8}");
        }
        else if (updateCount % 300 == 0)
        {
            var avg = updateCount - skippedCount > 0 ? totalUpdateMs / (updateCount - skippedCount) : 0;
            FastPsdTachieLog.Write(
                $"PSD tachie stats updates={updateCount} skipped={skippedCount} avg={avg:F3} ms max={maxUpdateMs:F3} ms slow={slowCount}");
        }
    }

    public void Dispose()
    {
        var measured = updateCount - skippedCount;
        var avg = measured > 0 ? totalUpdateMs / measured : 0;
        FastPsdTachieLog.Write(
            $"PSD tachie dispose updates={updateCount} skipped={skippedCount} avg={avg:F3} ms max={maxUpdateMs:F3} ms slow={slowCount}");
        inner.Dispose();
    }
}

internal static class TachieStateKey
{
    public static string Create(TachieSourceDescription desc)
    {
        var builder = new StringBuilder(512);
        builder.Append("mouth=").Append(desc.MouthShape).Append(';');
        builder.Append("voice=").Append(desc.VoiceVolume.ToString("F3", CultureInfo.InvariantCulture)).Append(';');
        AppendObject(builder, "character", desc.Tachie?.CharacterParameter);
        AppendObject(builder, "item", desc.Tachie?.ItemParameter);
        var layer = 0;
        if (desc.Tachie?.Faces is not null)
        {
            foreach (var face in desc.Tachie.Faces.OrderBy(f => f.Layer))
            {
                builder.Append("faceLayer=").Append(layer++).Append(':').Append(face.Layer).Append(';');
                AppendObject(builder, "face", face.FaceParameter);
            }
        }

        return builder.ToString();
    }

    private static void AppendObject(StringBuilder builder, string name, object? value)
    {
        if (value is null)
        {
            builder.Append(name).Append("=null;");
            return;
        }

        builder.Append(name).Append("Type=").Append(value.GetType().FullName).Append(';');
        foreach (var prop in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                continue;

            if (prop.Name == "HasErrors")
                continue;

            object? propValue;
            try
            {
                propValue = prop.GetValue(value);
            }
            catch
            {
                continue;
            }

            builder.Append(name).Append('.').Append(prop.Name).Append('=');
            AppendValue(builder, propValue);
            builder.Append(';');
        }
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                builder.Append(text);
                return;
            case IEnumerable enumerable when value is not string:
                builder.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first)
                        builder.Append(',');
                    AppendValue(builder, item);
                    first = false;
                }
                builder.Append(']');
                return;
            case IFormattable formattable:
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            default:
                builder.Append(value);
                return;
        }
    }
}

internal static class FastPsdTachieLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "user", "log", "radeon_fast_psd_tachie_log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never affect rendering.
        }
    }
}
