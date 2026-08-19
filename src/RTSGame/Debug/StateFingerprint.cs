using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using RTSGame.Simulation.Agents;

namespace RTSGame.Debug;

/// <summary>
/// Somewhere to put the values that make up a simulation's state, in order, so that
/// two runs can be compared by a single number.
/// </summary>
/// <remarks>
/// Two shapes of sink implement this: one folds the values into a 64-bit hash and
/// throws the labels away, which is what running the check costs; the other keeps
/// every label and value, which is what explaining a failure costs. The walk over
/// the state is written once, against this interface, so the cheap check and the
/// expensive explanation can never disagree about what the state <em>is</em> — which
/// is the failure mode of every hand-written pair of them.
/// <para>
/// Labels arrive as an already-existing string and an integer, never as something
/// formatted, so the folding sink pays nothing for information it discards.
/// </para>
/// </remarks>
internal interface IStateSink
{
    /// <summary>Enters a named group — a subsystem, or one numbered element of one.</summary>
    void Push(string group, int index);

    /// <summary>Contributes one field's value.</summary>
    void Add(string field, ulong value);
}

/// <summary>
/// Typed conveniences over <see cref="IStateSink"/>, so the walk reads as the state
/// it is describing rather than as a series of casts.
/// </summary>
/// <remarks>
/// Floats go in as their bits rather than as their values. A determinism check that
/// compared them numerically would forgive the two differences most worth catching:
/// a negative zero where a positive one belongs, which survives a division and comes
/// back out as a sign; and a NaN, which compares unequal to itself and would make a
/// world differ from a bit-identical copy of itself.
/// </remarks>
internal static class StateSinkExtensions
{
    public static void Add<TSink>(this ref TSink sink, string field, int value)
        where TSink : struct, IStateSink => sink.Add(field, (uint)value);

    public static void Add<TSink>(this ref TSink sink, string field, long value)
        where TSink : struct, IStateSink => sink.Add(field, (ulong)value);

    public static void Add<TSink>(this ref TSink sink, string field, bool value)
        where TSink : struct, IStateSink => sink.Add(field, value ? 1UL : 0UL);

    public static void Add<TSink>(this ref TSink sink, string field, float value)
        where TSink : struct, IStateSink => sink.Add(field, BitConverter.SingleToUInt32Bits(value));

    public static void Add<TSink>(this ref TSink sink, string field, Vector2 value)
        where TSink : struct, IStateSink
    {
        sink.Add(field, BitConverter.SingleToUInt32Bits(value.X));
        sink.Add(field, BitConverter.SingleToUInt32Bits(value.Y));
    }
}

/// <summary>Folds state into one 64-bit number and keeps nothing else.</summary>
/// <remarks>
/// FNV-1a, mixed a byte at a time. Order-sensitive by construction, which matters more
/// here than the usual reasons to want a good hash: the divergence a lockstep simulation
/// produces most often is the same values visited in a different order, and a
/// commutative fold — a sum, an exclusive-or over whole words — is exactly blind to it.
/// </remarks>
internal struct FoldingStateSink : IStateSink
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong hash = Offset;

    public FoldingStateSink()
    {
    }

    public ulong Value => hash;

    /// <summary>Mixes one value in. Public and static because the compiled field readers call it.</summary>
    public static ulong Mix(ulong accumulator, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            accumulator = (accumulator ^ ((value >> shift) & 0xFF)) * Prime;
        }

        return accumulator;
    }

    /// <summary>
    /// The group name is folded in, but the index is not — the index is already implied by
    /// the order values arrive in, and folding it as well would only slow the check down.
    /// </summary>
    public void Push(string group, int index)
    {
        foreach (var character in group) hash = Mix(hash, character);
    }

    public void Add(string field, ulong value) => hash = Mix(hash, value);
}

/// <summary>Keeps every value with the label it arrived under, for explaining a mismatch.</summary>
internal struct TracingStateSink : IStateSink
{
    private string group;

    public TracingStateSink()
    {
        group = string.Empty;
        Entries = new List<(string Label, ulong Value)>();
    }

    public List<(string Label, ulong Value)> Entries { get; }

    public void Push(string group, int index) =>
        this.group = index < 0 ? group : $"{group}[{index}]";

    public void Add(string field, ulong value) =>
        Entries.Add((group.Length == 0 ? field : $"{group}.{field}", value));
}

/// <summary>
/// Walks any piece of plain data into a sink at runtime, for state too small and too
/// varied to be worth compiling a schema for.
/// </summary>
/// <remarks>
/// The queue of pending orders is what this exists for. A command is a record carrying a
/// few ids and a point, there are rarely more than a handful in flight, and — the part
/// that matters — the jobs layer is going to add more kinds of them. A switch over
/// command types would be one more list to remember to extend, which is the failure this
/// whole file is a response to, so the fields are read off whatever record turns up.
/// </remarks>
internal static class PlainDataWalker
{
    private const BindingFlags Instance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Deep enough for a record holding a struct holding a vector, and no deeper.</summary>
    private const int DepthLimit = 8;

    public static void Write<TSink>(ref TSink sink, string label, object? value, int depth = 0)
        where TSink : struct, IStateSink
    {
        if (depth > DepthLimit)
        {
            throw new NotSupportedException(
                $"{label} nests deeper than the determinism fingerprint will walk. Plain data " +
                "is expected here; something has a reference to a live subsystem in it.");
        }

        if (value is null)
        {
            sink.Add(label, ulong.MaxValue);
            return;
        }

        var type = value.GetType();
        // The concrete type goes in, so two commands with identical fields and different
        // meanings — stop against follow, walk-here against work-here — are not the same value.
        foreach (var character in type.Name) sink.Add(label, character);

        switch (value)
        {
            case bool flag: sink.Add(label, flag); return;
            case float number: sink.Add(label, number); return;
            case double number: sink.Add(label, BitConverter.DoubleToUInt64Bits(number)); return;
            case Vector2 vector: sink.Add(label, vector); return;
            case string text:
                foreach (var character in text) sink.Add(label, character);
                return;
        }

        if (type.IsEnum || type.IsPrimitive)
        {
            sink.Add(label, Bits(type.IsEnum
                ? Convert.ChangeType(value, type.GetEnumUnderlyingType())
                : value));
            return;
        }

        if (value is System.Collections.IEnumerable sequence)
        {
            var index = 0;
            foreach (var element in sequence)
            {
                sink.Push(label, index++);
                Write(ref sink, label, element, depth + 1);
            }

            sink.Add(label, index);
            return;
        }

        foreach (var field in Fields(type))
        {
            Write(ref sink, field.Name, field.GetValue(value), depth + 1);
        }
    }

    /// <summary>
    /// An integer's bits, unsigned and unchecked. <c>Convert.ToUInt64</c> would throw on the
    /// first negative one, and a negative id is normal — <c>AgentId(-1)</c> is how "nobody"
    /// is spelled.
    /// </summary>
    private static ulong Bits(object value) => value switch
    {
        int number => (uint)number,
        uint number => number,
        long number => (ulong)number,
        ulong number => number,
        short number => (ushort)number,
        ushort number => number,
        sbyte number => (byte)number,
        byte number => number,
        char character => character,
        nint number => (ulong)(long)number,
        nuint number => (ulong)number,
        _ => throw new NotSupportedException($"No fingerprint defined for {value.GetType()}."),
    };

    /// <summary>
    /// Instance fields, ordered by name. Runtime reflection promises nothing about the
    /// order it hands fields back in, and a fingerprint that quietly depended on it would
    /// be one more thing to wonder about the day this has to agree across two machines.
    /// </summary>
    private static IEnumerable<FieldInfo> Fields(Type type) => type
        .GetFields(Instance)
        .Where(field => !field.IsStatic)
        .OrderBy(field => field.Name, StringComparer.Ordinal);
}

/// <summary>
/// Every value an <see cref="AgentState"/> carries, discovered from the struct itself
/// rather than listed by hand.
/// </summary>
/// <remarks>
/// This exists because the rule it serves kept sliding. "The determinism test grows with
/// each system" was owed something for three sessions running, and the reason is
/// structural rather than anybody's forgetfulness: the test compared five fields of a
/// seventy-field struct, and the cost of extending it fell on whoever added the
/// sixty-first — separately from, and later than, the work that added it. A rule whose
/// cost is paid in a different commit from the change that incurs it is a rule that will
/// be owed something.
/// <para>
/// So the list is not a list. Every field of the struct is walked down to its primitive
/// leaves and every leaf is read, which means a field added by the jobs layer is covered
/// by the determinism check the moment it is declared, without anybody remembering
/// anything. The one thing that cannot be automated — a field of a type this cannot
/// reduce, a reference to something living elsewhere — is refused loudly at construction
/// instead of being skipped quietly, so the alarm fires in the session that adds it.
/// </para>
/// <para>
/// Proof that the coverage is real rather than claimed is in
/// <c>SimulationSelfTests.FingerprintReadsEveryBodyField</c>, which perturbs each leaf in
/// turn and asserts the fingerprint notices. A digest nobody has tried to fool is not
/// evidence of anything.
/// </para>
/// </remarks>
internal static class AgentStateSchema
{
    /// <summary>One primitive value inside <see cref="AgentState"/>, and how to get at it.</summary>
    /// <param name="Name">Dotted path from the struct root, e.g. <c>Colliders.Movement</c>.</param>
    /// <param name="Path">Field chain from the struct root to the leaf, for perturbation.</param>
    /// <param name="Read">Compiled reader; reflection is paid once here rather than per tick.</param>
    internal sealed record Leaf(string Name, FieldInfo[] Path, Func<AgentState, ulong> Read);

    private const BindingFlags Instance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IReadOnlyList<Leaf> Leaves { get; } = Build();

    /// <summary>
    /// Whether a type is plain data all the way down — reducible to numbers, flags and
    /// enums with nothing living inside it.
    /// </summary>
    /// <remarks>
    /// The determinism census uses this to tell a field it need not think about from one it
    /// must: a point, an id or a cell can be fingerprinted wherever it turns up, whereas a
    /// subsystem has to be walked or argued about by name.
    /// </remarks>
    public static bool IsPlainData(Type type)
    {
        if (type.IsEnum || type.IsPrimitive) return true;
        if (!type.IsValueType || type.IsGenericType) return false;
        return type.GetFields(Instance)
            .Where(field => !field.IsStatic)
            .All(field => IsPlainData(field.FieldType));
    }

    /// <summary>Writes one body's state to a sink, leaf by leaf.</summary>
    public static void Write<TSink>(ref TSink sink, in AgentState agent, int slot)
        where TSink : struct, IStateSink
    {
        sink.Push("body", slot);
        // Tombstones included. A dead slot's fields are neutralised on despawn and nothing
        // should touch them again, so a difference in one is a write to a unit that has left
        // the world — which is precisely the class of bug the tombstone scheme exists to make
        // survivable, and worth knowing about rather than skipping past.
        var state = agent;
        foreach (var leaf in Leaves) sink.Add(leaf.Name, leaf.Read(state));
    }

    /// <summary>
    /// Returns <paramref name="agent"/> with one leaf changed, for testing that the
    /// fingerprint actually reads it.
    /// </summary>
    /// <remarks>
    /// Reflection through boxed copies, which is slow and does not matter: this runs once
    /// per field in one self-test, never in a tick.
    /// </remarks>
    public static AgentState Perturb(in AgentState agent, Leaf leaf) =>
        (AgentState)Set(agent, leaf.Path, 0)!;

    private static object? Set(object container, FieldInfo[] path, int depth)
    {
        var field = path[depth];
        if (depth == path.Length - 1)
        {
            field.SetValue(container, Different(field.GetValue(container)!));
            return container;
        }

        // A struct read out of a field is a copy, so the mutated copy has to be written
        // back into its parent on the way out. Anything less silently perturbs nothing,
        // which would make this probe pass by doing nothing at all.
        var inner = field.GetValue(container)!;
        field.SetValue(container, Set(inner, path, depth + 1)!);
        return container;
    }

    /// <summary>Some other value of the same type, guaranteed unequal to this one.</summary>
    /// <remarks>
    /// An enum is nudged through its underlying integer and put back, without caring whether
    /// the result names a declared member. The question being asked is whether the
    /// fingerprint read the field, not whether the value means anything, and requiring a
    /// legal value would mean this needed teaching about every enum the simulation grows.
    /// </remarks>
    private static object Different(object value)
    {
        var type = value.GetType();
        if (type.IsEnum)
        {
            var underlying = Convert.ChangeType(value, type.GetEnumUnderlyingType());
            return Enum.ToObject(type, Different(underlying));
        }

        return value switch
        {
            float number => number == 0f ? 1f : number * -2f,
            int number => number + 1,
            uint number => number + 1u,
            long number => number + 1L,
            ulong number => number + 1UL,
            short number => (short)(number + 1),
            ushort number => (ushort)(number + 1),
            sbyte number => (sbyte)(number + 1),
            byte number => (byte)(number + 1),
            bool flag => !flag,
            _ => throw new NotSupportedException(
                $"No perturbation defined for {type}. The determinism fingerprint reads this " +
                "field but cannot prove that it does; add a case here."),
        };
    }

    private static List<Leaf> Build()
    {
        var leaves = new List<Leaf>();
        var root = Expression.Parameter(typeof(AgentState), "agent");
        Collect(typeof(AgentState), root, Array.Empty<FieldInfo>(), string.Empty, leaves, root);

        // Sorted by path rather than left in reflection order. The runtime makes no promise
        // about field order, and while both worlds in one process would see the same order
        // whatever it is, a fingerprint that quietly depends on an unspecified ordering is
        // one more thing to wonder about when this eventually has to agree across two
        // machines. Sorting also makes the census output stable to read.
        leaves.Sort((first, second) => string.CompareOrdinal(first.Name, second.Name));
        return leaves;
    }

    private static void Collect(
        Type type,
        Expression access,
        FieldInfo[] path,
        string prefix,
        List<Leaf> leaves,
        ParameterExpression root)
    {
        if (TryReadPrimitive(type, access) is { } read)
        {
            leaves.Add(new Leaf(
                prefix,
                path,
                Expression.Lambda<Func<AgentState, ulong>>(read, root).Compile()));
            return;
        }

        if (!type.IsValueType || type.IsPrimitive)
        {
            throw new NotSupportedException(
                $"AgentState.{prefix} is a {type.Name}, which the determinism fingerprint cannot " +
                "reduce to values. Agent state has to be plain data for the fingerprint to cover " +
                "it without being told to: give this field a value type, or move what it points " +
                "at into a subsystem the world-level walk in DeterminismCheck describes " +
                "explicitly.");
        }

        foreach (var field in type.GetFields(Instance))
        {
            if (field.IsStatic) continue;
            Collect(
                field.FieldType,
                Expression.Field(access, field),
                path.Append(field).ToArray(),
                prefix.Length == 0 ? Name(field) : $"{prefix}.{Name(field)}",
                leaves,
                root);
        }
    }

    /// <summary>Positional record-struct members arrive as <c>&lt;Value&gt;k__BackingField</c>.</summary>
    private static string Name(FieldInfo field)
    {
        var name = field.Name;
        if (!name.StartsWith('<')) return name;
        var end = name.IndexOf('>');
        return end > 1 ? name[1..end] : name;
    }

    /// <summary>An expression hashing <paramref name="access"/>, or null if it is not a leaf.</summary>
    private static Expression? TryReadPrimitive(Type type, Expression access)
    {
        if (type.IsEnum)
        {
            // Enums go in as their underlying integer, so adding a locomotion state or an
            // activity kind needs nothing here.
            return AsUnsigned(Expression.Convert(access, type.GetEnumUnderlyingType()));
        }

        if (type == typeof(float))
        {
            return Expression.Convert(
                Expression.Call(
                    typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToUInt32Bits))!,
                    access),
                typeof(ulong));
        }

        if (type == typeof(bool))
        {
            return Expression.Condition(
                access,
                Expression.Constant(1UL),
                Expression.Constant(0UL));
        }

        return type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(byte) || type == typeof(sbyte)
            ? AsUnsigned(access)
            : null;
    }

    /// <summary>
    /// Widens through the unsigned form of the same width, so a negative number keeps its
    /// bits instead of being sign-extended into a value some other field could also produce.
    /// </summary>
    private static Expression AsUnsigned(Expression access) => access.Type switch
    {
        var type when type == typeof(int) => Expression.Convert(
            Expression.Convert(access, typeof(uint)), typeof(ulong)),
        var type when type == typeof(short) => Expression.Convert(
            Expression.Convert(access, typeof(ushort)), typeof(ulong)),
        var type when type == typeof(sbyte) => Expression.Convert(
            Expression.Convert(access, typeof(byte)), typeof(ulong)),
        var type when type == typeof(long) => Expression.Convert(access, typeof(ulong)),
        _ => Expression.Convert(access, typeof(ulong)),
    };
}
