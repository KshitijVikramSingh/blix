using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Blix.Shaders.Generator;

/// <summary>
/// Checks every <c>[ShaderEnum]</c> enum against the <c>//@tune enum{ … }</c> above its shader
/// member. It writes no code: both sides stay declared where they are written, and the build
/// refuses them when they disagree.
/// </summary>
/// <remarks>
/// The shader's list comes from the <c>.spv.tune.json</c> sidecar the shader tool writes from the
/// preprocessed source, so a declaration reached through an include is seen.
/// </remarks>
[Generator]
public sealed class ShaderEnumCheck : IIncrementalGenerator
{
    private const string AttributeName = "Blix.Graphics.ShaderEnumAttribute";
    private const string TuneSuffix = ".spv.tune.json";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enums = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is EnumDeclarationSyntax,
            static (ctx, _) => Declared.From(ctx));

        var sidecars = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(TuneSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => (
                Stage: Path.GetFileName(f.Path).Substring(0, Path.GetFileName(f.Path).Length - TuneSuffix.Length),
                Text: f.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(enums.Combine(sidecars), static (spc, pair) =>
        {
            var (declared, all) = pair;
            if (declared is not null) Check(spc, declared, all);
        });
    }

    private static void Check(SourceProductionContext spc, Declared e, ImmutableArray<(string Stage, string Text)> sidecars)
    {
        var file = Path.GetFileName(e.Stage);
        var sidecar = sidecars.FirstOrDefault(s => string.Equals(s.Stage, file, StringComparison.OrdinalIgnoreCase));
        if (sidecar.Text is null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.EnumNoShader, e.Location, e.Name, e.Stage));
            return;
        }

        List<string>? shaderNames;
        try
        {
            shaderNames = EnumNames(sidecar.Text, e.Member);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or KeyNotFoundException)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadReflection, e.Location, e.Name, file, ex.Message));
            return;
        }

        if (shaderNames is null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.EnumNoDeclaration, e.Location, e.Name, e.Member, file,
                string.Join(", ", e.Members.Select(m => m.Name))));
            return;
        }

        for (var i = 0; i < e.Members.Length; i++)
        {
            if (e.Members[i].Value != i)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.EnumNotSequential, e.Location, e.Name,
                    e.Members[i].Name, e.Members[i].Value, i));
                return;
            }
        }

        var csharp = e.Members.Select(m => m.Name).ToArray();
        if (csharp.Length != shaderNames.Count || csharp.Where((name, i) => Key(name) != Key(shaderNames[i])).Any())
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.EnumMismatch, e.Location, e.Name, e.Member, file,
                string.Join(", ", csharp), string.Join(", ", shaderNames)));
        }
    }

    // The enum names declared for one member, or null when it has no //@tune enum{} above it.
    private static List<string>? EnumNames(string text, string member)
    {
        foreach (var raw in (List<object?>)Json.Parse(text)!)
        {
            var entry = (Dictionary<string, object?>)raw!;
            if ((string?)entry["name"] != member) continue;
            return entry.TryGetValue("enumNames", out var names) && names is List<object?> list
                ? list.Select(n => (string)n!).ToList()
                : null;
        }

        return null;
    }

    // Case, spaces, dashes and underscores carry no meaning, as for every name Blix matches.
    private static string Key(string name) =>
        new string(name.Where(c => c is not ('-' or '_' or ' ')).Select(char.ToLowerInvariant).ToArray());

    private sealed record Declared(
        string Name, string Stage, string Member, ImmutableArray<(string Name, long Value)> Members, Location Location)
    {
        public static Declared? From(GeneratorAttributeSyntaxContext ctx)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type || ctx.TargetNode is not EnumDeclarationSyntax syntax) return null;
            var args = ctx.Attributes[0].ConstructorArguments;
            if (args.Length != 2 || args[0].Value is not string stage || args[1].Value is not string member) return null;

            var members = type.GetMembers().OfType<IFieldSymbol>()
                .Where(f => f.HasConstantValue)
                .Select(f => (f.Name, Convert.ToInt64(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)))
                .ToImmutableArray();

            return new Declared(type.Name, stage, member, members, syntax.Identifier.GetLocation());
        }
    }
}
