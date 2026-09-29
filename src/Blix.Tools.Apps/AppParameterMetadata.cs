using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace Blix.Tools.Apps;

/// <summary>
/// An app's typed parameters, read from metadata: validated as <c>Blix.Core.AppParameters</c>
/// validates them at run time, and described as it describes them.
/// </summary>
/// <remarks>
/// <b>Two readers of one truth again.</b> This never loads the assembly, so it cannot call
/// <c>AppParameters.Usage</c>; it rebuilds the same line from the signature, the parameter names
/// and their default constants, and the app suite checks that the two lines agree. The one thing it
/// cannot know is the members of an enum defined in another assembly, which it names by type.
/// </remarks>
internal static class AppParameterMetadata
{
    /// <summary>Why a parameter list cannot be bound, or null when it can.</summary>
    public static string? Problem(MetadataReader reader, MethodDefinition method)
    {
        var signature = method.DecodeSignature(new ParameterTypes(), genericContext: null);
        var names = Names(reader, method, signature.ParameterTypes.Length);

        for (var i = 0; i < signature.ParameterTypes.Length; i++)
        {
            if (!Supported(signature.ParameterTypes[i]))
            {
                return $"parameter '{names[i]}' is a {signature.ParameterTypes[i].Name}, which a command line " +
                    "cannot spell. An app takes AppArgs, int, long, float, double, bool, string, an enum, " +
                    "their nullable forms, or IReadOnlyList<string>.";
            }
        }

        return signature.ParameterTypes.Count(p => p.Name == AppArgs) > 1 ? "an app takes at most one AppArgs." : null;
    }

    /// <summary>One line of usage, the same line <c>AppParameters.Usage</c> writes. Empty for none.</summary>
    public static string Usage(MetadataReader reader, MethodDefinition method)
    {
        var signature = method.DecodeSignature(new ParameterTypes(), genericContext: null);
        var count = signature.ParameterTypes.Length;
        var names = Names(reader, method, count);
        var defaults = Defaults(reader, method, count);

        var entries = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var type = signature.ParameterTypes[i];
            if (type.Name == AppArgs) continue;

            // The same rules as Blix.Core.AppParameters.Describe, which the app suite compares
            // against this line for every fixture.
            var flag = FlagFor(names[i]);
            var (hasDefault, value) = defaults[i];
            var entry = $"--{flag} <{TypeName(type)}>";
            var isNullable = type.Name == Nullable;
            var inner = Unwrap(type);

            if (type.Name == "System.Boolean")
            {
                entries.Add(hasDefault && value is true ? $"--{flag}=false" : $"--{flag}");
            }
            else if (isNullable && inner.Name == "System.Boolean")
            {
                entries.Add($"[--{flag}[=false]]");
            }
            else if (type.Name == ReadOnlyList)
            {
                entries.Add(hasDefault ? $"[{entry}]" : entry);
            }
            else if (hasDefault && value is not null)
            {
                entries.Add($"{entry}={Format(value, inner)}");
            }
            else if (hasDefault || isNullable)
            {
                entries.Add($"[{entry}]");
            }
            else
            {
                entries.Add(entry);
            }
        }

        return string.Join(' ', entries);
    }

    private const string AppArgs = "Blix.Core.AppArgs";
    private const string ReadOnlyList = "System.Collections.Generic.IReadOnlyList`1";
    private const string Nullable = "System.Nullable`1";

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "System.Int32", "System.Int64", "System.Single", "System.Double", "System.Boolean",
    };

    private static bool Supported(ParameterType type)
    {
        if (type.Name is AppArgs or "System.String") return true;
        if (type.Name == ReadOnlyList) return type.Arguments is [{ Name: "System.String" }];
        var value = Unwrap(type);
        return value.IsEnum || Values.Contains(value.Name);
    }

    private static ParameterType Unwrap(ParameterType type) =>
        type.Name == Nullable && type.Arguments is [var inner] ? inner : type;

    private static string TypeName(ParameterType type)
    {
        if (type.Name == ReadOnlyList) return "text, repeatable";
        var value = Unwrap(type);
        if (value.IsEnum) return value.Members is { } members ? string.Join('|', members.Select(m => FlagFor(m.Name))) : value.Name;
        return value.Name switch
        {
            "System.String" => "text",
            "System.Int32" or "System.Int64" => "int",
            "System.Boolean" => "bool",
            _ => "number",
        };
    }

    private static string Format(object value, ParameterType type)
    {
        if (type.IsEnum && type.Members is { } members)
        {
            var member = members.FirstOrDefault(m => Equals(m.Value, value));
            if (member.Name is not null) return FlagFor(member.Name);
        }

        return value switch
        {
            string s => $"\"{s}\"",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    // mapSeed becomes map-seed. Must match Blix.Core.AppParameters.FlagFor.
    private static string FlagFor(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static string[] Names(MetadataReader reader, MethodDefinition method, int count)
    {
        var names = Enumerable.Range(0, count).Select(i => $"arg{i}").ToArray();
        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber is > 0 and var n && n <= count) names[n - 1] = reader.GetString(parameter.Name);
        }

        return names;
    }

    private static (bool Has, object? Value)[] Defaults(MetadataReader reader, MethodDefinition method, int count)
    {
        var defaults = new (bool Has, object? Value)[count];
        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);
            var n = parameter.SequenceNumber;
            if (n < 1 || n > count || (parameter.Attributes & ParameterAttributes.HasDefault) == 0) continue;
            var constant = parameter.GetDefaultValue();
            defaults[n - 1] = constant.IsNil ? (true, null) : (true, Decode(reader, reader.GetConstant(constant)));
        }

        return defaults;
    }

    private static object? Decode(MetadataReader reader, Constant constant)
    {
        var blob = reader.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => blob.ReadBoolean(),
            ConstantTypeCode.Int32 => blob.ReadInt32(),
            ConstantTypeCode.Int64 => blob.ReadInt64(),
            ConstantTypeCode.Single => blob.ReadSingle(),
            ConstantTypeCode.Double => blob.ReadDouble(),
            ConstantTypeCode.String => blob.ReadUTF16(blob.Length),
            ConstantTypeCode.NullReference => null,
            _ => null,
        };
    }

    /// <summary>A parameter's type, as much of it as a command line cares about.</summary>
    internal sealed record ParameterType(
        string Name, bool IsEnum = false, ImmutableArray<ParameterType> Arguments = default,
        (string Name, object? Value)[]? Members = null);

    private sealed class ParameterTypes : ISignatureTypeProvider<ParameterType, object?>
    {
        public ParameterType GetPrimitiveType(PrimitiveTypeCode code) => new(code switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Void => "System.Void",
            _ => "System." + code,
        });

        public ParameterType GetGenericInstantiation(ParameterType genericType, ImmutableArray<ParameterType> args) =>
            genericType with { Arguments = args };

        public ParameterType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = Qualified(reader.GetString(type.Namespace), reader.GetString(type.Name));
            if (!IsEnumBase(reader, type.BaseType)) return new(name);

            // Its members and their values, for usage: a default constant is only the number.
            var members = new List<(string, object?)>();
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Literal) == 0) continue;
                var constant = field.GetDefaultValue();
                members.Add((reader.GetString(field.Name), constant.IsNil ? null : Decode(reader, reader.GetConstant(constant))));
            }

            return new(name, IsEnum: true, Members: members.ToArray());
        }

        // Another assembly's value type is taken to be an enum; the run-time check is exact, and
        // resolving it here would mean loading assemblies this indexer deliberately never loads.
        public ParameterType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var name = Qualified(reader.GetString(type.Namespace), reader.GetString(type.Name));
            var isValue = rawTypeKind == (byte)SignatureTypeKind.ValueType;
            return new(name, IsEnum: isValue && !name.StartsWith("System.", StringComparison.Ordinal));
        }

        public ParameterType GetSZArrayType(ParameterType elementType) => new(elementType.Name + "[]");
        public ParameterType GetArrayType(ParameterType elementType, ArrayShape shape) => new(elementType.Name + "[]");
        public ParameterType GetByReferenceType(ParameterType elementType) => new(elementType.Name + "&");
        public ParameterType GetPointerType(ParameterType elementType) => new(elementType.Name + "*");
        public ParameterType GetGenericMethodParameter(object? context, int index) => new("!!" + index);
        public ParameterType GetGenericTypeParameter(object? context, int index) => new("!" + index);
        public ParameterType GetModifiedType(ParameterType modifier, ParameterType unmodifiedType, bool isRequired) => unmodifiedType;
        public ParameterType GetPinnedType(ParameterType elementType) => elementType;
        public ParameterType GetFunctionPointerType(MethodSignature<ParameterType> signature) => new("fnptr");

        public ParameterType GetTypeFromSpecification(
            MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        private static bool IsEnumBase(MetadataReader reader, EntityHandle baseType)
        {
            if (baseType.IsNil || baseType.Kind != HandleKind.TypeReference) return false;
            var reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
            return reader.GetString(reference.Namespace) == "System" && reader.GetString(reference.Name) == "Enum";
        }

        private static string Qualified(string ns, string name) => string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
