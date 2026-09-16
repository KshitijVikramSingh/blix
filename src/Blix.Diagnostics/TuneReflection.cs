using System.Linq;
using System.Reflection;
using System.Text;

namespace Blix.Diagnostics;

// Kind of control a tunable maps to in the overlay.
public enum TuneKind { Float, Int, Bool, Enum, Button, Text }

// Marks a C# field or property as a live-tunable value — the CPU twin of a
// shader `//@tune` decorator. The diagnostics overlay reflects these off a
// registered object, builds a control per member, and reads/writes the value
// straight back through the member:
//   float/int → slider (range required),  bool → toggle,  enum → dropdown,
//   bool + Action = true → button (set for the one frame it is clicked).
//
// The button kind exists because DebugControls has had a real ImGui.Button all
// along and this layer never surfaced it, so anything declaring its knobs with
// [Tune] had to spell an action as a checkbox — which is not what a checkbox
// means and does not read as one thing you press.
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

    /// <summary>
    /// Renders this bool as a button rather than a checkbox: an action, not a state.
    /// </summary>
    /// <remarks>
    /// The member is set true for the single frame the button is clicked and false otherwise, so a reader
    /// does the obvious thing — test it, act, and let it fall back by itself. A checkbox for an action asks
    /// the user to tick something and then guess whether anything happened, and asks the reader to remember
    /// to untick it.
    /// </remarks>
    public bool Action { get; init; }

    /// <summary>How many characters a string member accepts. Ignored by every other kind.</summary>
    public int MaxLength { get; init; } = 128;

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

/// <summary>One declared value moving, and where it moved from.</summary>
/// <param name="Name">The member's name — the same one the flag and the panel are derived from.</param>
public readonly record struct TunableChange(string Name, object From, object To)
{
    public override string ToString() => $"{Name}: {From} -> {To}";
}

/// <summary>
/// A subject that wants to hear when its own declared state is driven from outside.
/// </summary>
/// <remarks>
/// <b>One path in.</b> A value can be written by a flag at startup, by a panel mid-session, or by a
/// frame being replayed, and a subject implementing this cannot tell which — because it should not
/// have to. There is no separate initialisation hook for the same reason: applying a flag IS a
/// change, reported from the value the declaration started at.
/// <para>
/// Reported per change rather than per frame, deliberately. Blix says what moved; whether eight
/// flags at startup should cause one recompute or eight is an opinion about a particular tool, and
/// a tool that wants to coalesce sets a flag and acts in its own update — which is what it would
/// do anyway.
/// </para>
/// </remarks>
public interface ITunable
{
    void OnChanged(TunableChange change);
}

// A reflected tunable bound to a live object: presentation metadata + a value
// accessor that reads/writes the underlying member directly. The value is
// float-backed across all kinds (bool = 0/1, enum = option index); `Kind` tells
// the renderer which control to use. Editing `Value` mutates the source object.
public sealed class TunableField
{
    private readonly Func<float> get;
    private readonly Action<float> set;
    private readonly Func<string>? getText;
    private readonly Action<string>? setText;

    internal TunableField(string name, string label, string group, TuneKind kind,
        float min, float max, IReadOnlyList<string>? enumNames, Func<float> get, Action<float> set,
        Func<string>? getText = null, Action<string>? setText = null, int maxLength = 0)
    {
        this.getText = getText;
        this.setText = setText;
        MaxLength = maxLength;
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

    /// <summary>Buffer size for <see cref="TuneKind.Text"/>; zero for every other kind.</summary>
    public int MaxLength { get; }

    /// <summary>
    /// What this member held when it was first reflected — the value its declaration starts it at.
    /// </summary>
    /// <remarks>
    /// Read from the member rather than declared on the attribute, because the field initializer
    /// already says it: <c>[Tune(0, 1)] public float Weight = 0.5f</c> has stated where Weight
    /// begins, and repeating it in the attribute would give it two answers that can disagree.
    /// <para>
    /// It is also the <c>From</c> of the first change, which is what makes a change report readable
    /// at startup rather than only after the second edit.
    /// </para>
    /// </remarks>
    public object Initial { get; internal set; } = 0f;

    /// <summary>The value now, whichever kind this is — a float, or the string for Text.</summary>
    public object Current => Kind == TuneKind.Text ? Text : Value;

    public float Value
    {
        get => get();
        set => set(value);
    }

    /// <summary>
    /// The string behind a <see cref="TuneKind.Text"/> member.
    /// </summary>
    /// <remarks>
    /// A second accessor rather than a stringified <see cref="Value"/>, because every other kind is
    /// float-backed and genuinely is a number — a bool is 0/1, an enum is an option index. Text is
    /// the one kind that is not, and pretending otherwise would put a parse in the middle of every
    /// read. Reading it on any other kind throws rather than returning something plausible.
    /// </remarks>
    public string Text
    {
        get => getText is null
            ? throw new InvalidOperationException($"{Name} is {Kind}, not Text.")
            : getText();
        set
        {
            if (setText is null) throw new InvalidOperationException($"{Name} is {Kind}, not Text.");
            setText(value ?? string.Empty);
        }
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
                result.Add(new TunableField(
                    member.Name, label, group, tune.Action ? TuneKind.Button : TuneKind.Bool, 0f, 1f, null,
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
            else if (valueType == typeof(string))
            {
                // Numeric-free, so no range is required and none is meaningful. The bound a string
                // has is a buffer length, and it is carried separately for exactly that reason.
                result.Add(new TunableField(member.Name, label, group, TuneKind.Text, 0f, 0f, null,
                    () => 0f,
                    _ => { },
                    () => (string?)getRaw() ?? string.Empty,
                    v => setRaw(v ?? string.Empty),
                    Math.Max(1, tune.MaxLength)));
            }
            else
            {
                throw new InvalidOperationException(
                    $"[Tune] on {type.Name}.{member.Name}: unsupported type {valueType.Name} " +
                    "(float, int, bool, enum, or string).");
            }
        }
        foreach (var field in result) field.Initial = field.Current;
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

    // Which subject each field belongs to, so a change is reported to the object that owns it
    // rather than to all of them. An ObjectTunables over three settings objects is normal.
    private readonly Dictionary<TunableField, object> owners = new();

    public ObjectTunables(params object[] targets)
    {
        // Seeded after reflection below, so the first BuildControls reports a quiet frame
        // rather than every field at once.
        ArgumentNullException.ThrowIfNull(targets);
        var byGroup = new Dictionary<string, List<TunableField>>();
        foreach (var target in targets)
        {
            foreach (var field in TuneReflection.Reflect(target))
            {
                owners[field] = target;
                if (!byGroup.TryGetValue(field.Group, out var list))
                {
                    list = new List<TunableField>();
                    byGroup[field.Group] = list;
                    groups.Add((field.Group, list));
                }
                list.Add(field);
            }
        }

        foreach (var (_, items) in groups)
        {
            foreach (var f in items) lastSeen[f] = Snapshot(f);
        }
    }

    /// <summary>Every group and how many controls it holds, so "my slider is missing" is answerable.</summary>
    /// <remarks>
    /// A control that does not appear has two quite different causes — never registered, or registered and
    /// scrolled off the end of a panel with ten groups in it — and they are indistinguishable from the chair.
    /// </remarks>
    public string Describe() => string.Join(
        ", ",
        groups.Select(g => $"{g.Group}[{string.Join(" ", g.Items.Select(i => $"{i.Label}:{i.Kind}"))}]"));

    /// <summary>
    /// Did any declared value move since the last <see cref="BuildControls"/>?
    /// </summary>
    /// <remarks>
    /// <b>The one piece of bookkeeping a tool cannot do for itself and should not have to.</b>
    /// Declared state is written from several places — a panel this frame, a command-line
    /// argument at startup, a sink replaying a frame — and something downstream almost always
    /// has to recompute when it moves. Today that is a hand-written call after every write: the
    /// rig viewer has eight of them, and the capture tool that composes the same state has none,
    /// because nothing reminded it.
    /// <para>
    /// This is the reader of every declared member already, so it is the only thing positioned to
    /// answer. It says a value MOVED; it has no opinion about what that should cause, which is
    /// what keeps it bookkeeping rather than policy.
    /// </para>
    /// </remarks>
    public bool Changed { get; private set; }

    /// <summary>What moved, for a reader that wants to recompute only part of itself.</summary>
    public IReadOnlyList<string> ChangedNames => changedNames;

    private readonly List<string> changedNames = new();

    // Last frame's value per field. Comparing across FRAMES rather than across the control
    // round-trip is the whole of what makes this useful: a before/after within one frame only
    // ever sees what the panel itself did, and the write a tool most needs to hear about comes
    // from somewhere else — an argument at startup, a replayed frame, another system. Both look
    // identical from here, and should.
    private readonly Dictionary<TunableField, object> lastSeen = new();

    /// <summary>
    /// Drive declared state from a command line, and hand back what was not recognised.
    /// </summary>
    /// <remarks>
    /// <b>The second face of one declaration.</b> A panel and a flag are the same member rendered
    /// two ways, and the range that stops a slider leaving its bounds is the range that validates
    /// <c>--weight 3</c>. Nothing here is written per tool, which is the whole point: the reason
    /// one tool took <c>--mask</c> and another took <c>--mask-from</c> is that both were
    /// hand-written a release apart.
    /// <para>
    /// Flags come from the MEMBER name rather than the label — <c>MaskRoot</c> is
    /// <c>--mask-root</c>, not <c>--Mask root</c> — because a label is for reading and a flag is
    /// for typing.
    /// </para>
    /// <para>
    /// Unrecognised arguments are returned rather than rejected. A tool has flags of its own that
    /// are not state (<c>--frames</c>, <c>--out</c>), and this has no business knowing them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A declared flag was given a value it cannot hold. Loud, because the alternative is a tool
    /// that silently ran with a default while its command line said otherwise.
    /// </exception>
    public string[] Apply(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var byFlag = new Dictionary<string, TunableField>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, items) in groups)
        {
            foreach (var f in items)
            {
                if (f.Kind != TuneKind.Button) byFlag[FlagFor(f.Name)] = f;
            }
        }

        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!byFlag.TryGetValue(args[i], out var field))
            {
                rest.Add(args[i]);
                continue;
            }

            var before = Snapshot(field);

            if (field.Kind == TuneKind.Bool)
            {
                // Presence is true, which is how every hand-written flag in this tree already
                // behaves. An explicit "--lockstep false" still works when it is spelled.
                var explicitValue = i + 1 < args.Length && bool.TryParse(args[i + 1], out var parsed);
                field.Value = explicitValue && !bool.Parse(args[++i]) ? 0f : 1f;
            }
            else
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} needs a value after it.", nameof(args));
                }

                AssignFrom(field, args[i], args[++i]);
            }

            var after = Snapshot(field);
            lastSeen[field] = after;
            if (!Equals(before, after)) Mark(field, before, after);
        }

        return rest.ToArray();
    }

    /// <summary>Every flag this reports, for a caller that wants to print usage.</summary>
    public IEnumerable<(string Flag, TuneKind Kind, string? Options)> Flags()
    {
        foreach (var (_, items) in groups)
        {
            foreach (var f in items)
            {
                if (f.Kind == TuneKind.Button) continue;
                var options = f.Kind switch
                {
                    TuneKind.Enum => string.Join("|", f.EnumNames ?? Array.Empty<string>()),
                    TuneKind.Float or TuneKind.Int => $"{f.Min}..{f.Max}",
                    TuneKind.Text => $"text[{f.MaxLength}]",
                    _ => null,
                };
                yield return (FlagFor(f.Name), f.Kind, options);
            }
        }
    }

    private static void AssignFrom(TunableField field, string flag, string raw)
    {
        switch (field.Kind)
        {
            case TuneKind.Text:
                field.Text = raw;
                break;

            case TuneKind.Enum:
            {
                var names = field.EnumNames ?? Array.Empty<string>();
                var at = -1;
                for (var n = 0; n < names.Count; n++)
                {
                    if (string.Equals(names[n], raw, StringComparison.OrdinalIgnoreCase)) at = n;
                }

                if (at < 0)
                {
                    throw new ArgumentException(
                        $"{flag} is one of {string.Join(", ", names)} — not '{raw}'.", nameof(raw));
                }

                field.Value = at;
                break;
            }

            default:
            {
                if (!float.TryParse(raw, out var number))
                {
                    throw new ArgumentException($"{flag} takes a number, not '{raw}'.", nameof(raw));
                }

                // Clamped rather than refused, and the same clamp a slider gets. A range says what
                // the value MEANS; arguing with a command line about it helps nobody.
                field.Value = Math.Clamp(number, field.Min, field.Max);
                break;
            }
        }
    }

    /// <summary>MaskRoot becomes --mask-root; flySpeed becomes --fly-speed.</summary>
    public static string FlagFor(string member)
    {
        var sb = new StringBuilder("--");
        for (var i = 0; i < member.Length; i++)
        {
            var c = member[i];
            if (char.IsUpper(c) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Members this tool draws better itself. Still declared, still flagged, still reported.
    /// </summary>
    /// <remarks>
    /// <b>[Tune] declares state, not a widget.</b> A generated control is a floor rather than a
    /// ceiling: a mask root rendered as a text field is worse than one rendered as a combo of the
    /// bones this particular rig actually has, and no amount of reflection can know that list.
    /// <para>
    /// Naming a member here suppresses only the GENERATED control. The flag still exists, the
    /// change is still detected and still reported, and the tool writes the member from whatever
    /// control it drew instead — so a better widget costs a tool nothing but the widget.
    /// </para>
    /// </remarks>
    public ISet<string> Bespoke { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Every declared member name found across the bound targets.</summary>
    public IEnumerable<string> MemberNames => groups.SelectMany(g => g.Items).Select(f => f.Name);

    /// <summary>
    /// Throws unless every name given is actually declared by one of the bound targets.
    /// </summary>
    /// <remarks>
    /// <b>For the failure that has no symptom.</b> A caller naming members in strings — a Bespoke
    /// list, a replay, a recorded flag — keeps compiling after those members move to another type,
    /// because <c>nameof</c> only produces text. The binding is then silently gone: the flag parses
    /// to nothing, the panel draws nothing, and a run with the flag looks exactly like a run
    /// without it. That happened when N bodies were split out of one animation and five knobs went
    /// with them.
    /// </remarks>
    public void RequireDeclared(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var known = new HashSet<string>(MemberNames, StringComparer.Ordinal);
        var missing = names.Where(n => !known.Contains(n)).ToArray();
        if (missing.Length == 0) return;

        throw new ArgumentException(
            $"No bound target declares [Tune] {string.Join(", ", missing)}. "
            + $"Bound targets declare: {string.Join(", ", known.OrderBy(x => x, StringComparer.Ordinal))}.",
            nameof(names));
    }

    public void BuildControls(DebugContext debug)
    {
        ArgumentNullException.ThrowIfNull(debug);
        Changed = false;
        changedNames.Clear();

        foreach (var (group, items) in groups)
        {
            using (debug.Scope(group))
            {
                foreach (var f in items)
                {
                    // Detected either way — only the control is skipped. A member the tool draws
                    // itself is still state, and the whole point is that it stays one member.
                    if (Bespoke.Contains(f.Name))
                    {
                        Note(f);
                        continue;
                    }

                    if (f.Kind == TuneKind.Text)
                    {
                        f.Text = debug.Controls.Text(f.Label, f.Text, f.MaxLength);
                        Note(f);
                        continue;
                    }

                    f.Value = f.Kind switch
                    {
                        // A button reports the frame it was clicked and nothing else, so the member it is
                        // bound to is true for exactly that frame — no state to hold and none to reset.
                        TuneKind.Button => debug.Controls.Button(f.Label) ? 1f : 0f,
                        TuneKind.Bool => debug.Controls.Toggle(f.Label, f.Value != 0f) ? 1f : 0f,
                        TuneKind.Enum => debug.Controls.Enum(f.Label, (int)f.Value, f.EnumNames!),
                        _ => debug.Controls.Float(f.Label, f.Value, f.Min, f.Max),
                    };

                    // A button is true for exactly the frame it is pressed, so across frames it
                    // moves twice per press and the second move is the release. Only the press
                    // is news.
                    if (f.Kind == TuneKind.Button)
                    {
                        if (f.Value != 0f) Mark(f, 0f, 1f);
                        lastSeen[f] = Snapshot(f);
                    }
                    else Note(f);
                }
            }
        }
    }

    private void Note(TunableField f)
    {
        var now = Snapshot(f);
        if (lastSeen.TryGetValue(f, out var before) && !Equals(before, now)) Mark(f, before, now);
        lastSeen[f] = now;
    }

    private static object Snapshot(TunableField f) =>
        f.Kind == TuneKind.Text ? f.Text : f.Value;

    private void Mark(TunableField f, object from, object to)
    {
        Changed = true;
        changedNames.Add(f.Name);

        // Reported to the subject that OWNS the field, not to every target — an ObjectTunables
        // over three settings objects is normal, and two of them have no business hearing about
        // the third's slider.
        if (owners.TryGetValue(f, out var owner) && owner is ITunable tunable)
        {
            tunable.OnChanged(new TunableChange(f.Name, from, to));
        }
    }
}
