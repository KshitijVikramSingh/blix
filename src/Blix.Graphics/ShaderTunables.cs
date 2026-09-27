using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Blix.Graphics;

// Scans GLSL source for `//@tune` decorators — the opt-in + metadata tag that
// makes a shader uniform live-tunable from the diagnostics overlay without the
// game hand-wiring a dial + a mirror field + a UBO pack per variable.
//
// The tag sits immediately above the declaration and carries only what can't be
// inferred — range/default for scalars, the option list for enums:
//
//   layout(set = 0, binding = 0) uniform Tune {
//       //@tune 0..16 = 9.42
//       float uSunIntensity;
//       //@tune enum{ PBR, Albedo, Normal }
//       int   uDebugView;
//   } tune;
//
// Everything else is inferred: the member name + type (the declaration), the
// group (the enclosing `uniform <Block>` name), and the display label (from the
// name). An untagged member — or any block with no tags — is engine-driven and
// never surfaced.
//
// Why source-scanned, not reflected: spirv-cross sees the compiled module, where
// comments and intent are gone. The binding (offset/type) comes from SPIR-V
// reflection; this intent comes from the source. The diagnostics layer joins the
// two by name. GLSL-source tooling lives here in Blix.Graphics (next to the GLSL
// preprocessor / ShaderLoader), independent of any backend.
public enum TunableKind { Float, Int, Enum, Bool }

public sealed record ShaderTunable(
    string Name,
    string Block,
    TunableKind Kind,
    float Min,
    float Max,
    float Default,
    IReadOnlyList<string>? EnumNames = null)
{
    // Human label inferred from the name: drop a leading lowercase type prefix
    // ('u' before an uppercase letter), split camelCase into words, and present
    // sentence-case. "uSunIntensity" → "Sun intensity".
    public string Label => DeriveLabel(Name);

    internal static string DeriveLabel(string name)
    {
        var start = (name.Length >= 2 && name[0] == 'u' && char.IsUpper(name[1])) ? 1 : 0;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = start; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > start) sb.Append(' ');
            sb.Append(c);
        }
        if (sb.Length == 0) return name;
        // Sentence case: first char upper, the rest lower.
        var s = sb.ToString();
        return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
    }
}

public static class ShaderTunables
{
    // `//@tune <payload>` — the payload is everything after the marker.
    private static readonly Regex TagLine = new(@"//@tune\s+(.+?)\s*$", RegexOptions.Compiled);
    // A UBO/SSBO block opener: `... uniform <Block> {`. Captures the block name.
    private static readonly Regex BlockOpen = new(@"\buniform\s+(\w+)\s*\{", RegexOptions.Compiled);
    // A scalar member/uniform declaration: `<type> <name> ;` (ignoring layout/
    // qualifiers already consumed). Captures type + name.
    private static readonly Regex Decl = new(@"^\s*(?:layout\s*\([^)]*\)\s*)?(?:uniform\s+)?(float|int|uint|bool|vec2|vec3|vec4)\s+(\w+)\s*;", RegexOptions.Compiled);
    private static readonly Regex EnumPayload = new(@"^enum\s*\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex RangePayload = new(@"^([-+0-9.eE]+)\s*\.\.\s*([-+0-9.eE]+)\s*(?:=\s*([-+0-9.eE]+))?", RegexOptions.Compiled);

    public static IReadOnlyList<ShaderTunable> Scan(string glslSource)
    {
        ArgumentNullException.ThrowIfNull(glslSource);
        var result = new List<ShaderTunable>();
        var lines = glslSource.Replace("\r\n", "\n").Split('\n');

        var currentBlock = "";
        string? pendingTag = null;   // payload of a //@tune awaiting its declaration

        foreach (var raw in lines)
        {
            var line = raw;

            // Track the enclosing uniform-block name for grouping. A block opener
            // and a close brace don't carry a declaration we tag.
            var open = BlockOpen.Match(line);
            if (open.Success) { currentBlock = open.Groups[1].Value; continue; }
            if (line.Contains('}') && currentBlock.Length > 0 && !line.Contains('{'))
            {
                currentBlock = "";
                // a stray closing brace can't follow a tag; drop any pending one
                pendingTag = null;
                continue;
            }

            var tag = TagLine.Match(line);
            if (tag.Success) { pendingTag = tag.Groups[1].Value.Trim(); continue; }

            // Blank / pure-comment lines between the tag and the declaration are
            // tolerated; anything else must be the declaration the tag applies to.
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;

            if (pendingTag is { } payload)
            {
                var decl = Decl.Match(line);
                if (decl.Success)
                {
                    var type = decl.Groups[1].Value;
                    var name = decl.Groups[2].Value;
                    result.Add(Build(name, currentBlock, type, payload));
                }
                // Whether or not it parsed, the tag is consumed by the next decl.
                pendingTag = null;
            }
        }

        return result;
    }

    private static readonly Regex BoolPayload = new(
        @"^bool(?:\s*=\s*(\S+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static ShaderTunable Build(string name, string block, string type, string payload)
    {
        // <b>`//@tune bool` exists because most of these were never magnitudes.</b> Six of
        // VulkanSponza's eight dials switch something on or off, and every one of them was
        // declared `0..1` and drawn as a slider, because a toggle could not be asked for. A
        // slider over a boolean invites 0.37, which the shader silently rounds at its `> 0.5`
        // test, and it reads as an amount when it names a choice -- which is how one of them
        // acquired a DEFAULT of 1 that nobody intended as "use the other normal".
        //
        // The uniform stays a float carrying 0 or 1, so the shader side is unchanged and
        // AppendUniforms keeps writing one kind of thing.
        var bl = BoolPayload.Match(payload);
        if (bl.Success)
        {
            var on = bl.Groups[1].Success && ParseFloat(bl.Groups[1].Value) != 0f;
            return new ShaderTunable(name, block, TunableKind.Bool, 0, 1, on ? 1f : 0f);
        }

        var en = EnumPayload.Match(payload);
        if (en.Success)
        {
            var names = en.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new ShaderTunable(name, block, TunableKind.Enum,
                Min: 0, Max: Math.Max(0, names.Length - 1), Default: 0, EnumNames: names);
        }

        var rng = RangePayload.Match(payload);
        if (!rng.Success)
        {
            throw new FormatException(
                $"ShaderTunables: '{name}' has an unparseable //@tune payload: \"{payload}\".");
        }
        var min = ParseFloat(rng.Groups[1].Value);
        var max = ParseFloat(rng.Groups[2].Value);
        var def = rng.Groups[3].Success ? ParseFloat(rng.Groups[3].Value) : min;
        var kind = type == "float" ? TunableKind.Float : TunableKind.Int;
        return new ShaderTunable(name, block, kind, min, max, def);
    }

    private static float ParseFloat(string s) =>
        float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>
/// The `//@tune` metadata as a build artifact: written beside the .spv, read at load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scanning source is right; scanning it at RUNTIME was not.</b> The decorators are intent that
/// the compiled module cannot carry, so they have to come from the GLSL — but the build is where
/// the GLSL is, fully expanded, with every include resolved against the compiler's own -I paths.
/// Recovering them at load instead meant shipping every .vert/.frag/.comp into the application
/// output purely so something could re-read the text, and it could not see an engine-owned include
/// at all, because those are not staged. A tunable declared in a shared block was therefore
/// invisible, and <see cref="ShaderTunablePanel"/> answers zero for a name it never saw — so the
/// failure was a dial silently reading zero rather than an error.
/// </para>
/// <para>
/// This is the sidecar pattern the tree already uses twice: <c>.spv.refl.json</c> for the binding
/// table and <c>.blixapps.json</c> for app discovery. Run at build, write a sidecar, read it cheap.
/// </para>
/// </remarks>
public static class ShaderTunableSidecar
{
    /// <summary>The sidecar that belongs to a compiled shader: <c>&lt;name&gt;.spv.tune.json</c>.</summary>
    public static string PathFor(string spvPath) => spvPath + ".tune.json";

    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string ToJson(IReadOnlyList<ShaderTunable> tunables) =>
        System.Text.Json.JsonSerializer.Serialize(tunables, Options);

    /// <summary>
    /// Reads the sidecar beside a compiled shader. An absent file yields no tunables.
    /// </summary>
    /// <remarks>
    /// Absent is not an error: a shader with no decorators is the ordinary case, and a consumer
    /// that asks for tunables it does not have should get an empty panel rather than a throw.
    /// </remarks>
    public static IReadOnlyList<ShaderTunable> Load(string spvPath)
    {
        var path = PathFor(spvPath);
        if (!File.Exists(path)) return Array.Empty<ShaderTunable>();
        return System.Text.Json.JsonSerializer.Deserialize<ShaderTunable[]>(File.ReadAllText(path), Options)
               ?? Array.Empty<ShaderTunable>();
    }
}
