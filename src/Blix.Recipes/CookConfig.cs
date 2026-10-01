using System.Globalization;

namespace Blix.Recipes;

/// <summary>
/// What a project decides about each asset it cooks, in one file the project names.
/// </summary>
/// <remarks>
/// <para>
/// Every cook decision that is not a source-format fact or a renderer's lives here: how a mesh is
/// split, whether its UVs flip, and the material rules its source did not say (<see cref="MaterialPatch"/>'s
/// grammar — asserted counts, near-miss refusals, a source pin). The build, <c>blix cook</c> and a
/// pack's cook script all read it; nothing is discovered beside a source, because an input that
/// applies by sitting in the right folder is an invisible one.
/// </para>
/// <para>
/// <b>Grammar.</b> <c>#</c> starts a comment, which is where a decision's reason goes. <c>asset
/// &lt;source&gt; [-&gt; &lt;output&gt;]</c> opens an entry, the source relative to this file; the lines under
/// it, until the next <c>asset</c>, are its: <c>split &lt;triangles&gt;</c>, <c>split-extent &lt;metres&gt;</c>,
/// <c>split-foliage off</c>, <c>flip-v</c>, <c>source &lt;hash&gt;</c> and <c>material ...</c>. An asset the
/// file does not name cooks with glTF's defaults.
/// </para>
/// <para>
/// Each entry is stamped with the hash of its own text, so editing one asset's entry re-cooks that
/// asset and no other.
/// </para>
/// </remarks>
public sealed class CookConfig
{
    /// <summary>One asset and what the project decides about it.</summary>
    /// <param name="Source">The source's full path.</param>
    /// <param name="Output">Where a project cook writes it, relative to the output root; null for its own name.</param>
    /// <param name="Materials">Its material rules, or null when it has none.</param>
    public sealed record Entry(
        string Source, string? Output, bool FlipTextureV, int SplitTriBudget, bool SplitFoliage,
        float SplitMaxExtent, MaterialPatch? Materials, string Hash, int Line);

    private CookConfig(string path, IReadOnlyList<Entry> entries)
    {
        Path = path;
        Entries = entries;
    }

    public string Path { get; }

    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The entry for <paramref name="source"/>, or null when this file does not name it.</summary>
    public Entry? For(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var full = System.IO.Path.GetFullPath(source);
        return Entries.FirstOrDefault(e => string.Equals(e.Source, full, StringComparison.Ordinal));
    }

    /// <summary>Reads a cook configuration, refusing a malformed or duplicated entry by line.</summary>
    public static CookConfig Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"No cook configuration at {path}.", path);
        if (path.Contains(' ', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{path}: a cook configuration's path may not contain a space — the build passes it as a recipe option, and options are space-separated.");
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var lines = File.ReadAllText(path).Split('\n');
        var entries = new List<Entry>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        (string Source, string Written, string? Output, int Line)? open = null;
        var body = new List<(string Text, int Number)>();

        void Close()
        {
            if (open is not { } head) return;
            entries.Add(Build(path, head.Source, head.Written, head.Output, head.Line, body));
            body.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var hashAt = line.IndexOf('#');
            var content = (hashAt >= 0 ? line[..hashAt] : line).Trim();
            if (content.Length == 0) continue;

            var fields = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields[0] == "asset")
            {
                Close();
                var output = fields.Length == 4 && fields[2] == "->" ? fields[3] : null;
                if (fields.Length != 2 && output is null)
                    throw new InvalidDataException($"{path}:{i + 1}: expected 'asset <source>' or 'asset <source> -> <output>'.");
                var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, fields[1]));
                if (seen.TryGetValue(source, out var first))
                    throw new InvalidDataException($"{path}:{i + 1}: '{fields[1]}' already has an entry, at line {first}; an asset is decided once.");
                seen[source] = i + 1;
                open = (source, fields[1], output, i + 1);
                continue;
            }

            if (open is null)
                throw new InvalidDataException($"{path}:{i + 1}: '{fields[0]}' belongs to an asset, and no 'asset' line precedes it.");
            body.Add((content, i + 1));
        }

        Close();
        foreach (var e in entries)
        {
            if (!File.Exists(e.Source))
                throw new InvalidDataException($"{path}:{e.Line}: no source at {e.Source} — a mistyped entry must not cook as the defaults.");
        }

        return new CookConfig(path, entries);
    }

    private static Entry Build(
        string path, string source, string written, string? output, int line, List<(string Text, int Number)> body)
    {
        var flip = false;
        var split = 0;
        var foliage = true;
        var extent = MeshRecipe.DefaultSplitMaxExtent;
        var rules = new List<(string Text, int Number)>();
        foreach (var (text, number) in body)
        {
            var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (fields[0])
            {
                case "flip-v" when fields.Length == 1:
                    flip = true;
                    break;
                case "split" when fields.Length == 2 && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var n) && n >= 0:
                    split = n;
                    break;
                case "split-extent" when fields.Length == 2
                    && float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0:
                    extent = m;
                    break;
                case "split-foliage" when fields.Length == 2 && fields[1] is "on" or "off":
                    foliage = fields[1] == "on";
                    break;
                case "material" or "source":
                    rules.Add((text, number));
                    break;
                default:
                    throw new InvalidDataException(
                        $"{path}:{number}: '{text}' is not an entry line — expected split N, split-extent M, split-foliage on|off, flip-v, source <hash> or material ...");
            }
        }

        // The entry's own text as written — the source relative to this file, so the stamp is the same
        // in every checkout — without comments, so a comment edit does not re-cook and a decision edit does.
        var hash = MaterialPatch.Hash($"{written}\n{output}\n" + string.Join('\n', body.Select(b => b.Text)));
        var materials = rules.Count == 0 ? null : MaterialPatch.FromLines(path, rules, hash, "config");
        return new Entry(source, output, flip, split, foliage, extent, materials, hash, line);
    }
}
