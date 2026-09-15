using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Blix.Tools.Apps;

// Writes the app index: every [BlixApp] an assembly declares, as a sidecar next to it.
//
// ── Why this exists ─────────────────────────────────────────────────────────
//   The attribute is the source of truth, because it lives with the code it names
//   and cannot drift from it. But discovery has to be instant and must not require
//   a build: the moment you most want to know what a project contains is often the
//   moment its build is broken. Generating an index from the attribute gets both —
//   it is regenerated rather than maintained, so it cannot drift, and it is a file,
//   so reading it costs nothing.
//
//   It is also the only place that can record which apphost runs which app. An
//   attribute cannot say that about itself, and it is the thing the launcher needs
//   the moment a project has more than one assembly.
//
//   Blix.Tools.Shader already establishes this pattern one layer down: run at build,
//   reflect, write a sidecar, read it cheap. This is the same move one level up.
//
// ── Metadata, never loading ─────────────────────────────────────────────────
//   It reads the ECMA-335 tables directly and never loads the assembly. That is not
//   an optimisation — loading would mean resolving the whole dependency graph of
//   whatever is being indexed, running static initialisers, and failing for reasons
//   that have nothing to do with the question being asked. Nothing here executes any
//   of the code it reads.
public static class Program
{
    private const string AttributeNamespace = "Blix.Core";
    private const string AttributeName = "BlixAppAttribute";

    public static int Main(string[] args)
    {
        var assemblyPath = Value(args, "--assembly");
        var outputPath = Value(args, "--output");

        if (assemblyPath is null || outputPath is null)
        {
            Console.Error.WriteLine("usage: blix-apps --assembly <path.dll> --output <path.blixapps.json>");
            return 2;
        }

        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"blix-apps: no assembly at {assemblyPath}");
            return 2;
        }

        List<AppEntry> apps;
        bool hasEntryPoint;
        try
        {
            (apps, hasEntryPoint) = Read(assemblyPath);
        }
        catch (BadImageFormatException)
        {
            // Not a managed assembly. Native sidecars land in the same folders and are
            // not an error — there is simply nothing to index.
            return 0;
        }
        catch (DeclarationException bad)
        {
            Console.Error.WriteLine($"blix-apps: {bad.Message}");
            return 1;
        }

        var duplicate = apps.GroupBy(a => a.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            Console.Error.WriteLine(
                $"blix-apps: '{duplicate.Key}' is declared {duplicate.Count()} times in " +
                $"{Path.GetFileName(assemblyPath)}: {string.Join(", ", duplicate.Select(d => d.Method))}");
            return 1;
        }

        var host = Path.ChangeExtension(assemblyPath, null);
        var index = new AppIndex(
            Assembly: Path.GetFileName(assemblyPath),
            AppHost: File.Exists(host) ? Path.GetFileName(host) : null,
            HasEntryPoint: hasEntryPoint,
            Apps: apps.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(index, JsonOptions));
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static (List<AppEntry> Apps, bool HasEntryPoint) Read(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);

        if (!pe.HasMetadata) throw new BadImageFormatException("no CLI metadata");
        var reader = pe.GetMetadataReader();

        var corHeader = pe.PEHeaders.CorHeader;
        var entryToken = corHeader is not null && (corHeader.Flags & CorFlags.NativeEntryPoint) == 0
            ? corHeader.EntryPointTokenOrRelativeVirtualAddress
            : 0;
        var hasEntryPoint = entryToken != 0;

        // Which method IS Main, so the launcher knows when an app needs no selecting.
        // A [BlixApp] on the entry point is the smallest useful declaration there is —
        // it renames an executable and gives it a summary, and nothing else changes —
        // and the launcher must exec it directly rather than passing a selector the
        // app was never written to strip.
        var entryPoint = hasEntryPoint
            ? MetadataTokens.MethodDefinitionHandle(entryToken & 0x00FFFFFF)
            : default;

        var apps = new List<AppEntry>();
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            foreach (var attributeHandle in method.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                if (!IsBlixApp(reader, attribute)) continue;

                var where = Describe(reader, method, handle);
                Validate(reader, method, where);
                apps.Add(Decode(reader, attribute, where, isEntryPoint: handle == entryPoint));
            }
        }

        return (apps, hasEntryPoint);
    }

    private static bool IsBlixApp(MetadataReader reader, CustomAttribute attribute)
    {
        // The attribute's constructor is either a MemberReference (the usual case — the
        // attribute is defined in another assembly) or a MethodDefinition (Blix.Core
        // indexing itself). Both have to be followed to the declaring type.
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind != HandleKind.TypeReference) return false;
                var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                return reader.GetString(type.Name) == AttributeName
                    && reader.GetString(type.Namespace) == AttributeNamespace;
            }

            case HandleKind.MethodDefinition:
            {
                var ctor = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var type = reader.GetTypeDefinition(ctor.GetDeclaringType());
                return reader.GetString(type.Name) == AttributeName
                    && reader.GetString(type.Namespace) == AttributeNamespace;
            }

            default:
                return false;
        }
    }

    private static AppEntry Decode(
        MetadataReader reader, CustomAttribute attribute, string where, bool isEntryPoint)
    {
        var value = attribute.DecodeValue(new StringTypeProvider());

        var name = value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DeclarationException($"[BlixApp] on {where} has no name.");
        }

        string? summary = null;
        var headed = false;
        foreach (var named in value.NamedArguments)
        {
            if (named.Name == "Summary") summary = named.Value as string;
            else if (named.Name == "Headed") headed = named.Value is true;
        }

        return new AppEntry(name, summary, headed, where, isEntryPoint);
    }

    // The four shapes an app may have, checked HERE so a mis-declared app is a build
    // failure rather than a tool that is quietly unreachable. An app that cannot be
    // found because it was declared slightly wrong is the worst failure this layer has.
    private static void Validate(MetadataReader reader, MethodDefinition method, string where)
    {
        if ((method.Attributes & MethodAttributes.Static) == 0)
        {
            throw new DeclarationException($"[BlixApp] on {where}: an app must be static.");
        }

        var signature = method.DecodeSignature(new StringTypeProvider(), genericContext: null);

        if (signature.ReturnType is not ("System.Int32" or "System.Void"))
        {
            throw new DeclarationException(
                $"[BlixApp] on {where}: an app returns int or void, not {signature.ReturnType}.");
        }

        var parameters = signature.ParameterTypes;
        var ok = parameters.Length == 0
            || (parameters.Length == 1 && parameters[0] == "System.String[]");
        if (!ok)
        {
            throw new DeclarationException(
                $"[BlixApp] on {where}: an app takes string[] or nothing, not ({string.Join(", ", parameters)}).");
        }
    }

    private static string Describe(MetadataReader reader, MethodDefinition method, MethodDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(method.GetDeclaringType());
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        var full = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        return $"{full}.{reader.GetString(method.Name)}";
    }

    private static string? Value(string[] args, string flag)
    {
        var at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private sealed class DeclarationException(string message) : Exception(message);

    private sealed record AppIndex(string Assembly, string? AppHost, bool HasEntryPoint, AppEntry[] Apps);

    private sealed record AppEntry(
        string Name, string? Summary, bool Headed, string Method, bool IsEntryPoint);

    // Types as strings, which is all this needs. The full provider contract exists for
    // callers that rebuild real Type objects; here the questions are "is it int" and
    // "is it string[]", and a name answers both.
    private sealed class StringTypeProvider : ICustomAttributeTypeProvider<string>, ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Void => "System.Void",
            _ => code.ToString(),
        };

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> args) => genericType;
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var ns = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var ns = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        public string GetTypeFromSpecification(
            MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";

        public string GetSystemType() => "System.Type";
        public string GetTypeFromSerializedName(string name) => name;
        public bool IsSystemType(string type) => type == "System.Type";
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }
}
