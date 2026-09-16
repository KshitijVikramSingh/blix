using System.Numerics;
using Blix.Diagnostics;

namespace Blix.Tools.Studio;

/// <summary>
/// N animated bodies of one rig, laid out and packed into the palettes a skinned draw reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because "echo" did not deserve to be a Blix type.</b> <see cref="RigAnimation"/>
/// held one <c>Subject</c> and N-1 <c>Echoes</c> — viewer vocabulary for "the extra bodies I draw to
/// show instancing works", describing a relationship that was not even true, since they play
/// different clips. It also made body 0 structurally privileged, which is the same fault as the
/// glTF importer's "primary skin": the naming was the visible end of it, and the giveaway was a
/// helper whose whole job was translating between an instance index and an echo index.
/// </para>
/// <para>
/// So there are N bodies and they are peers. Body 0 is only the one a tool's panel happens to drive,
/// which is a fact about the tool and not about this type.
/// </para>
/// <para>
/// <b>And the layout lives here rather than in <see cref="RigAnimation"/>.</b> A row of bodies spaced
/// along X is stage staging, the same category as the ground plane — it is not something a type
/// about animating a rig should know. What that type kept was one rig, one composed pose, and root
/// motion; everything about *several* of them is here.
/// </para>
/// </remarks>
public sealed class RigInstances : ITunable
{
    private readonly StudioRig rig;
    private readonly RigAnimation[] bodies;
    private readonly BonePaletteSet[] palettesBySkin;
    private readonly List<Matrix4x4> placements = new();
    private readonly List<Pose> posed = new();
    private readonly Matrix4x4[] scratchWorlds;
    private BonePaletteSet? poseCheck;

    public RigInstances(StudioRig rig, int count)
    {
        ArgumentNullException.ThrowIfNull(rig);
        this.rig = rig;

        Count = Math.Clamp(count, 1, StudioRig.MaxInstances);
        bodies = new RigAnimation[Count];
        for (var i = 0; i < Count; i++) bodies[i] = new RigAnimation(rig);

        palettesBySkin = rig.CreatePaletteSets(StudioRig.MaxInstances);
        scratchWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
    }

    /// <summary>How many bodies are drawn.</summary>
    public int Count { get; }

    /// <summary>One body. They are peers; the index is only an order.</summary>
    public RigAnimation this[int index] => bodies[index];

    /// <summary>
    /// The body a tool's panel drives. Body 0 by convention, and that convention is the tool's.
    /// </summary>
    public RigAnimation Driven => bodies[0];

    /// <summary>
    /// How each body past the first comes to differ from it. Null leaves them where they are.
    /// </summary>
    /// <remarks>
    /// <b>Policy, and it belongs to the caller</b> — conventions §6. The viewer drifts bodies apart
    /// by rate so a glance shows they are not frame-locked; a capture stakes them at fixed phases so
    /// the same arguments give the same picture. This once lived inside the animation's own
    /// <c>Advance</c> as <c>Rate * (1 + (i + 1) * 0.17)</c>, where the capture tool's only way to
    /// disagree was to stop using the class — which is exactly what it did, for long enough that
    /// per-skin palette packing had to be written twice.
    /// </remarks>
    public Action<RigAnimation, int, double>? Step { get; set; }

    /// <summary>
    /// Every body on one clip at one instant — the negative control.
    /// </summary>
    /// <remarks>
    /// <b>Shared rather than left to each caller, because a control each tool implemented itself
    /// would not be one.</b> Both must get the same answer from it: N bodies, one distinct pose. It
    /// overrides <see cref="Step"/> entirely.
    /// </remarks>
    [Tune] public bool Lockstep { get; set; }

    /// <summary>
    /// Strip every body's root and let the caller move the row by the driven body's travel.
    /// </summary>
    /// <remarks>
    /// <b>Set-level, because a half-driven row is incoherent.</b> The driven body would be moved by
    /// the transform while the others kept their root motion in the pose and slid inside their
    /// slots. This was one flag on the whole session until N bodies were split out of it; putting
    /// it back here is what makes the row behave as one thing again.
    /// </remarks>
    [Tune] public bool DriveRoot
    {
        get => Driven.DriveRoot;
        set { foreach (var body in bodies) body.DriveRoot = value; }
    }

    /// <summary>How many distinct poses the bodies actually hold. 1 under lockstep.</summary>
    /// <remarks>
    /// Fingerprinted WITHOUT placement. The placement is baked into each drawn palette, so bodies
    /// standing apart differ whatever their poses are — a verdict taken from the drawn slices reads
    /// "all different" even under lockstep, which makes the control useless.
    /// </remarks>
    public int DistinctPoses { get; private set; } = 1;

    /// <summary>Where each body stands, in instance order.</summary>
    public IReadOnlyList<Matrix4x4> Placements => placements;

    /// <summary>One skin's palettes.</summary>
    public BonePaletteSet PalettesFor(int skinIndex) => palettesBySkin[skinIndex];

    /// <summary>How many skins the rig poses.</summary>
    public int SkinCount => palettesBySkin.Length;

    /// <summary>Bone worlds for one body, into a shared scratch. Valid until the next call.</summary>
    /// <remarks>
    /// A scratch rather than an array per body: a reader consumes it before asking for the next, and
    /// collecting these into an array gives N references to one buffer. See <c>RigView</c>, which
    /// takes this as a delegate for exactly that reason.
    /// </remarks>
    public IReadOnlyList<Matrix4x4> BoneWorldsFor(int body)
    {
        StudioRig.ComputeBoneWorlds(rig.Skeleton, bodies[body].Posed, scratchWorlds);
        return scratchWorlds;
    }

    /// <summary>Advance every body. The driven one by the delta; the rest by the caller's policy.</summary>
    public void Advance(double delta)
    {
        Driven.Advance(delta);

        for (var i = 1; i < Count; i++)
        {
            if (Lockstep)
            {
                bodies[i].Subject.Clip = Driven.Subject.Clip;
                bodies[i].Subject.ScrubTo(Driven.Subject.Time);
                bodies[i].Refresh();
                continue;
            }

            Step?.Invoke(bodies[i], i, delta);
        }
    }

    /// <summary>Lay the bodies in a row and pack their palettes.</summary>
    /// <param name="origin">Where the row is centred.</param>
    /// <param name="spacing">Distance between neighbours.</param>
    public void Pack(Matrix4x4 origin, float spacing)
    {
        placements.Clear();
        posed.Clear();

        var half = (Count - 1) * 0.5f;
        for (var i = 0; i < Count; i++)
        {
            var body = bodies[i];
            var slot = Matrix4x4.CreateTranslation((i - half) * spacing, 0f, 0f) * origin;

            if (body.DriveRoot)
            {
                // <b>Its OWN travel, not the row's.</b> Driving strips the root from the pose and
                // moves the body by the distance its clip asked for — and each body plays a
                // different clip, so each asked for a different distance. Moving the whole row by
                // the driven body's travel drags four bodies by a fifth one's Dodge, and a body with
                // real root motion of its own is displaced by someone else's instead of following
                // the curve it was stripped for. That is what the single session-wide RootTravel
                // could not express: there was one accumulator for N clips.
                //
                // Under lockstep every body IS the driven one, at the same instant, so they share
                // its travel — their own accumulators never ran, because a scrub is not an advance.
                RootMotion.Strip(rig.Skeleton, body.Posed, body.Subject.RestPose);
                var travel = Lockstep ? Driven.RootTravel : body.RootTravel;
                slot = Matrix4x4.CreateTranslation(travel) * slot;
            }

            placements.Add(slot);
            posed.Add(body.Posed);
        }

        rig.PackPalettes(posed, placements, palettesBySkin);
        DistinctPoses = CountDistinctPoses();
    }

    /// <summary>The clip index body <paramref name="offset"/> plays — the driven one, stepped along.</summary>
    public int ClipIndexFor(int offset)
    {
        if (rig.Clips.Count == 0) return 0;
        var start = 0;
        for (var c = 0; c < rig.Clips.Count; c++)
        {
            if (ReferenceEquals(rig.Clips[c], Driven.Subject.Clip)) { start = c; break; }
        }

        return (start + offset) % rig.Clips.Count;
    }

    public void OnChanged(TunableChange change) => Driven.OnChanged(change);

    private int CountDistinctPoses()
    {
        poseCheck ??= new BonePaletteSet(rig.Skeleton.BoneCount, StudioRig.MaxInstances);
        poseCheck.Reset();
        foreach (var body in bodies) poseCheck.Add(rig.Skeleton, body.Posed, Matrix4x4.Identity);

        var seen = new HashSet<ulong>();
        for (var i = 0; i < poseCheck.Count; i++) seen.Add(poseCheck.Fingerprint(i));
        return seen.Count;
    }
}
