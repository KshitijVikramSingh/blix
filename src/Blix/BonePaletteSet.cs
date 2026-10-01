using System.Numerics;

namespace Blix;

// Several bodies' bone palettes in ONE buffer, so one instanced draw can pose them all.
//
// ── The decision being named ────────────────────────────────────────────────
// Instance `i`'s matrices start at `i * JointCount`. That single sentence is restated in two
// languages in every consumer — a C# loop that packs at some stride, and a GLSL line that
// reads `gl_InstanceIndex * BONE_COUNT` — and **nothing checks that the two agree**. When
// they disagree the bodies do not vanish or throw; they render as other bodies' poses,
// smeared, which reads as a skinning bug anywhere but where it is.
//
// Three consumers already live with that:
//   • Bulwark packs `i * EnemyBones * 16` floats and hard-codes `#define BONE_COUNT 15` in
//     two shaders, guarded by a throw at load if the asset disagrees. The throw exists
//     *because* the number is in three places.
//   • the external RTSGame consumer packs into `paletteScratch[count * JointCount ..]` and multiplies
//     `gl_InstanceIndex * int(uSkin.x)` — same contract, joint count passed as data.
//   • The toolchain lab is the third.
//
// ── What is deliberately NOT named ──────────────────────────────────────────
// **Where the model matrix lives.** Bulwark bakes it into the palette (`skin × model`, a
// world-space palette and no instance buffer at all); the external RTSGame consumer keeps the palette in model
// space and carries `model` and `tint` in a separate instance buffer. Those are two
// algorithms, not two copies of one — a set that insisted on either would force one shape
// onto the other, which is the mistake conventions §4 names with the turret rigs.
//
// So this owns the STRIDE and the packing, and has no opinion about what is packed. A
// caller writing world-space palettes and a caller writing model-space ones use it
// identically.
public sealed class BonePaletteSet
{
    /// <summary>Matrices per instance: the skin's joint count, not its skeleton's bone count. Instance i occupies <c>[i * JointCount, (i+1) * JointCount)</c>.</summary>
    /// <remarks>
    /// Joints, because a palette has one matrix per skin joint and a skeleton can have bones that are no joint of
    /// this skin (BrainStem: 18 joints, 19 bones). A stride read off the skeleton renders bodies 1 and up from
    /// other bodies' matrices; body 0 hides it, its base being zero either way.
    /// </remarks>
    public int JointCount { get; }

    /// <summary>How many instances the buffer was sized for.</summary>
    public int Capacity { get; }

    /// <summary>Every instance's matrices, back to back. Length is <c>Capacity * JointCount</c>.</summary>
    public Matrix4x4[] Matrices { get; }

    /// <summary>How many instances have been written since the last <see cref="Reset"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Bytes an upload should send: only the live prefix, not the whole capacity.</summary>
    /// <remarks>
    /// A short write is legal and is the point. Uploading the full capacity to draw one body is how
    /// an instance buffer comes to cost megabytes a frame for nothing — measured elsewhere in this
    /// tree at 3 ms a frame to upload 111 MB of unused tail.
    /// </remarks>
    public int LiveMatrixCount => Count * JointCount;


    public BonePaletteSet(int jointCount, int capacity)
    {
        if (jointCount < 0) throw new ArgumentOutOfRangeException(nameof(jointCount), "Joint count must be non-negative.");
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least one.");
        JointCount = jointCount;
        Capacity = capacity;
        Matrices = new Matrix4x4[capacity * jointCount];
    }

    /// <summary>Forget every written instance. Call once per frame, before the first <see cref="Add"/>.</summary>
    public void Reset() => Count = 0;

    /// <summary>Adds one body for <paramref name="skin"/>, its palette from the skin's skeleton's bone worlds; returns the body's slot.</summary>
    /// <remarks>
    /// A palette is a binding operation: the joints and their inverse binds are the skin's, the worlds are
    /// its skeleton's. There is no overload taking a skeleton and a pose, because a skeleton cannot say which
    /// skin's binds to apply.
    /// </remarks>
    /// <param name="skin">The skin being packed; its joint count is this set's stride.</param>
    /// <param name="worlds">This body's bone worlds, of the skin's own skeleton (<see cref="BoneWorlds.Compute"/>).</param>
    /// <param name="post">What goes after every world: the hierarchy's placement, then the body's.</param>
    /// <exception cref="ArgumentException">The skin's joint count is not this set's stride, or the worlds are another skeleton's.</exception>
    /// <exception cref="InvalidOperationException">The set is full.</exception>
    public int Add(SkinBinding skin, BoneWorlds worlds, Matrix4x4 post)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(worlds);
        if (skin.JointCount != JointCount)
        {
            throw new ArgumentException(
                $"A {skin.JointCount}-joint skin; this set is packed at a stride of {JointCount}.", nameof(skin));
        }

        if (Count >= Capacity)
        {
            // Loud rather than silently dropping the body. A crowd that quietly stops growing at
            // capacity is a bug that only shows up as "the last few enemies are invisible".
            throw new InvalidOperationException(
                $"BonePaletteSet is full at {Capacity} instance(s). Size it for the crowd, or stop adding.");
        }

        skin.ComputePalette(worlds, post, Matrices.AsSpan(Count * JointCount, JointCount));
        return Count++;
    }

    /// <summary>
    /// A stable hash of one instance's matrices — "is this body in a different pose from that one?"
    /// </summary>
    /// <remarks>
    /// <b>A test that can only pass is not a test.</b> Three bodies drawn from one buffer look
    /// independent as long as they are posed differently, and the way to be sure the mechanism works
    /// rather than the picture flattering it is to check both directions: different clips must give
    /// different fingerprints, and the same clip at the same time must give identical ones. The second
    /// half is the negative control, and without it a set that quietly wrote every instance to slot 0
    /// would pass the first half whenever the poses happened to differ.
    /// <para>
    /// Deliberately not a cryptographic hash and deliberately not stable across runtimes — it exists to
    /// compare two slices in one process, not to be recorded. Bit-exact on the float pattern rather than
    /// tolerant, because two instances that differ by a rounding error are two instances that were
    /// computed separately, which is exactly the claim.
    /// </para>
    /// </remarks>
    public ulong Fingerprint(int instance) => Fingerprint(Slice(instance));

    /// <summary>The same hash over any matrices: bone worlds, when the question is about poses and not a skin.</summary>
    public static ulong Fingerprint(ReadOnlySpan<Matrix4x4> matrices)
    {
        // FNV-1a over the raw float bits. Cheap, order-sensitive, and a single changed bone moves it.
        // Read as a float span over the matrices rather than member by member: no per-bone array, and
        // it cannot silently skip a component the way an enumerated list of sixteen names can.
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(matrices);
        var hash = 1469598103934665603UL;
        foreach (var value in floats)
        {
            hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(value)) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>One instance's matrices, as a window onto the shared array.</summary>
    public Span<Matrix4x4> Slice(int instance)
    {
        if (instance < 0 || instance >= Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instance), instance, $"This set holds {Capacity} instance(s).");
        }

        return Matrices.AsSpan(instance * JointCount, JointCount);
    }
}
