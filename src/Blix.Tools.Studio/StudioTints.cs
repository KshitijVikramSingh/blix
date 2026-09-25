using System.Numerics;

namespace Blix.Tools.Studio;

/// <summary>
/// A colour per material NAME, overriding what the file said.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because a kit asset often ships no colour at all.</b> All 40 materials across this tree's
/// nature kit are <c>baseColorFactor = (1,1,1,1)</c> with no texture: the file carries geometry,
/// baked vertex occlusion, and a material name. Something downstream turns <c>"Grass"</c> into
/// <c>(0.110, 0.155, 0.060)</c>. A viewer that renders those files faithfully therefore shows white
/// scenery and is, correctly, useless for looking at them — which is a strange place for the tool
/// whose job is looking at assets.
/// </para>
/// <para>
/// <b>Keyed on the NAME, which is what keeps a game out of this.</b> The external RTSGame consumer's material table
/// holds the real table and the studio must not reference a game. But the name is the surface a game
/// tints against, so a panel that lists names and takes colours shows exactly that surface while
/// knowing nothing about any game: audition a green, read off the RGB, put it in the game's table.
/// The engine owns the mechanism, the caller declares the policy.
/// </para>
/// <para>
/// <b>Session-only, deliberately.</b> Persisting would make this a store of colours, which is what
/// the game's table already is — a second one that can disagree with it is worse than none. What is
/// being auditioned here is a number to go and write down somewhere else.
/// </para>
/// </remarks>
public sealed class StudioTints
{
    private readonly Dictionary<string, Vector3> overrides = new(StringComparer.Ordinal);

    /// <summary>How many materials currently differ from what their file said.</summary>
    public int Count => overrides.Count;

    /// <summary>The colour to draw a part with: the override if there is one, else the asset's own.</summary>
    /// <remarks>
    /// <b>Takes the asset's colour rather than returning null.</b> A view should not have to write
    /// the "or the one from the file" branch four times — the failure that shape produces is one call
    /// site that forgets, and a part that ignores the panel for no visible reason.
    /// </remarks>
    public Vector3 Resolve(string materialName, Vector3 assetColour) =>
        materialName.Length > 0 && overrides.TryGetValue(materialName, out var tint) ? tint : assetColour;

    /// <summary>Override one material's colour. An empty name is ignored rather than stored.</summary>
    /// <remarks>
    /// Ignored because a nameless material cannot be told apart from another nameless one, and a
    /// single entry under "" would tint every one of them together — which looks like a bug in the
    /// panel rather than a property of the file.
    /// </remarks>
    public void Set(string materialName, Vector3 colour)
    {
        if (materialName.Length == 0) return;
        overrides[materialName] = colour;
    }

    /// <summary>Drop one material's override, returning it to what the file said.</summary>
    public void Clear(string materialName) => overrides.Remove(materialName);

    /// <summary>Drop every override.</summary>
    public void ClearAll() => overrides.Clear();

    /// <summary>Whether this material is currently being overridden.</summary>
    public bool Has(string materialName) => overrides.ContainsKey(materialName);
}
