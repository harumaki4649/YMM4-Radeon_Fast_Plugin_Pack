using System.Reflection;

namespace RadeonFastFileSourcePlugin;

internal static class PsdInternalApiProbe
{
    private static readonly object Gate = new();
    private static bool started;

    public static void RunOnce(string reason)
    {
        lock (Gate)
        {
            if (started)
                return;

            started = true;
        }

        FastFileSourceSettings settings;
        try
        {
            settings = FastFileSourceSettingsStore.Current;
            if (!settings.EnablePsdInternalApiProbe)
                return;
        }
        catch
        {
            return;
        }

        _ = Task.Run(() => Probe(reason, settings));
    }

    private static void Probe(string reason, FastFileSourceSettings settings)
    {
        try
        {
            FastFileSourceLog.Write($"PSD internal probe start reason={reason}");
            ProbeNamedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieSource");
            ProbeNamedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachiePlugin");
            ProbeNamedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieItemParameter");
            ProbeNamedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieFaceParameter");
            ProbePsdAssemblies(settings.PsdInternalApiProbeMaxTypes);
            FastFileSourceLog.Write("PSD internal probe done");
        }
        catch (Exception ex)
        {
            FastFileSourceLog.Write($"PSD internal probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProbePsdAssemblies(int maxTypes)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly =>
            {
                var name = assembly.GetName().Name ?? "";
                return name.Contains("Psd", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Tachie", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FastFileSourceLog.Write($"PSD internal probe assemblies count={assemblies.Count} names=\"{string.Join(", ", assemblies.Select(x => x.GetName().Name))}\"");

        var logged = 0;
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(x => x is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types
                         .Where(IsInterestingType)
                         .OrderBy(type => type.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (logged++ >= maxTypes)
                    return;

                ProbeType(type, includeDetails: false);
            }
        }
    }

    private static void ProbeNamedType(string fullName)
    {
        var type = ResolveType(fullName);
        if (type is null)
        {
            FastFileSourceLog.Write($"PSD internal probe type missing type={fullName}");
            return;
        }

        ProbeType(type, includeDetails: true);
    }

    private static Type? ResolveType(string fullName)
    {
        return Type.GetType($"{fullName}, YukkuriMovieMaker.Plugin.Tachie.Psd", throwOnError: false) ??
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type is not null);
    }

    private static bool IsInterestingType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Contains("Psd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Layer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tachie", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cache", StringComparison.OrdinalIgnoreCase);
    }

    private static void ProbeType(Type type, bool includeDetails)
    {
        FastFileSourceLog.Write(
            $"PSD internal type type={type.FullName} assembly={type.Assembly.GetName().Name} visibility={Visibility(type)} base={type.BaseType?.FullName ?? "<none>"}");

        LogConstructors(type);
        LogProperties(type, includeDetails ? 80 : 16);
        LogFields(type, includeDetails ? 80 : 16);
        LogMethods(type, includeDetails ? 80 : 24);
    }

    private static void LogConstructors(Type type)
    {
        foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            FastFileSourceLog.Write(
                $"PSD internal ctor type={type.FullName} visibility={Visibility(ctor)} params=({FormatParameters(ctor)})");
        }
    }

    private static void LogProperties(Type type, int max)
    {
        foreach (var property in type
                     .GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(max))
        {
            var getter = property.GetGetMethod(nonPublic: true);
            var setter = property.GetSetMethod(nonPublic: true);
            FastFileSourceLog.Write(
                $"PSD internal property type={type.FullName} name={property.Name} propertyType={FormatType(property.PropertyType)} get={Visibility(getter)} set={Visibility(setter)}");
        }
    }

    private static void LogFields(Type type, int max)
    {
        foreach (var field in type
                     .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(max))
        {
            var lifetime = field.IsStatic ? "static" : "instance";
            FastFileSourceLog.Write(
                $"PSD internal field type={type.FullName} name={field.Name} fieldType={FormatType(field.FieldType)} visibility={Visibility(field)} lifetime={lifetime}");
        }
    }

    private static void LogMethods(Type type, int max)
    {
        foreach (var method in type
                     .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(IsInterestingMethod)
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.GetParameters().Length)
                     .Take(max))
        {
            var lifetime = method.IsStatic ? "static" : "instance";
            FastFileSourceLog.Write(
                $"PSD internal method type={type.FullName} name={method.Name} return={FormatType(method.ReturnType)} visibility={Visibility(method)} lifetime={lifetime} params=({FormatParameters(method)})");
        }
    }

    private static bool IsInterestingMethod(MethodInfo method)
    {
        if (method.IsSpecialName)
            return false;

        var name = method.Name;
        return name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Load", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Read", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cache", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Parse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Draw", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Dispose", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatParameters(MethodBase method)
    {
        return string.Join(", ", method.GetParameters().Select(parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}"));
    }

    private static string FormatType(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];

        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FormatType))}>";
    }

    private static string Visibility(Type type)
    {
        if (type.IsPublic || type.IsNestedPublic)
            return "public";
        if (type.IsNestedFamily)
            return "protected";
        if (type.IsNestedAssembly)
            return "internal";
        if (type.IsNestedPrivate)
            return "private";
        return "internal";
    }

    private static string Visibility(MethodBase? method)
    {
        if (method is null)
            return "none";
        if (method.IsPublic)
            return "public";
        if (method.IsFamily)
            return "protected";
        if (method.IsAssembly)
            return "internal";
        if (method.IsPrivate)
            return "private";
        return "nonpublic";
    }

    private static string Visibility(FieldInfo field)
    {
        if (field.IsPublic)
            return "public";
        if (field.IsFamily)
            return "protected";
        if (field.IsAssembly)
            return "internal";
        if (field.IsPrivate)
            return "private";
        return "nonpublic";
    }
}
