using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Ymm4ApiScanner <ymm4Dir> <outputDir>");
    return 2;
}

var ymm4Dir = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDir);

var assemblyNames = new[]
{
    "YukkuriMovieMaker.dll",
    "YukkuriMovieMaker.Plugin.dll",
    "YukkuriMovieMaker.Settings.dll",
    "YukkuriMovieMaker.Plugin.FileSource.FFmpeg.dll",
    "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.dll",
    "YukkuriMovieMaker.Plugin.FileSource.WIC.dll",
    "YukkuriMovieMaker.Plugin.FileSource.Psd.dll",
    "YukkuriMovieMaker.Plugin.Tachie.Psd.dll",
    "YukkuriMovieMaker.Plugin.Community.dll",
};

var keywords = new[]
{
    "Project", "Scene", "Timeline", "MainViewModel", "CreateFileAsync", "VideoFileWriter",
    "FileSource", "Plugin", "Open", "Load", "Save", "Item", "Tachie", "Psd", "Cache",
    "Render", "Player", "Update", "Source", "Audio", "Video", "Bitmap", "Progress",
    "Export", "Write", "Setting", "Window", "ViewModel", "Service", "Manager", "Factory",
    "Event", "Changed", "Dispose", "Preload", "Frame", "Effect", "Resource", "Loop",
};

var detailPatterns = new[]
{
    "YukkuriMovieMaker.VideoFileWriter.VideoFileWriter",
    "YukkuriMovieMaker.ViewModels.MainViewModel",
    "YukkuriMovieMaker.Player.Video.TimelineSource",
    "YukkuriMovieMaker.Player.Audio.TimelineSource",
    "YukkuriMovieMaker.Project.Items.VideoItem",
    "YukkuriMovieMaker.Player.Video.EffectedItemSource",
    "YukkuriMovieMaker.Player.Audio.EffectedItemSource",
    "YukkuriMovieMaker.Commons.SafeTransform3DHelper",
    "FileSourceFactory",
    "VideoFileWriter",
    "TimelineSource",
    "MainViewModel",
    "Tachie",
    "Psd",
};

var all = new List<string>();
var hits = new SortedSet<string>(StringComparer.Ordinal);
var details = new List<string>();
var methodRecords = new List<MethodRecord>();
var callEdges = new List<CallEdge>();

foreach (var assemblyName in assemblyNames)
{
    var path = Path.Combine(ymm4Dir, assemblyName);
    if (!File.Exists(path))
        continue;

    using var stream = File.OpenRead(path);
    using var peReader = new PEReader(stream);
    if (!peReader.HasMetadata)
        continue;

    var reader = peReader.GetMetadataReader();
    all.Add($"# Assembly {assemblyName}");

    foreach (var typeHandle in reader.TypeDefinitions)
    {
        var type = reader.GetTypeDefinition(typeHandle);
        var typeName = GetFullTypeName(reader, type);
        var typeVis = TypeVisibility(type.Attributes);
        var baseName = GetTypeReferenceName(reader, type.BaseType);
        var typeLine = $"TYPE\t{typeVis}\t{typeName}\tbase={baseName}";
        all.Add(typeLine);
        AddHitIfInteresting(assemblyName, typeLine);

        var typeDetails = IsDetailType(typeName);
        if (typeDetails)
        {
            details.Add("");
            details.Add($"# [{assemblyName}] {typeName} ({typeVis})");
            details.Add($"base: {baseName}");
        }

        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var methodName = reader.GetString(method.Name);
            var access = MethodVisibility(method.Attributes);
            var signature = DecodeMethodSignature(reader, method);
            var methodToken = MetadataTokens.GetToken(methodHandle);
            var methodFullName = $"{typeName}.{methodName}{signature}";
            var methodLine = $"  METHOD\t{access}\t{methodName}{signature}";
            all.Add(methodLine);
            methodRecords.Add(new MethodRecord(assemblyName, methodToken, typeName, methodName, signature, access));
            AddHitIfInteresting(assemblyName, $"{typeName} {methodLine}");
            if (typeDetails)
                details.Add(methodLine);

            foreach (var callee in DecodeCalls(peReader, reader, method))
                callEdges.Add(new CallEdge(assemblyName, methodToken, methodFullName, callee));
        }

        foreach (var propertyHandle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var propertyName = reader.GetString(property.Name);
            var propertyLine = $"  PROP\t{propertyName}";
            all.Add(propertyLine);
            AddHitIfInteresting(assemblyName, $"{typeName} {propertyLine}");
            if (typeDetails)
                details.Add(propertyLine);
        }

        foreach (var eventHandle in type.GetEvents())
        {
            var evt = reader.GetEventDefinition(eventHandle);
            var eventName = reader.GetString(evt.Name);
            var eventLine = $"  EVENT\t{eventName}";
            all.Add(eventLine);
            AddHitIfInteresting(assemblyName, $"{typeName} {eventLine}");
            if (typeDetails)
                details.Add(eventLine);
        }
    }
}

File.WriteAllLines(Path.Combine(outputDir, "ymm4_private_api_scan.txt"), all, new UTF8Encoding(false));
File.WriteAllLines(Path.Combine(outputDir, "ymm4_private_api_hits.txt"), hits, new UTF8Encoding(false));
File.WriteAllLines(Path.Combine(outputDir, "ymm4_private_api_details.txt"), details, new UTF8Encoding(false));
WriteFramePipelineReports(outputDir, methodRecords, callEdges);

Console.WriteLine($"scan={Path.Combine(outputDir, "ymm4_private_api_scan.txt")}");
Console.WriteLine($"hits={Path.Combine(outputDir, "ymm4_private_api_hits.txt")}");
Console.WriteLine($"details={Path.Combine(outputDir, "ymm4_private_api_details.txt")}");
Console.WriteLine($"lines={all.Count}");
Console.WriteLine($"hits={hits.Count}");
Console.WriteLine($"details={details.Count}");
Console.WriteLine($"pipeline={Path.Combine(outputDir, "ymm4_frame_pipeline_summary.txt")}");
return 0;

void AddHitIfInteresting(string assemblyName, string line)
{
    if (keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
        hits.Add($"[{assemblyName}] {line}");
}

bool IsDetailType(string typeName) =>
    detailPatterns.Any(p => typeName.Contains(p, StringComparison.OrdinalIgnoreCase));

static string GetFullTypeName(MetadataReader reader, TypeDefinition type)
{
    var ns = reader.GetString(type.Namespace);
    var name = reader.GetString(type.Name);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}

static string GetTypeReferenceName(MetadataReader reader, EntityHandle handle)
{
    if (handle.IsNil)
        return "";

    return handle.Kind switch
    {
        HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
        HandleKind.TypeReference => GetTypeRefName(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
        HandleKind.TypeSpecification => "TypeSpec",
        _ => handle.Kind.ToString(),
    };
}

static string GetTypeRefName(MetadataReader reader, TypeReference typeRef)
{
    var ns = reader.GetString(typeRef.Namespace);
    var name = reader.GetString(typeRef.Name);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}

static string TypeVisibility(TypeAttributes attrs)
{
    return (attrs & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
        TypeAttributes.NotPublic or TypeAttributes.NestedAssembly => "internal",
        TypeAttributes.NestedPrivate => "private",
        TypeAttributes.NestedFamily => "protected",
        TypeAttributes.NestedFamORAssem => "protected internal",
        _ => "nonpublic",
    };
}

static string MethodVisibility(MethodAttributes attrs)
{
    return (attrs & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.Private => "private",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        _ => "nonpublic",
    };
}

static string DecodeMethodSignature(MetadataReader reader, MethodDefinition method)
{
    try
    {
        var provider = new SignatureNameProvider(reader);
        var sig = method.DecodeSignature(provider, genericContext: null);
        var parameterNames = method.GetParameters()
            .Select(h => reader.GetParameter(h))
            .Where(p => p.SequenceNumber > 0)
            .OrderBy(p => p.SequenceNumber)
            .Select(p => reader.GetString(p.Name))
            .ToArray();

        var args = sig.ParameterTypes.Select((type, index) =>
        {
            var name = index < parameterNames.Length && !string.IsNullOrWhiteSpace(parameterNames[index])
                ? $" {parameterNames[index]}"
                : "";
            return $"{type}{name}";
        });
        return $"({string.Join(", ", args)}) : {sig.ReturnType}";
    }
    catch
    {
        return "()";
    }
}

static IEnumerable<string> DecodeCalls(PEReader peReader, MetadataReader reader, MethodDefinition method)
{
    if (method.RelativeVirtualAddress == 0)
        yield break;

    MethodBodyBlock body;
    try
    {
        body = peReader.GetMethodBody(method.RelativeVirtualAddress);
    }
    catch
    {
        yield break;
    }

    var il = body.GetILBytes();
    if (il is null || il.Length == 0)
        yield break;

    for (var offset = 0; offset < il.Length;)
    {
        var opcodeOffset = offset;
        var op = il[offset++];
        if (op == 0xFE)
        {
            if (offset >= il.Length)
                yield break;
            op = (byte)(0xFE00 | il[offset++]);
        }

        var operandSize = GetOperandSize(il, offset, op);
        if (operandSize < 0 || offset + operandSize > il.Length)
            yield break;

        if ((op == 0x28 || op == 0x6F || op == 0x73) && operandSize == 4 && offset + 4 <= il.Length)
        {
            var token = BitConverter.ToInt32(il, offset);
            var resolved = ResolveMemberName(reader, token);
            if (!string.IsNullOrWhiteSpace(resolved))
                yield return $"{opcodeOffset:X4} {OpcodeName(op)} {resolved}";
        }

        offset += operandSize;
    }
}

static int GetOperandSize(byte[] il, int operandOffset, int op)
{
    return op switch
    {
        0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D
            or 0x0E or 0x0F or 0x10 or 0x11 or 0x14 or 0x15 or 0x16 or 0x17 or 0x18 or 0x19 or 0x1A or 0x1B or 0x1C
            or 0x1D or 0x1E or 0x25 or 0x26 or 0x27 or 0x2A or 0x58 or 0x59 or 0x5A or 0x5B or 0x5C or 0x5D or 0x5E
            or 0x5F or 0x60 or 0x61 or 0x62 or 0x63 or 0x64 or 0x65 or 0x66 or 0x67 or 0x68 or 0x69 or 0x6A or 0x6B
            or 0x6C or 0x6D or 0x6E or 0x76 or 0x77 or 0x79 or 0x7A or 0xFE01 or 0xFE02 or 0xFE03 or 0xFE04
            or 0xFE05 or 0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E or 0xFE0F or 0xFE11 or 0xFE12
            or 0xFE13 or 0xFE14 or 0xFE15 or 0xFE16 or 0xFE17 or 0xFE18 or 0xFE19 or 0xFE1A or 0xFE1C => 0,

        0x12 or 0x13 or 0x1F or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x34
            or 0x35 or 0x36 or 0x37 => 1,

        0x20 or 0x21 or 0x22 or 0x23 or 0x28 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F or 0x40
            or 0x41 or 0x42 or 0x43 or 0x44 or 0x46 or 0x47 or 0x48 or 0x49 or 0x4A or 0x4B or 0x4C
            or 0x4D or 0x4E or 0x4F or 0x50 or 0x51 or 0x52 or 0x53 or 0x54 or 0x55 or 0x56 or 0x57 or 0x6F
            or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x78 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F
            or 0x80 or 0x81 or 0x82 or 0x83 or 0x8C or 0x8D or 0x8E or 0x8F or 0x90 or 0x91 or 0x92 or 0x93
            or 0x94 or 0x95 or 0x96 or 0x97 or 0x98 or 0x99 or 0x9A or 0x9B or 0x9C or 0x9D or 0x9E or 0x9F
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0xAD or 0xB3 or 0xB4 or 0xB5 or 0xB6 or 0xB7
            or 0xB8 or 0xD0 or 0xD1 or 0xD2 or 0xD3 or 0xD4 or 0xD5 or 0xD6 or 0xD7 or 0xD8 or 0xD9
            or 0xDA or 0xFE06 or 0xFE07 => 4,

        0x24 => 8,
        0xFE00 => 2,
        0xDD or 0xDE => 4,
        0x45 => operandOffset + 4 <= il.Length ? 4 + Math.Max(0, BitConverter.ToInt32(il, operandOffset)) * 4 : 0,
        _ => GuessOperandSize(op),
    };
}

static int GuessOperandSize(int op)
{
    if (op is >= 0x02 and <= 0x1E)
        return 0;
    if (op is >= 0x2B and <= 0x37)
        return 1;
    return op is 0x20 or 0x21 or 0x22 or 0x23 or 0x28 or 0x6F or 0x73 ? 4 : 0;
}

static string OpcodeName(int op) => op switch
{
    0x28 => "call",
    0x6F => "callvirt",
    0x73 => "newobj",
    _ => $"op_{op:X}",
};

static string ResolveMemberName(MetadataReader reader, int token)
{
    EntityHandle handle;
    try
    {
        handle = MetadataTokens.EntityHandle(token);
    }
    catch
    {
        return "";
    }

    try
    {
        return handle.Kind switch
        {
            HandleKind.MethodDefinition => ResolveMethodDefinitionName(reader, (MethodDefinitionHandle)handle),
            HandleKind.MemberReference => ResolveMemberReferenceName(reader, (MemberReferenceHandle)handle),
            HandleKind.MethodSpecification => ResolveMethodSpecificationName(reader, (MethodSpecificationHandle)handle),
            _ => "",
        };
    }
    catch
    {
        return "";
    }
}

static string ResolveMethodDefinitionName(MetadataReader reader, MethodDefinitionHandle handle)
{
    var method = reader.GetMethodDefinition(handle);
    var type = reader.GetTypeDefinition(method.GetDeclaringType());
    return $"{GetFullTypeName(reader, type)}.{reader.GetString(method.Name)}";
}

static string ResolveMemberReferenceName(MetadataReader reader, MemberReferenceHandle handle)
{
    var member = reader.GetMemberReference(handle);
    var parent = ResolveMemberParentName(reader, member.Parent);
    return string.IsNullOrWhiteSpace(parent)
        ? reader.GetString(member.Name)
        : $"{parent}.{reader.GetString(member.Name)}";
}

static string ResolveMethodSpecificationName(MetadataReader reader, MethodSpecificationHandle handle)
{
    var spec = reader.GetMethodSpecification(handle);
    var method = spec.Method;
    return method.Kind switch
    {
        HandleKind.MethodDefinition => ResolveMethodDefinitionName(reader, (MethodDefinitionHandle)method),
        HandleKind.MemberReference => ResolveMemberReferenceName(reader, (MemberReferenceHandle)method),
        _ => method.Kind.ToString(),
    };
}

static string ResolveMemberParentName(MetadataReader reader, EntityHandle parent)
{
    return parent.Kind switch
    {
        HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)parent)),
        HandleKind.TypeReference => GetTypeRefName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeSpecification => "TypeSpec",
        HandleKind.MethodDefinition => ResolveMethodDefinitionName(reader, (MethodDefinitionHandle)parent),
        _ => parent.Kind.ToString(),
    };
}

static void WriteFramePipelineReports(string outputDir, List<MethodRecord> methods, List<CallEdge> calls)
{
    var focusTerms = new[]
    {
        "VideoFileWriter.VideoFileWriter.CreateFileAsync",
        "VideoFileWriter.VideoFileWriter.Render",
        "Player.Video.TimelineSource.Update",
        "Player.Video.TimelineSource.UpdateResources",
        "Player.Video.TimelineSource.DrawResource",
        "Player.Audio.TimelineSource.read",
        "Player.Audio.TimelineSource.OpenCloseResources",
        "Player.Audio.TimelineSource.ReadResources",
        "TachieSource.Update",
        "PsdTachieSource.Update",
        "EffectedItemSource.Update",
        "EffectedSourceOutput.Update",
        "FFmpegD3D11VideoProcessor.ProcessFrame",
        "CrossDeviceFrameBridge.Transfer",
    };

    var summary = new List<string>
    {
        "# YMM4 frame pipeline static analysis",
        $"generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        "",
        "## Focus methods",
    };

    var focusMethods = methods
        .Where(m => focusTerms.Any(t => m.FullName.Contains(t, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(m => m.Assembly, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.TypeName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.MethodName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var method in focusMethods)
        summary.Add($"{method.Assembly}\t0x{method.Token:X8}\t{method.Access}\t{method.FullName}");

    summary.Add("");
    summary.Add("## Calls made by focus methods");
    foreach (var method in focusMethods)
    {
        summary.Add("");
        summary.Add($"### {method.FullName}");
        foreach (var edge in calls.Where(c => c.Assembly == method.Assembly && c.CallerToken == method.Token).Take(200))
            summary.Add($"  -> {edge.Callee}");
    }

    summary.Add("");
    summary.Add("## Callers touching focus terms");
    var callerHits = calls
        .Where(c => focusTerms.Any(t => c.Callee.Contains(t, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(c => c.Callee, StringComparer.OrdinalIgnoreCase)
        .ThenBy(c => c.Caller, StringComparer.OrdinalIgnoreCase)
        .ToList();
    foreach (var edge in callerHits)
        summary.Add($"{edge.Assembly}\t{edge.Caller}\t=>\t{edge.Callee}");

    var interestingCalls = calls
        .Where(c => IsFramePipelineInteresting(c.Caller) || IsFramePipelineInteresting(c.Callee))
        .OrderBy(c => c.Assembly, StringComparer.OrdinalIgnoreCase)
        .ThenBy(c => c.Caller, StringComparer.OrdinalIgnoreCase)
        .ToList();

    File.WriteAllLines(Path.Combine(outputDir, "ymm4_frame_pipeline_summary.txt"), summary, new UTF8Encoding(false));
    File.WriteAllLines(
        Path.Combine(outputDir, "ymm4_frame_pipeline_calls.txt"),
        interestingCalls.Select(c => $"{c.Assembly}\t{c.Caller}\t=>\t{c.Callee}"),
        new UTF8Encoding(false));
}

static bool IsFramePipelineInteresting(string value)
{
    var terms = new[]
    {
        "TimelineSource", "VideoFileWriter", "CreateFileAsync", "Render", "Update", "DrawResource",
        "ReadResources", "OpenCloseResources", "EffectedItemSource", "EffectedSourceOutput",
        "Tachie", "Psd", "Frame", "Bitmap", "Texture", "ProcessFrame", "CrossDeviceFrameBridge",
    };
    return terms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));
}

sealed record MethodRecord(string Assembly, int Token, string TypeName, string MethodName, string Signature, string Access)
{
    public string FullName => $"{TypeName}.{MethodName}{Signature}";
}

sealed record CallEdge(string Assembly, int CallerToken, string Caller, string Callee);

sealed class SignatureNameProvider(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[]";
    public string GetByReferenceType(string elementType) => $"{elementType}&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => $"{elementType}*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => $"{elementType}[]";
    public string GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataNames.GetFullTypeName(metadataReader, metadataReader.GetTypeDefinition(handle));
    public string GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataNames.GetTypeRefName(metadataReader, metadataReader.GetTypeReference(handle));
    public string GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}

static class MetadataNames
{
    public static string GetFullTypeName(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public static string GetTypeRefName(MetadataReader reader, TypeReference typeRef)
    {
        var ns = reader.GetString(typeRef.Namespace);
        var name = reader.GetString(typeRef.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
