using System.Linq;
using System.Reflection;
using System.Text;

namespace Blix.Diagnostics;

// Kind of control a tunable maps to in the overlay.
public enum TuneKind { Float, Int, Bool, Enum }

// Marks a C# field or property as a live-tunable value — the CPU twin of a
// shader `//@tune` decorator. The diagnostics overlay reflects these off a
// registered object, builds a control per member, and reads/writes the value
// straight back through the member:
//   float/int → slider (range required),  bool → toggle,  enum → dropdown.
//
//   sealed class FogSettings {
//       [Tune(0, 0.5)] public float Density = 0.1f;   // slider
//       [Tune]         public bool  Enabled = true;   // toggle
//       [Tune]         public TonemapMode Tonemap;    // dropdown
//   }
//
// Opt-in: only attributed members surface. A numeric range is required (it's
// what reflection can't infer); bool/enum imply their range. Label defaults to
// the de-camelCased member name; Group to the owner's type name (a trailing
// "Settings" is stripped, so FogSettings → "Fog").
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class TuneAttribute : Attribute
{
    public float Min { get; }
    public float Max { get; }
    public bool HasRange { get; }
    public string? Label { get; init; }
    public string? Group { get; init; }

    // bool / enum members — range is implied.
    public TuneAttribute() { }

    // float / int members — range is required.
    public TuneAttribute(double min, double max)
    {
        Min = (float)min;
        Max = (float)max;
        HasRange = true;
    }
}

// A reflected tunable bound to a live object: presentation metadata + a value
// accessor that reads/writes the underlying member directly. The value is
// float-backed across all kinds (bool = 0/1, enum = option index); `Kind` tells
// the renderer which control to use. Editing `Value` mutates the source object.
public sealed class TunableField
{
    private readonly Func<float> get;
    private readonly Action<float> set;

    internal TunableField(string name, string label, string group, TuneKind kind,
        float min, float max, IReadOnlyList<string>? enumNames, Func<float> get, Action<float> set)
    {
        Name = name;
        Label = label;
        Group = group;
        Kind = kind;
        Min = min;
        Max = max;
        EnumNames = enumNames;
        this.get = get;
        this.set = set;
    }

    public string Name { get; }
    public string Label { get; }
    public string Group { get; }
    public TuneKind Kind { get; }
    public float Min { get; }
    public float Max { get; }
    public IReadOnlyList<string>? EnumNames { get; }

    public float Value
    {
        get => get();
        set => set(value);
    }
}

public static class TuneReflection
{
    private const BindingFlags MemberFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Reflect every [Tune]-attributed field/property on `target` into a live
    // TunableField. Declaration order is preserved.
    public static IReadOnlyList<TunableField> Reflect(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var type = target.GetType();
        var defaultGroup = StripSettings(type.Name);
        var result = new List<TunableField>();

        foreach (var member in EnumerateMembers(type))
        {
            var tune = member.GetCustomAttribute<TuneAttribute>();
            if (tune is null) continue;

            var (valueType, getRaw, setRaw) = AccessorsFor(member, target);
            var label = tune.Label ?? DeriveLabel(member.Name);
            var group = tune.Group ?? defaultGroup;

            if (valueType == typeof(bool))
            {
                result.Add(new TunableField(member.Name, label, group, TuneKind.Bool, 0f, 1f, null,
                    () => (bool)getRaw()! ? 1f : 0f,
                    v => setRaw(v >= 0.5f)));
            }
            else if (valueType.IsEnum)
            {
                var names = Enum.GetNames(valueType);
                var values = Enum.GetValues(valueType);
                result.Add(new TunableField(member.Name, label, group, TuneKind.Enum,
                    0f, Math.Max(0, names.Length - 1), names,
                    () => Math.Max(0, Array.IndexOf(values, getRaw())),
                    v => setRaw(values.GetValue(Math.Clamp((int)MathF.Round(v), 0, names.Length - 1))!)));
            }
            else if (valueType == typeof(float) || valueType == typeof(int))
            {
                if (!tune.HasRange)
                {
                    throw new InvalidOperationException(
                        $"[Tune] on {type.Name}.{member.Name}: numeric members need a (min, max) range.");
                }
                var isInt = valueType == typeof(int);
                result.Add(new TunableField(member.Name, label, group,
                    isInt ? TuneKind.Int : TuneKind.Float, tune.Min, tune.Max, null,
                    () => Convert.ToSingle(getRaw()),
                    v => setRaw(isInt ? (object)(int)MathF.Round(v) : v)));
            }
            else
            {
                throw new InvalidOperationException(
                    $"[Tune] on {type.Name}.{member.Name}: unsupported type {valueType.Name} (float, int, bool, or enum).");
            }
        }
        return result;
    }

    private static string StripSettings(string typeName) =>
        typeName.Length > 8 && typeName.EndsWith("Settings", StringComparison.Ordinal)
            ? typeName[..^8]
            : typeName;

    // Fields then properties, each in metadata (≈ declaration) order.
    private static IEnumerable<MemberInfo> EnumerateMembers(Type type)
    {
        foreach (var f in type.GetFields(MemberFlags)) yield return f;
        foreach (var p in type.GetProperties(MemberFlags)) yield return p;
    }

    private static (Type ValueType, Func<object?> Get, Action<object?> Set) AccessorsFor(
        MemberInfo member, object target)
    {
        switch (member)
        {
            case FieldInfo f:
                return (f.FieldType, () => f.GetValue(target), v => f.SetValue(target, v));
            case PropertyInfo p when p.CanRead && p.CanWrite:
                return (p.PropertyType, () => p.GetValue(target), v => p.SetValue(target, v));
            default:
                throw new InvalidOperationException(
                    $"[Tune] on {member.DeclaringType?.Name}.{member.Name}: a property must have both a getter and a setter.");
        }
    }

    // "density" → "Density", "flySpeed" → "Fly speed", "_moveSpeed" → "Move speed".
    // Splits at lower→upper boundaries, drops a leading underscore, sentence-cases.
    internal static string DeriveLabel(string name)
    {
        var start = name.Length > 0 && name[0] == '_' ? 1 : 0;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = start; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > start && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        if (sb.Length == 0) return name;
        var s = sb.ToString();
        return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
    }
}

// Renders one or more [Tune]-tagged objects into the overlay — grouped (by the
// type's stripped name or the tag's Group), one control per member, reading
// edits straight back through the member. Construct once with the target
// objects; call BuildControls each frame from IDebuggable.Debug. Replaces the
// hand-wired `debug.Controls.X(...)` + backing-field + write triplet per knob.
public sealed class ObjectTunables
{
    private readonly List<(string Group, List<TunableField> Items)> groups = new();

    public ObjectTunables(params object[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var byGroup = new Dictionary<string, List<TunableField>>();
        foreach (var target in targets)
        {
            foreach (var field in TuneReflection.Reflect(target))
            {
                if (!byGroup.TryGetValue(field.Group, out var list))
                {
                    list = new List<TunableField>();
                    byGroup[field.Group] = list;
                    groups.Add((field.Group, list));
                }
                list.Add(field);
            }
        }
    }

    /// <summary>Every group and how many controls it holds, so "my slider is missing" is answerable.</summary>
    /// <remarks>
    /// A control that does not appear has two quite different causes — never registered, or registered and
    /// scrolled off the end of a panel with ten groups in it — and they are indistinguishable from the chair.
    /// </remarks>
    public string Describe() => string.Join(", ", groups.Select(g => $"{g.Group}({g.Items.Count})"));

    public void BuildControls(DebugContext debug)
    {
        ArgumentNullException.ThrowIfNull(debug);
        foreach (var (group, items) in groups)
        {
            using (debug.Scope(group))
            {
                foreach (var f in items)
                {
                    f.Value = f.Kind switch
                    {
                        TuneKind.Bool => debug.Controls.Toggle(f.Label, f.Value != 0f) ? 1f : 0f,
                        TuneKind.Enum => debug.Controls.Enum(f.Label, (int)f.Value, f.EnumNames!),
                        _ => debug.Controls.Float(f.Label, f.Value, f.Min, f.Max),
                    };
                }
            }
        }
    }
}
