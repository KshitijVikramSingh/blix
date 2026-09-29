using System.Reflection;
using System.Text.RegularExpressions;

namespace Blix.Graphics;

/// <summary>A separate <c>sampler</c> a shader declares, and the preset it samples with.</summary>
/// <param name="Name">The GLSL name, as reflection reports it.</param>
/// <param name="Preset">A <see cref="SamplerDescription"/> preset's name: <c>LinearClamp</c>, <c>LinearClampMipmap</c>, ...</param>
public sealed record ShaderSampler(string Name, string Preset)
{
    /// <summary>The sampler state the preset names.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SamplerDescription Description => ShaderSamplers.Preset(Preset);
}

/// <summary>
/// Scans GLSL for <c>//@sampler</c>: the sampler state a separate <c>sampler</c> declaration uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sampling state is a fact about how a shader reads, so the shader states it.</b> A shader that
/// samples its textures through separate images and a few shared samplers needs those samplers to
/// exist, and the only place that knows what each one is for is the shader that samples with it:
/// </para>
/// <code>
/// //@sampler LinearClamp
/// layout(set = 1, binding = 20) uniform sampler uLinearClamp;
/// </code>
/// <para>
/// The device builds each one into the descriptor-set layout as an immutable sampler, so nothing is
/// bound per draw and no list has to stay in step with the shader. It exists because a combined
/// <c>sampler2D</c> counts against the per-stage sampler limit, which is 16 on MoltenVK, while a
/// separate <c>texture2D</c> counts only against sampled images (256): a stage with twenty textures
/// and two samplers is inside every limit.
/// </para>
/// <para>
/// <b>A separate image ignores its texture's own sampler.</b> That sampler belongs to combined
/// bindings; a <c>texture2D</c> is read through whichever sampler the shader pairs it with.
/// </para>
/// <para>
/// Every mistake fails the build rather than a draw: a <c>//@sampler</c> with no <c>uniform sampler</c>
/// after it, a <c>uniform sampler</c> with no <c>//@sampler</c> before it, and a sampler declared in a
/// form the scan does not read (two names in one declaration). An array, <c>uniform sampler uS[4];</c>,
/// is one declaration: its preset applies to every element.
/// </para>
/// </remarks>
public static class ShaderSamplers
{
    private static readonly Regex TagLine = new(@"//@sampler\s+(\S+)\s*$", RegexOptions.Compiled);
    // One sampler per declaration, optionally an array: `uniform sampler uS;` or `uniform sampler uS[4];`.
    // An array takes one preset for every element, which is what the layout builds.
    private static readonly Regex SamplerDecl = new(
        @"^\s*(?:layout\s*\([^)]*\)\s*)?uniform\s+sampler(?:Shadow)?\s+(\w+)\s*(?:\[\s*\d*\s*\])?\s*;", RegexOptions.Compiled);
    // Anything that declares a separate sampler at all. A line that matches this and not the one
    // above (two names in one declaration, say) is refused, so none slips past the scan unstated.
    private static readonly Regex AnySamplerDecl = new(@"\buniform\s+sampler(?:Shadow)?\s", RegexOptions.Compiled);

    /// <summary>Every separate sampler in <paramref name="glslSource"/>, with its declared preset.</summary>
    public static IReadOnlyList<ShaderSampler> Scan(string glslSource)
    {
        ArgumentNullException.ThrowIfNull(glslSource);
        var result = new List<ShaderSampler>();
        string? pending = null;
        var lineNo = 0;

        foreach (var raw in glslSource.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var tag = TagLine.Match(raw);
            if (tag.Success)
            {
                if (pending is not null)
                    throw new InvalidOperationException(
                        $"//@sampler {pending} on the line before line {lineNo} names no sampler: another //@sampler follows it.");
                pending = tag.Groups[1].Value;
                Preset(pending); // an unknown preset fails here, naming the ones that exist
                continue;
            }

            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            var decl = SamplerDecl.Match(raw);
            if (!decl.Success && AnySamplerDecl.IsMatch(raw))
                throw new InvalidOperationException(
                    $"Line {lineNo} declares a separate sampler in a form the scan does not read: \"{trimmed}\". " +
                    "Declare one per line, 'uniform sampler uName;' or 'uniform sampler uName[N];', each with its //@sampler.");
            if (decl.Success)
            {
                var name = decl.Groups[1].Value;
                if (pending is null)
                    throw new InvalidOperationException(
                        $"'uniform sampler {name}' (line {lineNo}) says nothing about how it samples. Put " +
                        $"//@sampler <preset> on the line before it; presets: {string.Join(", ", PresetNames)}.");
                result.Add(new ShaderSampler(name, pending));
                pending = null;
                continue;
            }

            if (pending is not null)
                throw new InvalidOperationException(
                    $"//@sampler {pending} must be followed by a 'uniform sampler' declaration; line {lineNo} is \"{trimmed}\".");
        }

        if (pending is not null)
            throw new InvalidOperationException($"//@sampler {pending} at the end of the source names no sampler.");
        return result;
    }

    /// <summary>The <see cref="SamplerDescription"/> preset called <paramref name="name"/>.</summary>
    public static SamplerDescription Preset(string name) =>
        Presets.TryGetValue(name, out var preset)
            ? preset
            : throw new InvalidOperationException(
                $"No sampler preset named '{name}'. Presets: {string.Join(", ", PresetNames)}.");

    /// <summary>The preset names, which are <see cref="SamplerDescription"/>'s own static properties.</summary>
    public static IReadOnlyList<string> PresetNames => Presets.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // Read from the type rather than listed, so a preset added to SamplerDescription is usable here
    // without a second list to update.
    private static readonly Dictionary<string, SamplerDescription> Presets = typeof(SamplerDescription)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(SamplerDescription))
        .ToDictionary(p => p.Name, p => (SamplerDescription)p.GetValue(null)!, StringComparer.Ordinal);
}

/// <summary>The <c>//@sampler</c> declarations as a build artifact, beside the .spv.</summary>
/// <remarks>
/// The same sidecar pattern as <see cref="ShaderTunableSidecar"/>, for the same reason: a comment is
/// not in the compiled module, and the build is where the expanded source is. Always written, even
/// empty, so a present file says "scanned, none here".
/// </remarks>
public static class ShaderSamplerSidecar
{
    /// <summary>The sidecar that belongs to a compiled shader: <c>&lt;name&gt;.spv.samplers.json</c>.</summary>
    public static string PathFor(string spvPath) => spvPath + ".samplers.json";

    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string ToJson(IReadOnlyList<ShaderSampler> samplers) =>
        System.Text.Json.JsonSerializer.Serialize(samplers, Options);

    public static IReadOnlyList<ShaderSampler> FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<ShaderSampler[]>(json, Options) ?? Array.Empty<ShaderSampler>();

    /// <summary>The sidecar beside a compiled shader, or none when it is absent.</summary>
    public static IReadOnlyList<ShaderSampler> Load(string spvPath)
    {
        var path = PathFor(spvPath);
        return File.Exists(path) ? FromJson(File.ReadAllText(path)) : Array.Empty<ShaderSampler>();
    }
}
