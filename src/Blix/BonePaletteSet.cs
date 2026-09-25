using System.Numerics;

namespace Blix;

// Several bodies' bone palettes in ONE buffer, so one instanced draw can pose them all.
//
// ── The decision being named ────────────────────────────────────────────────
// Instance `i`'s matrices start at `i * BoneCount`. That single sentence is restated in two
// languages in every consumer — a C# loop that packs at some stride, and a GLSL line that
// reads `gl_InstanceIndex * BONE_COUNT` — and **nothing checks that the two agree**. When
// they disagree the bodies do not vanish or throw; they render as other bodies' poses,
// smeared, which reads as a skinning bug anywhere but where it is.
//
// Three consumers already live with that:
//   • Bulwark packs `i * EnemyBones * 16` floats and hard-codes `#define BONE_COUNT 15` in
//     two shaders, guarded by a throw at load if the asset disagrees. The throw exists
//     *because* the number is in three places.
//   • the external RTSGame consumer packs into `paletteScratch[count * BoneCount ..]` and multiplies
//     `gl_InstanceIndex * int(uSkin.x)` — same contract, bone count passed as data.
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
    /// <summary>Matrices per instance. Instance i occupies <c>[i * BoneCount, (i+1) * BoneCount)</c>.</summary>
    public int BoneCount { get; }

    /// <summary>How many instances the buffer was sized for.</summary>
    public int Capacity { get; }

    /// <summary>Every instance's matrices, back to back. Length is <c>Capacity * BoneCount</c>.</summary>
    public Matrix4x4[] Matrices { get; }

    /// <summary>How many instances have been written since the last <see cref="Reset"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Bytes an upload should send: only the live prefix, not the whole capacity.</summary>
    /// <remarks>
    /// A short write is legal and is the point. Uploading the full capacity to draw one body is how
    /// an instance buffer comes to cost megabytes a frame for nothing — measured elsewhere in this
    /// tree at 3 ms a frame to upload 111 MB of unused tail.
    /// </remarks>
    public int LiveMatrixCount => Count * BoneCount;

    private readonly BonePalette scratch;

    public BonePaletteSet(int boneCount, int capacity)
    {
        if (boneCount < 0) throw new ArgumentOutOfRangeException(nameof(boneCount), "Bone count must be non-negative.");
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least one.");
        BoneCount = boneCount;
        Capacity = capacity;
        Matrices = new Matrix4x4[capacity * boneCount];
        scratch = new BonePalette(boneCount);
    }

    /// <summary>Forget every written instance. Call once per frame, before the first <see cref="Add"/>.</summary>
    public void Reset() => Count = 0;

    /// <summary>
    /// Computes <paramref name="pose"/>'s palette into the next free slot and returns that slot's index.
    /// </summary>
    /// <param name="post">
    /// Multiplied onto every matrix after the palette is built — the caller's chance to bake a world
    /// placement in (Bulwark's shape). Pass <see cref="Matrix4x4.Identity"/> to keep the palette in
    /// model space and place the body some other way (the external RTSGame consumer's shape). Row-vector compose: the
    /// palette is applied first, then this.
    /// </param>
    /// <returns>The instance index, which is what a shader's gl_InstanceIndex must equal.</returns>
    public int Add(Skeleton skeleton, Pose pose, Matrix4x4 post)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        if (skeleton.BoneCount != BoneCount)
        {
            throw new ArgumentException(
                $"Skeleton has {skeleton.BoneCount} bones; this set is packed at a stride of {BoneCount}.",
                nameof(skeleton));
        }
        if (Count >= Capacity)
        {
            // Loud rather than silently dropping the body. A crowd that quietly stops growing at
            // capacity is a bug that only shows up as "the last few enemies are invisible".
            throw new InvalidOperationException(
                $"BonePaletteSet is full at {Capacity} instance(s). Size it for the crowd, or stop adding.");
        }

        skeleton.ComputeBonePalette(pose, scratch);
        var at = Count * BoneCount;
        for (var b = 0; b < BoneCount; b++)
        {
            Matrices[at + b] = scratch.Matrices[b] * post;
        }

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
    public ulong Fingerprint(int instance)
    {
        // FNV-1a over the raw float bits. Cheap, order-sensitive, and a single changed bone moves it.
        // Read as a float span over the matrices rather than member by member: no per-bone array, and
        // it cannot silently skip a component the way an enumerated list of sixteen names can.
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(Slice(instance));
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

        return Matrices.AsSpan(instance * BoneCount, BoneCount);
    }
}
