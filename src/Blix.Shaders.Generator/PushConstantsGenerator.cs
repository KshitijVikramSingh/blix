using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Blix.Shaders.Generator;

/// <summary>
/// Writes the fields of every <c>[PushConstants]</c> struct from the shaders' reflection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fields are generated rather than checked.</b> A push block is laid out by GLSL's rules,
/// where a vec3 takes 16 bytes, and C# lays out a struct of Vector3 at 12. A hand-written struct
/// that is right by name can be wrong by offset, and the fix is either padding by hand or a copy
/// per draw. Written from reflection, each field carries its <c>[FieldOffset]</c>, so the struct is
/// the bytes the shader reads.
/// </para>
/// <para>
/// The reflection files arrive as AdditionalFiles, declared from the project's GlslShader items
/// at evaluation time so a design-time build sees them without compiling a shader.
/// </para>
/// </remarks>
[Generator]
public sealed class PushConstantsGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Blix.Graphics.PushConstantsAttribute";
    private const string ShaderNameAttribute = "Blix.Graphics.ShaderNameAttribute";
    private const string ReflectionSuffix = ".spv.refl.json";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is StructDeclarationSyntax,
            static (ctx, _) => Declaration.From(ctx));

        var reflections = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(ReflectionSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => new Reflection(
                Path.GetFileName(f.Path).Substring(0, Path.GetFileName(f.Path).Length - ReflectionSuffix.Length),
                f.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(declarations.Combine(reflections), static (spc, pair) =>
        {
            var (declaration, all) = pair;
            if (declaration is not null) Emit(spc, declaration, all);
        });
    }

    private static void Emit(SourceProductionContext spc, Declaration d, ImmutableArray<Reflection> reflections)
    {
        if (!d.IsPartial)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotPartial, d.Location, d.Name));
            return;
        }

        if (d.DeclaredField is { } field)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.HasFields, d.Location, d.Name, field));
            return;
        }

        if (d.Stages.Length == 0)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoStages, d.Location, d.Name));
            return;
        }

        // Every listed stage's block, merged by offset. A stage without one is fine (a fragment
        // shader often reads nothing), but they must agree wherever they overlap.
        var members = new List<Member>();
        var anyBlock = false;
        foreach (var stage in d.Stages)
        {
            var file = Path.GetFileName(stage);
            var reflection = reflections.FirstOrDefault(r => string.Equals(r.Stage, file, StringComparison.OrdinalIgnoreCase));
            if (reflection is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoReflection, d.Location, d.Name, stage));
                return;
            }

            List<Member>? block;
            try
            {
                block = PushBlock(reflection.Text, file);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or KeyNotFoundException)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadReflection, d.Location, d.Name, file, e.Message));
                return;
            }

            if (block is null) continue;
            anyBlock = true;

            foreach (var m in block)
            {
                var clash = members.FirstOrDefault(x => x.Offset == m.Offset || x.Name == m.Name);
                if (clash is null)
                {
                    members.Add(m);
                }
                else if (clash.Name != m.Name || clash.Type != m.Type || clash.Offset != m.Offset)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.Disagree, d.Location, d.Name,
                        clash.Name, clash.Type, clash.Offset, clash.Stage, m.Name, m.Type, m.Offset, m.Stage));
                    return;
                }
            }
        }

        if (!anyBlock)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoBlock, d.Location, d.Name, string.Join(", ", d.Stages)));
            return;
        }

        members.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Types first, then names: an unsupported member is the more basic problem.
        foreach (var m in members)
        {
            if (m.IsArray)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.Array, d.Location, d.Name, m.Name, m.Stage));
                return;
            }

            if (CSharpType(m.Type) is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.Unsupported, d.Location, d.Name, m.Name, m.Type));
                return;
            }
        }

        var rule = d.Prefix is { Length: > 0 } p ? $"Prefix = \"{p}\"" : "exact names";
        foreach (var o in d.Overrides)
        {
            if (!members.Any(m => m.Name == o.Key))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnknownOverride, d.Location, d.Name, o.Key,
                    string.Join(", ", members.Select(m => m.Name))));
                return;
            }
        }

        var named = members.Select(m => (Member: m, Field: FieldName(m.Name, d))).ToList();
        foreach (var group in named.GroupBy(n => n.Field).Where(g => g.Count() > 1))
        {
            var pair = group.Take(2).ToArray();
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.Collision, d.Location, d.Name,
                pair[0].Member.Name, pair[1].Member.Name, group.Key, rule));
            return;
        }

        var size = members.Max(m => m.Offset + SizeOf(m.Type));
        spc.AddSource($"{d.HintName}.PushConstants.g.cs", Write(d, named, size, rule));
    }

    private static string Write(Declaration d, List<(Member Member, string Field)> fields, int size, string rule)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Blix.Shaders.Generator from the shaders' reflection.");
        sb.AppendLine("#nullable enable");
        if (d.Namespace is { Length: > 0 } ns) sb.Append("namespace ").Append(ns).AppendLine(";");
        sb.AppendLine();

        foreach (var outer in d.Containing) sb.Append(outer).AppendLine(" {");

        sb.Append("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit, Size = ")
            .Append(size).AppendLine(")]");
        sb.Append("partial struct ").Append(d.Name).AppendLine();
        sb.AppendLine("{");
        foreach (var (m, field) in fields)
        {
            sb.Append("    /// <summary><c>").Append(m.Name).Append("</c>: ").Append(m.Type).Append(" at byte ")
                .Append(m.Offset).Append(", from ").Append(m.Stage).Append(" (").Append(rule).AppendLine(").</summary>");
            sb.Append("    [global::System.Runtime.InteropServices.FieldOffset(").Append(m.Offset).Append(")] public ")
                .Append(CSharpType(m.Type)).Append(' ').Append(field).AppendLine(";");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>The block's size in bytes, which is what the program's push range expects.</summary>");
        sb.Append("    public const int SizeInBytes = ").Append(size).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Write these bytes into <paramref name=\"destination\"/>, a buffer reused across frames.</summary>");
        sb.AppendLine("    public readonly void WriteTo(global::System.Span<byte> destination)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (destination.Length < SizeInBytes)");
        sb.AppendLine("        {");
        sb.Append("            throw new global::System.ArgumentException($\"").Append(d.Name)
            .AppendLine(" is {SizeInBytes} bytes; the destination holds {destination.Length}.\", nameof(destination));");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        global::System.Runtime.InteropServices.MemoryMarshal.Write(destination, in this);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>These bytes, as a draw's <c>pushConstants</c> takes them.</summary>");
        sb.AppendLine("    public readonly byte[] ToBytes()");
        sb.AppendLine("    {");
        sb.AppendLine("        var bytes = new byte[SizeInBytes];");
        sb.AppendLine("        global::System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in this);");
        sb.AppendLine("        return bytes;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        foreach (var _ in d.Containing) sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FieldName(string member, Declaration d)
    {
        if (d.Overrides.TryGetValue(member, out var renamed)) return renamed;
        if (d.Prefix is { Length: > 0 } p && member.Length > p.Length && member.StartsWith(p, StringComparison.Ordinal))
        {
            return member.Substring(p.Length);
        }

        return member;
    }

    // Null means unsupported. bool is 4 bytes in a block, so it is carried as a uint.
    private static string? CSharpType(string glsl) => glsl switch
    {
        "float" => "float",
        "int" => "int",
        "uint" => "uint",
        "bool" => "uint",
        "vec2" => "global::System.Numerics.Vector2",
        "vec3" => "global::System.Numerics.Vector3",
        "vec4" => "global::System.Numerics.Vector4",
        "mat4" => "global::System.Numerics.Matrix4x4",
        _ => null,
    };

    private static int SizeOf(string glsl) => glsl switch
    {
        "vec2" => 8,
        "vec3" => 12,
        "vec4" => 16,
        "mat4" => 64,
        _ => 4,
    };

    // The push-constant block's members in one stage's reflection, or null when it has none.
    private static List<Member>? PushBlock(string text, string stage)
    {
        var root = (Dictionary<string, object?>)Json.Parse(text)!;
        if (!root.TryGetValue("push_constants", out var pcs) || pcs is not List<object?> { Count: > 0 } list) return null;

        var typeRef = (string)((Dictionary<string, object?>)list[0]!)["type"]!;
        var types = (Dictionary<string, object?>)root["types"]!;
        var block = (Dictionary<string, object?>)types[typeRef]!;

        var result = new List<Member>();
        foreach (var raw in (List<object?>)block["members"]!)
        {
            var m = (Dictionary<string, object?>)raw!;
            result.Add(new Member(
                (string)m["name"]!,
                (string)m["type"]!,
                (int)(double)m["offset"]!,
                m.ContainsKey("array"),
                stage));
        }

        return result;
    }

    private sealed record Reflection(string Stage, string Text);

    private sealed record Member(string Name, string Type, int Offset, bool IsArray, string Stage);

    private sealed record Declaration(
        string Name, string HintName, string? Namespace, ImmutableArray<string> Containing,
        ImmutableArray<string> Stages, string? Prefix, ImmutableDictionary<string, string> Overrides,
        bool IsPartial, string? DeclaredField, Location Location)
    {
        public static Declaration? From(GeneratorAttributeSyntaxContext ctx)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type || ctx.TargetNode is not StructDeclarationSyntax syntax) return null;

            var attribute = ctx.Attributes[0];
            var stages = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
                ? attribute.ConstructorArguments[0].Values.Select(v => v.Value as string).Where(v => v is not null).Select(v => v!).ToImmutableArray()
                : ImmutableArray<string>.Empty;
            var prefix = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Prefix").Value.Value as string;

            var overrides = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var a in type.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() != ShaderNameAttribute || a.ConstructorArguments.Length != 2) continue;
                if (a.ConstructorArguments[0].Value is string shader && a.ConstructorArguments[1].Value is string name)
                {
                    overrides[shader] = name;
                }
            }

            var containing = new List<string>();
            for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            {
                var kind = outer.TypeKind == TypeKind.Struct ? "struct" : outer.IsRecord ? "record" : "class";
                containing.Insert(0, $"{(outer.IsStatic ? "static " : "")}partial {kind} {outer.Name}");
            }

            var declaredField = type.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => !f.IsStatic && !f.IsImplicitlyDeclared)?.Name;

            return new Declaration(
                type.Name,
                type.ToDisplayString().Replace('<', '_').Replace('>', '_'),
                type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                containing.ToImmutableArray(),
                stages,
                prefix,
                overrides.ToImmutable(),
                syntax.Modifiers.Any(m => m.Text == "partial"),
                declaredField,
                syntax.Identifier.GetLocation());
        }
    }
}
