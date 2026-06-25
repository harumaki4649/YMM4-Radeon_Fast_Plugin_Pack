using System.Reflection;
using System.Runtime.InteropServices;

namespace RadeonFastFileSourcePlugin;

internal static class NativeDllResolver
{
    private static int initialized;

    private static readonly HashSet<string> KnownNativeDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "AmfNative.dll",
        "RadeonFastNativeAudio.dll",
        "RadeonFastNativeImage.dll",
        "RadeonFastNativeVideo.dll",
    };

    public static string Initialize()
    {
        if (Interlocked.Exchange(ref initialized, 1) != 0)
            return "already-initialized";

        // .NET 10 で SetDllImportResolver が二重登録やタイミング不良で
        // InvalidOperationException を投げることがある。アセンブリ読み込み中
        // (ModuleInitializer 由来) の例外はホストクラッシュに直結するため、
        // リゾルバ登録の失敗は致命扱いせず既定の検索動作にフォールバックさせる。
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var nativeDirs = GetNativeDirectories(assembly)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ConfigureVipsModulePath(nativeDirs);
            NativeLibrary.SetDllImportResolver(assembly, ResolveNativeDll);

            return nativeDirs.Length == 0
                ? "no-native-dir"
                : string.Join(";", nativeDirs);
        }
        catch (Exception)
        {
            return "resolver-register-failed";
        }
    }

    private static IntPtr ResolveNativeDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var normalizedName = NormalizeDllName(libraryName);
        if (!KnownNativeDlls.Contains(normalizedName))
            return IntPtr.Zero;

        foreach (var dir in GetNativeDirectories(assembly))
        {
            var path = Path.Combine(dir, normalizedName);
            if (File.Exists(path))
                return NativeLibrary.Load(path, assembly, searchPath);
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> GetNativeDirectories(Assembly assembly)
    {
        var assemblyDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            yield return Path.Combine(assemblyDir, "native");
            yield return assemblyDir;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "user", "RadeonFastFileSourcePlugin", "native");
    }

    private static string NormalizeDllName(string libraryName)
    {
        var fileName = Path.GetFileName(libraryName);
        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".dll";
    }

    private static void ConfigureVipsModulePath(IEnumerable<string> nativeDirs)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIPS_MODULEDIR")))
            return;

        foreach (var dir in nativeDirs)
        {
            var moduleDir = Path.Combine(dir, "vips-modules-8.18");
            if (Directory.Exists(moduleDir))
            {
                Environment.SetEnvironmentVariable("VIPS_MODULEDIR", moduleDir);
                return;
            }
        }
    }
}
