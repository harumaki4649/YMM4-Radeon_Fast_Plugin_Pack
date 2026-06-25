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

            // ネイティブDLLの依存関係(libvips, glib, gobject 等)を解決するため、
            // native ディレクトリを PATH と DLL 検索パスに追加する。
            // NativeLibrary.Load は既定ではDLLの依存先を同じディレクトリから探さないため、
            // AddDllDirectory と PATH の両方で明示的に登録する。
            AddNativeDirsToPath(nativeDirs);
            RegisterDllDirectories(nativeDirs);

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

        // UseDllDirectoryForDependencies を指定して、読み込んだDLLと同じ
        // ディレクトリから依存DLL(libvips, glib 等)を探索させる。
        var effectiveSearchPath = searchPath
            | DllImportSearchPath.UseDllDirectoryForDependencies
            | DllImportSearchPath.AssemblyDirectory;

        foreach (var dir in GetNativeDirectories(assembly))
        {
            var path = Path.Combine(dir, normalizedName);
            if (File.Exists(path))
                return NativeLibrary.Load(path, assembly, effectiveSearchPath);
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

    private static void AddNativeDirsToPath(string[] nativeDirs)
    {
        // ネイティブDLLの依存関係が解決されるよう、PATH の先頭に追加する。
        // 既に含まれているディレクトリは重複追加しない。
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var existing = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.TrimEnd('\\').TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = nativeDirs
            .Where(d => !existing.Contains(d.TrimEnd('\\').TrimEnd('/')))
            .ToArray();

        if (toAdd.Length == 0)
            return;

        var newPath = string.Join(';', toAdd) + ";" + currentPath;
        Environment.SetEnvironmentVariable("PATH", newPath);
    }

    private static void RegisterDllDirectories(string[] nativeDirs)
    {
        // AddDllDirectory を使って DLL 検索パスを登録する。
        // これにより NativeLibrary.Load 時に依存DLL も native ディレクトリから
        // 解決されるようになる (LOAD_LIBRARY_SEARCH_USER_DIRS と同等の効果)。
        try
        {
            // SetDefaultDllDirectories で LOAD_LIBRARY_SEARCH_DEFAULT_DIRS を有効化し、
            // その上で AddDllDirectory でユーザーディレクトリを追加する。
            const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;
            if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
                return;

            foreach (var dir in nativeDirs)
            {
                var ptr = Marshal.StringToHGlobalUni(dir);
                try
                {
                    AddDllDirectory(ptr);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
        catch
        {
            // AddDllDirectory が失敗しても PATH 経由でフォールバック可能。
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddDllDirectory(IntPtr lpDirectoryName);
}
