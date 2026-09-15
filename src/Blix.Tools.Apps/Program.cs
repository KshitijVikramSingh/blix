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

    // <b>Recipes are indexed by the same pass, for the same reason.</b> A cooking recipe has
    // exactly the app layer's discovery problem — it lives with the code it names, it must be
    // findable without a build, and a project's own must be found with no registration step. There
    // was no argument for solving that twice, and the expensive half (reading ECMA-335 without
    // loading anything) was already written.
    private const string RecipeNamespace = "Blix.Cooked";
    private const string RecipeName = "RecipeAttribute";

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
        List<RecipeEntry> recipes;
        bool hasEntryPoint;
        try
        {
            (apps, recipes, hasEntryPoint) = Read(assemblyPath);
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

        // The same rule for recipes, and it matters more: a recipe id is stamped into every file
        // it writes, so two recipes sharing one would make the files' own provenance ambiguous
        // after the fact, when there is nothing left to disambiguate them with.
        var clash = recipes.GroupBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            Console.Error.WriteLine(
                $"blix-apps: recipe id '{clash.Key}' is declared {clash.Count()} times in " +
                $"{Path.GetFileName(assemblyPath)}: {string.Join(", ", clash.Select(d => d.Method))}");
            return 1;
        }

        var host = Path.ChangeExtension(assemblyPath, null);
        var index = new AppIndex(
            Assembly: Path.GetFileName(assemblyPath),
            AppHost: File.Exists(host) ? Path.GetFileName(host) : null,
            HasEntryPoint: hasEntryPoint,
            Apps: apps.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(),
            Recipes: recipes.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(index, JsonOptions));
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static (List<AppEntry> Apps, List<RecipeEntry> Recipes, bool HasEntryPoint) Read(string assemblyPath)
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
        var recipes = new List<RecipeEntry>();
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            foreach (var attributeHandle in method.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                var where = Describe(reader, method, handle);

                if (IsAttribute(reader, attribute, AttributeNamespace, AttributeName))
                {
                    Validate(reader, method, where);
                    apps.Add(Decode(reader, attribute, where, isEntryPoint: handle == entryPoint));
                }
                else if (IsAttribute(reader, attribute, RecipeNamespace, RecipeName))
                {
                    ValidateRecipe(reader, method, where);
                    recipes.Add(DecodeRecipe(reader, attribute, where));
                }
            }
        }

        return (apps, recipes, hasEntryPoint);
    }

    private static RecipeEntry DecodeRecipe(MetadataReader reader, CustomAttribute attribute, string where)
    {
        var value = attribute.DecodeValue(new StringTypeProvider());

        var id = value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new DeclarationException($"[Recipe] on {where} has no id.");
        }

        // Four characters, because the id rides in every cooked file's preamble as a 4cc. A longer
        // one would truncate on write and attribute files to a recipe that does not exist — which
        // is the sort of thing that is obvious at build and impossible to diagnose afterwards.
        if (id.Length != 4)
        {
            throw new DeclarationException(
                $"[Recipe] on {where}: id '{id}' must be exactly 4 characters; it is stamped as a 4cc.");
        }

        string produces = "", consumes = "", summary = "";
        uint version = 1;
        foreach (var named in value.NamedArguments)
        {
            switch (named.Name)
            {
                case "Produces": produces = named.Value as string ?? ""; break;
                case "Consumes": consumes = named.Value as string ?? ""; break;
                case "Summary": summary = named.Value as string ?? ""; break;
                case "Version": version = named.Value is int v ? (uint)v : 1; break;
            }
        }

        if (!produces.StartsWith('.'))
        {
            throw new DeclarationException(
                $"[Recipe] on {where}: Produces must be an extension beginning with '.', got '{produces}'.");
        }

        if (consumes.Length == 0 || consumes.Split(';').Any(e => !e.StartsWith('.')))
        {
            throw new DeclarationException(
                $"[Recipe] on {where}: Consumes must be ';'-separated extensions beginning with '.', got '{consumes}'.");
        }

        return new RecipeEntry(id, produces, consumes, summary, version, where);
    }

    // The one shape a recipe may have. Checked here so a mis-declared recipe is a build failure
    // rather than a cook that silently never runs — the same bar the app layer sets, for the same
    // reason: a thing that cannot be found because it was declared slightly wrong is the worst
    // failure this layer has.
    private static void ValidateRecipe(MetadataReader reader, MethodDefinition method, string where)
    {
        if ((method.Attributes & MethodAttributes.Static) == 0)
        {
            throw new DeclarationException($"[Recipe] on {where}: a recipe must be static.");
        }

        var signature = method.DecodeSignature(new StringTypeProvider(), genericContext: null);

        if (signature.ReturnType != "Blix.Cooked.CookOutcome")
        {
            throw new DeclarationException(
                $"[Recipe] on {where}: a recipe returns Blix.Cooked.CookOutcome, not {signature.ReturnType}.");
        }

        if (signature.ParameterTypes is not ["Blix.Cooked.CookRequest"])
        {
            throw new DeclarationException(
                $"[Recipe] on {where}: a recipe takes one Blix.Cooked.CookRequest, not " +
                $"({string.Join(", ", signature.ParameterTypes)}).");
        }
    }

    private static bool IsAttribute(
        MetadataReader reader, CustomAttribute attribute, string attributeNamespace, string attributeName)
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
                return reader.GetString(type.Name) == attributeName
                    && reader.GetString(type.Namespace) == attributeNamespace;
            }

            case HandleKind.MethodDefinition:
            {
                var ctor = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var type = reader.GetTypeDefinition(ctor.GetDeclaringType());
                return reader.GetString(type.Name) == attributeName
                    && reader.GetString(type.Namespace) == attributeNamespace;
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

    private sealed record AppIndex(
        string Assembly, string? AppHost, bool HasEntryPoint, AppEntry[] Apps, RecipeEntry[] Recipes);

    private sealed record RecipeEntry(
        string Id, string Produces, string Consumes, string Summary, uint Version, string Method);

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
