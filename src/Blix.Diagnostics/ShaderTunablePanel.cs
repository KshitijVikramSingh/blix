using Blix.Graphics;

namespace Blix.Diagnostics;

// Auto-builds the diagnostics overlay's shader-variable dials from a shader's
// `//@tune` decorators (scanned by ShaderTunables) and feeds the live values
// back as named uniforms. A game declares its tunables in the shader and
// registers the panel once — instead of hand-wiring a C# field + a dial + a
// UBO pack per variable. The binding (offset/type) is resolved downstream by
// the name-keyed uniform write; this panel owns only the value and the UI.
//
//   var panel = new ShaderTunablePanel(ShaderTunables.Scan(litFragSource));
//   // in IDebuggable.Debug:  panel.BuildControls(debug);
//   // per frame:             panel.AppendUniforms(perFrame);   // by name → UBO
//   // CPU-side read:         panel.Value("uVisualizeCascades")
public sealed class ShaderTunablePanel
{
    private readonly IReadOnlyList<ShaderTunable> tunables;
    private readonly Dictionary<string, float> values = new();
    // First-seen block order + members in declaration order, so the overlay is
    // stable frame-to-frame rather than hash-ordered.
    private readonly List<(string Group, List<ShaderTunable> Items)> groups = new();

    public ShaderTunablePanel(IReadOnlyList<ShaderTunable> tunables)
    {
        ArgumentNullException.ThrowIfNull(tunables);
        this.tunables = tunables;

        var byGroup = new Dictionary<string, List<ShaderTunable>>();
        foreach (var t in tunables)
        {
            values[t.Name] = t.Default;
            var key = string.IsNullOrEmpty(t.Block) ? "Tune" : t.Block;
            if (!byGroup.TryGetValue(key, out var list))
            {
                list = new List<ShaderTunable>();
                byGroup[key] = list;
                groups.Add((key, list));
            }
            list.Add(t);
        }
    }

    public IReadOnlyList<ShaderTunable> Tunables => tunables;

    // Current edited value of a tunable by uniform name — for CPU-side reads
    // (e.g. gating a debug gizmo on a visualize toggle). 0 if unknown.
    public float Value(string name) => values.TryGetValue(name, out var v) ? v : 0f;

    /// <summary>Sets a tunable's value, for a caller that knows the name.</summary>
    /// <remarks>
    /// <b>So a shader dial can be driven by something other than a hand on a slider.</b> These are
    /// live-tuning controls, which is exactly right while somebody is looking at the image and
    /// exactly wrong for a measurement: an A/B run has no overlay, so a term that only exists as a
    /// tunable cannot be measured at all. Returns false for a name the shader does not declare,
    /// which is the difference between setting a dial and silently setting nothing.
    /// </remarks>
    public bool TrySetValue(string name, float value)
    {
        if (!values.ContainsKey(name)) return false;
        values[name] = value;
        return true;
    }

    // Register a control per tunable, grouped by block. Call from
    // IDebuggable.Debug. Float → slider, enum → dropdown; edits read back in.
    public void BuildControls(DebugContext debug)
    {
        ArgumentNullException.ThrowIfNull(debug);
        foreach (var (group, items) in groups)
        {
            using (debug.Scope(group))
            {
                foreach (var t in items)
                {
                    if (t.Kind == TunableKind.Enum && t.EnumNames is { Count: > 0 } names)
                    {
                        values[t.Name] = debug.Controls.Enum(t.Label, (int)values[t.Name], names);
                    }
                    else
                    {
                        values[t.Name] = debug.Controls.Float(t.Label, values[t.Name], t.Min, t.Max);
                    }
                }
            }
        }
    }

    // Append the live values as named uniforms for the per-frame write path.
    public void AppendUniforms(ICollection<ShaderUniform> dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        foreach (var t in tunables)
        {
            dst.Add(new ShaderUniform(t.Name, new FloatUniform(values[t.Name])));
        }
    }
}
