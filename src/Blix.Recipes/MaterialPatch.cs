using System.Text;
using System.Text.RegularExpressions;
using Blix.Assets;

namespace Blix.Recipes;

/// <summary>
/// What a scene needs an asset to say that its author did not.
/// </summary>
/// <remarks>
/// <para>
/// An asset's material rules — corrections or additions that are neither source-format facts nor
/// renderer heuristics — as one entry of a project's <see cref="CookConfig"/> states them. They are
/// applied once while cooking and included in provenance.
/// </para>
/// <para>
/// Rules match material names, may assert an expected match count, and fail when they match nothing.
/// The entry's hash is recorded in the cooked stamp so tools can identify the exact policy applied.
/// </para>
/// <para>
/// This type owns parsing, matching, application, and refusal mechanics. Asset names, selectors,
/// intended values, and the configuration itself remain with the project.
/// </para>
/// <para>
/// Only <c>material</c> rules are implemented. The grammar retains an explicit kind so unsupported
/// targets are refused rather than accidentally interpreted as material policy.
/// </para>
/// </remarks>
public sealed class MaterialPatch
{
    /// <summary>One rule: what to match, how many to expect, and what to say about them.</summary>
    public sealed record Rule(
        string Kind,
        string Selector,
        int? ExpectedCount,
        IReadOnlyList<KeyValuePair<string, string>> Assignments,
        int Line);

    private MaterialPatch(string path, string contentHash, string? sourcePin, IReadOnlyList<Rule> rules, string stampKey)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        ContentHash = contentHash;
        SourcePin = sourcePin;
        Rules = rules;
        this.stampKey = stampKey;
    }

    private readonly string stampKey;

    public string Path { get; }
    public string FileName { get; }

    /// <summary>Short hash of the rules' own text, for the cook stamp.</summary>
    public string ContentHash { get; }

    /// <summary>
    /// The source hash this patch was written against, if it declared one.
    /// </summary>
    /// <remarks>
    /// Name matching catches renames but not a source that keeps names while changing meaning.
    /// An optional source pin guards that case and is enforced when present.
    /// </remarks>
    public string? SourcePin { get; }

    public IReadOnlyList<Rule> Rules { get; }

    /// <summary>What the stamp records, so a cooked artifact can say what decided it and which version.</summary>
    public string StampFragment => $"{stampKey}={FileName}@{ContentHash}";

    /// <summary>Reads rules from text in this grammar; <paramref name="origin"/> names it in refusals.</summary>
    /// <remarks>Throws <see cref="InvalidDataException"/> on a malformed line, naming it.</remarks>
    public static MaterialPatch Parse(string text, string origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(origin);
        var lines = text.Split('\n').Select((line, i) => (line, i + 1)).ToArray();
        return FromLines(origin, lines, Hash(text), "rules");
    }

    /// <summary>The rules of one cook-configuration entry, stamped as that configuration's.</summary>
    internal static MaterialPatch FromLines(string origin, IReadOnlyList<(string Text, int Number)> lines, string hash, string stampKey)
    {
        var rules = new List<Rule>();
        string? sourcePin = null;
        foreach (var (raw, number) in lines)
        {
            // Everything after '#' is the REASON, and keeping it is the point: a rule whose purpose
            // nobody recorded gets deleted by the next person who tidies up.
            var line = raw;
            var hashAt = line.IndexOf('#');
            if (hashAt >= 0) line = line[..hashAt];
            line = line.Trim();
            if (line.Length == 0) continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields[0] == "source")
            {
                if (fields.Length != 2)
                    throw new InvalidDataException($"{origin}:{number}: 'source' takes exactly one hash.");
                sourcePin = fields[1];
                continue;
            }

            if (fields[0] != "material")
                throw new InvalidDataException(
                    $"{origin}:{number}: unknown kind '{fields[0]}'. Only 'material' is understood today.");
            if (fields.Length < 3)
                throw new InvalidDataException(
                    $"{origin}:{number}: expected 'material <selector> <key>=<value> ...'.");

            // A selector may declare how many it expects to match: column_*{8}. A glob that silently
            // grows from eight materials to forty is the over-matching failure this whole file is
            // meant to avoid, and only the author knows the right number.
            var selector = fields[1];
            int? expected = null;
            var brace = selector.IndexOf('{');
            if (brace >= 0)
            {
                if (!selector.EndsWith('}') || !int.TryParse(selector[(brace + 1)..^1], out var n))
                    throw new InvalidDataException($"{origin}:{number}: malformed expected count in '{selector}'.");
                expected = n;
                selector = selector[..brace];
            }

            var assignments = new List<KeyValuePair<string, string>>();
            foreach (var f in fields.Skip(2))
            {
                var eq = f.IndexOf('=');
                if (eq <= 0 || eq == f.Length - 1)
                    throw new InvalidDataException($"{origin}:{number}: '{f}' is not key=value.");
                assignments.Add(new KeyValuePair<string, string>(f[..eq], f[(eq + 1)..]));
            }
            rules.Add(new Rule("material", selector, expected, assignments, number));
        }

        return new MaterialPatch(origin, hash, sourcePin, rules, stampKey);
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    /// <summary>Refuses if the patch pinned a source hash and the source has changed since.</summary>
    public void RequireSource(string actualSourceHash)
    {
        if (SourcePin is null) return;
        if (string.Equals(SourcePin, actualSourceHash, StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException(
            $"{Path} was written against source {SourcePin}, and the source is now {actualSourceHash}. " +
            "Re-check the rules against the changed asset, then update the 'source' line. " +
            "A patch that applies to an asset its author no longer describes is how a rule keeps " +
            "matching a name whose meaning moved.");
    }

    /// <summary>Applies every rule, returning a new table. Throws if any rule matches nothing.</summary>
    public BlixMeshMaterial[] Apply(BlixMeshMaterial[] materials, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var result = (BlixMeshMaterial[])materials.Clone();

        var names = materials.Select(m => m.Name).ToArray();
        foreach (var rule in Rules)
        {
            var hits = Match(rule, names);
            foreach (var i in hits)
                foreach (var (key, value) in rule.Assignments)
                    result[i] = ApplyOne(result[i], key, value, rule, Path);

            log?.Invoke(
                $"  patch {rule.Selector} -> {hits.Count} material(s): " +
                string.Join(" ", rule.Assignments.Select(a => $"{a.Key}={a.Value}")));
        }
        return result;
    }

    /// <summary>
    /// Which of <paramref name="materialNames"/> the patch declares to carry a DirectX-convention
    /// normal map (<c>normal=directx</c>), by index; the last rule to state a material's convention wins.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Apply"/> because the convention is not a material property: it is how
    /// the material's normal IMAGE is to be read, and the image is cooked before, and apart from, the
    /// material table. Rules match with the same refusals as <see cref="Apply"/>.
    /// </remarks>
    public IReadOnlySet<int> DirectXNormalMaterials(IReadOnlyList<string> materialNames)
    {
        ArgumentNullException.ThrowIfNull(materialNames);
        var directX = new HashSet<int>();
        foreach (var rule in Rules)
        {
            var hits = Match(rule, materialNames);
            foreach (var (key, value) in rule.Assignments)
            {
                if (key != "normal") continue;
                var isDirectX = NormalConvention(value, rule, Path);
                foreach (var i in hits)
                {
                    if (isDirectX) directX.Add(i);
                    else directX.Remove(i);
                }
            }
        }

        return directX;
    }

    // Every rule matches through here, so Apply and DirectXNormalMaterials refuse the same way.
    private List<int> Match(Rule rule, IReadOnlyList<string> names)
    {
        var pattern = GlobToRegex(rule.Selector);
        var hits = new List<int>();
        for (var i = 0; i < names.Count; i++)
            if (pattern.IsMatch(names[i])) hits.Add(i);

        if (hits.Count == 0)
        {
            // Named, with near misses, because "your rule matched nothing" is only actionable if
            // it also says what was there. This is the whole difference from a silent heuristic.
            var near = names.OrderBy(n => Distance(n, rule.Selector)).Take(5);
            throw new InvalidDataException(
                $"{Path}:{rule.Line}: '{rule.Selector}' matched no material. " +
                $"Closest names present: {string.Join(", ", near)}.");
        }
        if (rule.ExpectedCount is { } want && hits.Count != want)
        {
            throw new InvalidDataException(
                $"{Path}:{rule.Line}: '{rule.Selector}' expected {want} material(s) and matched " +
                $"{hits.Count}: {string.Join(", ", hits.Select(h => names[h]))}.");
        }

        return hits;
    }

    private static bool NormalConvention(string value, Rule rule, string path) => value switch
    {
        "directx" => true,
        "opengl" => false,
        _ => throw new InvalidDataException(
            $"{path}:{rule.Line}: normal takes 'directx' or 'opengl', not '{value}'."),
    };

    private static BlixMeshMaterial ApplyOne(
        BlixMeshMaterial m, string key, string value, Rule rule, string path)
    {
        var x = m.Ext;
        switch (key)
        {
            // Base metallic-roughness values can be corrected explicitly instead of through
            // renderer-wide thresholds.
            case "metallic":  return m with { MetallicFactor = F(value) };
            case "roughness": return m with { RoughnessFactor = F(value) };

            case "transmission":  return m with { TransmissionFactor = F(value), Extensions = x with { TransmissionFactor = F(value) } };
            case "ior":           return m with { Extensions = x with { IndexOfRefraction = F(value) } };
            case "sheen":         return m with { Extensions = x with { SheenColorFactor = V3(value) } };
            case "sheenRoughness": return m with { Extensions = x with { SheenRoughnessFactor = F(value) } };
            case "diffuseTransmission":      return m with { Extensions = x with { DiffuseTransmissionFactor = F(value) } };
            case "diffuseTransmissionColor": return m with { Extensions = x with { DiffuseTransmissionColorFactor = V3(value) } };

            // The one texture a patch may author, and it names an image the asset ALREADY has
            // rather than introducing one.
            //
            // <b>Why this exists.</b> lit.frag used to shade the transmitted lobe as
            // diffuseTransmissionColor * baseColor, which gave a leaf card its per-texel variation
            // for free -- and squared the tint the moment a scene authored the colour, because the
            // value authored was the leaf's own measured albedo. Removing the multiply made the
            // renderer obey the spec and cost that variation. This is how a scene asks for it back
            // as a decision it states, instead of one the renderer assumes for everybody.
            //
            // <b>Why not a file path.</b> A cooked material's image fields are rows in THIS file's
            // image table, built while cooking from the images the source actually carries. Naming
            // an outside file would mean growing that table, cooking a texture that no material
            // references, and inventing a resolution rule for the path -- none of which the use
            // case needs, since the image wanted is always one the asset already ships.
            case "diffuseTransmissionColorTexture":
                return value switch
                {
                    "baseColor" => m with { Extensions = x with { DiffuseTransmissionColorImage = m.BaseColorImage } },
                    "none" => m with { Extensions = x with { DiffuseTransmissionColorImage = BlixMesh.NoImage } },
                    _ => throw new InvalidDataException(
                        $"{path}:{rule.Line}: diffuseTransmissionColorTexture takes 'baseColor' or " +
                        $"'none', not '{value}'. A patch may point this at an image the asset " +
                        "already carries; it cannot introduce one."),
                };
            case "thickness":           return m with { Extensions = x with { ThicknessFactor = F(value) } };
            case "attenuationDistance": return m with { Extensions = x with { AttenuationDistance = F(value) } };
            case "attenuationColor":    return m with { Extensions = x with { AttenuationColor = V3(value) } };
            case "specular":       return m with { Extensions = x with { SpecularFactor = F(value) } };
            case "specularColor":  return m with { Extensions = x with { SpecularColorFactor = V3(value) } };
            case "clearcoat":          return m with { Extensions = x with { ClearcoatFactor = F(value) } };
            case "clearcoatRoughness": return m with { Extensions = x with { ClearcoatRoughnessFactor = F(value) } };
            case "unlit":          return m with { Extensions = x with { Unlit = B(value) } };

            // The normal map's green convention. Not a material property: the cook reads it through
            // DirectXNormalMaterials and flips the IMAGE once, so the table is unchanged here and the
            // cooked map is glTF's convention. `opengl` is the spec's default, stated to record that
            // the map was checked.
            case "normal":
                NormalConvention(value, rule, path);
                return m;
            default:
                throw new InvalidDataException(
                    $"{path}:{rule.Line}: unknown material key '{key}'.");
        }

        float F(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f)
            ? f
            : throw new InvalidDataException($"{path}:{rule.Line}: '{s}' is not a number.");

        bool B(string s) => s is "1" or "true" or "yes";

        System.Numerics.Vector3 V3(string s)
        {
            var p = s.Split(',');
            if (p.Length != 3) throw new InvalidDataException($"{path}:{rule.Line}: '{s}' is not r,g,b.");
            return new System.Numerics.Vector3(F(p[0]), F(p[1]), F(p[2]));
        }
    }

    /// <summary>Glob to regex: '*' spans anything, everything else is literal. Case-insensitive.</summary>
    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        foreach (var c in glob) sb.Append(c == '*' ? ".*" : Regex.Escape(c.ToString()));
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Crude edit distance, only ever used to order suggestions in an error message.</summary>
    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
