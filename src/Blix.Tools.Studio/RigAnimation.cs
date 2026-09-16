using Blix.Diagnostics;
using System.Numerics;

namespace Blix.Tools.Studio;

/// <summary>How a session composes its subject's pose from one or two clips.</summary>
public enum PoseMode
{
    /// <summary>Player A alone.</summary>
    Single,

    /// <summary>A and B interpolated by weight — <see cref="PoseBlend.Lerp"/>.</summary>
    Blend,

    /// <summary>B layered on A as an offset from rest — <see cref="PoseDelta.LayerOnto"/>.</summary>
    Additive,

    /// <summary>
    /// B blended onto A through a <see cref="BoneMask"/> — an upper body doing one thing while the
    /// legs do another.
    /// </summary>
    /// <remarks>
    /// The mode the animation arc's stage D was deferred for, and the one the character arc asked
    /// for with a body playing a one-shot chop at 0.868 m/s and its legs frozen mid-swing. Blend
    /// mixes two whole poses and cannot express that; this one can, and the only difference in the
    /// arithmetic is a per-bone weight.
    /// </remarks>
    Masked,
}

/// <summary>
/// A rig being animated: the clocks, the composed pose, and the palettes N bodies are drawn from.
/// </summary>
/// <remarks>
/// <b>Named for the job rather than the lifetime.</b> This was <c>RigSession</c>, and "session"
/// named only the fact that it persists — true of most objects, and silent about what this one
/// holds. What it actually does is turn clips and a time into poses, palettes and placements.
/// </remarks>
/// <remarks>
/// <b>One rig, one composed pose — and it used to be more.</b> This held N bodies as a `Subject`
/// plus N-1 `Echoes`, laid them out in a row, packed their palettes and counted their distinct
/// poses. "Echo" was viewer vocabulary for "the extra bodies I draw to show instancing works",
/// describing a relationship that was not true (they play different clips) and making body 0
/// structurally privileged — the same fault as the glTF importer's "primary skin". All of that is
/// <see cref="RigInstances"/> now, where N bodies are peers.
/// <para>
/// A row of bodies spaced along X was never an animation concern either; it is stage staging, the
/// same category as the ground plane.
/// </para>
/// <para>
/// <b>It owns no clock of its own.</b> <see cref="Advance"/> takes a delta and <see cref="Scrub"/>
/// takes a time; whether those come from a frame, a slider or a fixed-step loop is the caller's.
/// That is what lets the capture be reproducible while the viewer is live, from one implementation.
/// </para>
/// <para>
/// <b>And no drawing, no UI and no camera.</b> It answers "what pose is each body in" and hands over
/// palettes; where those bodies stand relative to each other is a placement the caller supplies,
/// because a row of three in a lab and a crowd in a game disagree about it.
/// </para>
/// </remarks>
public sealed class RigAnimation : ITunable
{
    private readonly StudioRig rig;
    private readonly Matrix4x4[] boneWorlds;
    private readonly Matrix4x4[] restWorlds;
    private readonly Matrix4x4[] scratchWorlds;
    private ClipPlayer? secondary;

    public RigAnimation(StudioRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        this.rig = rig;

        Subject = new ClipPlayer(rig.Skeleton, rig.Clips.Count > 0 ? rig.Clips[0] : null);
        Posed = rig.Skeleton.CreateRestPose();

        boneWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        restWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        scratchWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        StudioRig.ComputeBoneWorlds(rig.Skeleton, Subject.RestPose, restWorlds);
    }

    /// <summary>The body the panel's transport, bone list and root-motion readout describe.</summary>
    public ClipPlayer Subject { get; }

    /// <summary>The second clip, for blend and additive. Its clock only runs in those modes.</summary>
    /// <summary>
    /// The second clip, for <see cref="PoseMode.Blend"/>, <see cref="PoseMode.Additive"/> and
    /// <see cref="PoseMode.Masked"/>. Built on first use.
    /// </summary>
    /// <remarks>
    /// <b>Lazy because most bodies never blend.</b> A row of eight is eight of these, and seven of
    /// them play one clip and read this never — eight composition machines constructed to run one.
    /// Nothing breaks if it is eager; it is simply paid for and unused, and the allocation is
    /// trivial. What made it worth changing is that it stopped the uniform-instance shape from
    /// carrying an apology: every body can now blend, and a body that does not costs nothing for the
    /// capability.
    /// </remarks>
    public ClipPlayer Secondary => secondary ??= new ClipPlayer(
        rig.Skeleton, rig.Clips.Count > 1 ? rig.Clips[1] : null);

    /// <summary>The subject's composed pose, after blending and any root strip.</summary>
    public Pose Posed { get; }

    /// <summary>Every live instance's palette, sliced at the rig's bone count.</summary>

    /// <summary>Skin 0's palettes. Instance counts are the same across skins.</summary>

    [Tune] public PoseMode Mode { get; set; } = PoseMode.Single;

    /// <summary>Blend weight, or additive overlay strength. Clamped on use.</summary>
    [Tune(0, 1)] public float Weight { get; set; } = 0.5f;

    /// <summary>Which bones <see cref="PoseMode.Masked"/> reaches. Null masks nothing, so B is ignored.</summary>
    public BoneMask? Mask { get; set; }

    /// <summary>The bone the mask's subtree starts at.</summary>
    /// <remarks>
    /// Declared rather than privately set, so <c>--mask-root</c> and the panel's bone combo are the
    /// same member. The combo stays bespoke — a generated text field cannot know which bones THIS
    /// rig has — but the state it writes is this one, and moving it rebuilds the mask through
    /// <see cref="OnChanged"/> whichever door it came through.
    /// </remarks>
    [Tune] public string MaskRoot { get; set; } = string.Empty;

    /// <summary>How many bones the mask fades over, up the chain from its root.</summary>
    [Tune(0, 6)] public int MaskFalloff { get; set; }

    /// <summary>
    /// The bone a masked layer most likely wants to start at, by name, or null if nothing matches.
    /// </summary>
    /// <remarks>
    /// <b>A guess, and labelled as one.</b> There is no standard for rig bone names — "Spine",
    /// "spine_01", "mixamorig:Spine" and "Bip01 Spine1" are all real exports — so this tries the
    /// common spellings in order and the panel lets you correct it in one click. A lab that opened
    /// with no mask at all would be technically honest and practically useless: the first thing
    /// anyone does is pick a spine.
    /// </remarks>
    public static string? GuessUpperBodyRoot(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);

        foreach (var wanted in new[] { "spine", "spine_01", "spine1", "spine.001", "chest", "torso" })
        {
            foreach (var bone in skeleton.Bones)
            {
                var name = bone.Name;
                var tail = name.LastIndexOf(':') >= 0 ? name[(name.LastIndexOf(':') + 1)..] : name;
                if (string.Equals(tail, wanted, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }

        return null;
    }

    /// <summary>Build the mask from a named bone, or clear it when the name is not in the rig.</summary>
    /// <remarks>
    /// Swallowing the miss here rather than throwing, because this is driven by a panel and a
    /// half-typed bone name is a normal thing for a panel to hold. <see cref="BoneMask.Subtree"/>
    /// still throws for everyone else, which is right for a caller that meant it.
    /// </remarks>
    public bool SetMask(string rootBone, int falloff = 0)
    {
        MaskRoot = rootBone;
        MaskFalloff = Math.Max(0, falloff);

        try
        {
            Mask = BoneMask.Subtree(rig.Skeleton, rootBone, 1f, MaskFalloff);
            return true;
        }
        catch (ArgumentException)
        {
            Mask = null;
            return false;
        }
    }

    /// <summary>Put every echo on the subject's clip at the subject's instant — the negative control.</summary>
    /// <remarks>
    /// It must then produce exactly ONE distinct pose. Without a direction that is expected to
    /// collapse, "the bodies look different" is evidence only that something differs, which is what
    /// any number of broken mechanisms also produce.
    /// </remarks>


    /// <summary>Strip the root and let the caller move the body by <see cref="RootTravel"/> instead.</summary>
    /// <remarks>
    /// <b>Not [Tune] here — <see cref="RigInstances.DriveRoot"/> is, and it fans out to every body.</b>
    /// Driving is a decision about the whole row: a row where one body is driven and the rest keep
    /// their root motion in-pose has some bodies moved by the transform and others sliding inside
    /// their slots. It was one session-wide flag before N bodies were split out, and setting only
    /// the driven body's is exactly the regression that split introduced.
    /// </remarks>
    public bool DriveRoot { get; set; }

    /// <summary>Accumulated root travel, in the rig's post-mesh-node space.</summary>
    public Vector3 RootTravel { get; private set; }

    /// <summary>Net turn accumulated — composed, not a sum of magnitudes.</summary>
    public Quaternion RootTurn { get; private set; } = Quaternion.Identity;

    /// <summary>Total turning done to get there, which is a different question from the net turn.</summary>
    public float RootTurnPathDegrees { get; private set; }

    /// <summary>The subject's bone transforms in object space — for drawing, not for skinning.</summary>
    public IReadOnlyList<Matrix4x4> BoneWorlds => boneWorlds;

    /// <summary>The rest pose's, for the ghost overlay.</summary>
    public IReadOnlyList<Matrix4x4> RestWorlds => restWorlds;


    /// <summary>Advance every clock by <paramref name="delta"/> and recompose.</summary>
    /// <remarks>
    /// <b>Each echo gets its own clip and its own rate.</b> Staggering ONE clip across bodies proves
    /// the phases are independent and nothing else — a mechanism that forced every body onto one clip
    /// passes that by construction, because there is only one clip. Different clips at different
    /// rates is the claim worth making, and it is the one a still frame can carry.
    /// </remarks>
    public void Advance(double delta)
    {
        Subject.Advance(delta);
        if (Mode != PoseMode.Single) Secondary.Advance(delta);
        Compose();

        RootTravel += Vector3.TransformNormal(Subject.RootDelta.Translation, rig.MeshNodeTransform);
        RootTurn = Quaternion.Normalize(RootTurn * Subject.RootDelta.Rotation);
        RootTurnPathDegrees += DegreesOf(Subject.RootDelta.Rotation);

    }

    /// <summary>Jump the subject to an absolute clip time. A scrub is not travel — the delta is cleared.</summary>
    public void Scrub(double clipTime)
    {
        Subject.ScrubTo(clipTime);
        Compose();
    }

    /// <summary>Recompose without moving any clock — after a mode, weight or clip change.</summary>
    public void Refresh() => Compose();

    /// <summary>
    /// A declared value moved — from a flag, a panel, or a replayed frame. Recompose.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole of what replaced eight hand-written Refresh() calls.</b> Every one of
    /// them sat after a panel write, and the capture tool that composes the same state had none,
    /// because nothing reminded it. A tool cannot forget to call this.
    /// <para>
    /// The mask is rebuilt only when its own inputs move, which is why the change carries a name:
    /// reaching for a bone list because a weight slider moved would be exactly the per-frame
    /// recompute that reporting per change exists to avoid.
    /// </para>
    /// </remarks>
    public void OnChanged(TunableChange change)
    {
        if (change.Name is nameof(MaskRoot) or nameof(MaskFalloff)) SetMask(MaskRoot, MaskFalloff);
        Compose();
    }

    /// <summary>Forget the accumulated root motion.</summary>
    public void ResetTravel()
    {
        RootTravel = Vector3.Zero;
        RootTurn = Quaternion.Identity;
        RootTurnPathDegrees = 0f;
    }

    // The three composition modes are three ENGINE primitives, not three implementations here.
    private void Compose()
    {
        switch (Mode)
        {
            case PoseMode.Blend:
                PoseBlend.Lerp(Subject.Pose, Secondary.Pose, Math.Clamp(Weight, 0f, 1f), Posed);
                break;

            case PoseMode.Masked:
                // No mask means no layer: A alone, rather than a silent whole-body blend. A mask that
                // failed to build should look like nothing happening, not like the wrong thing.
                if (Mask is null) Posed.CopyFrom(Subject.Pose);
                else PoseBlend.Lerp(Subject.Pose, Secondary.Pose, Math.Clamp(Weight, 0f, 1f), Mask, Posed);
                break;

            case PoseMode.Additive:
                // B layered ON TOP of A: order matters, because the delta is applied to whatever is
                // already in the target.
                for (var i = 0; i < Posed.BoneCount; i++)
                {
                    Posed.Locals[i] = PoseDelta.LayerOnto(
                        Subject.Pose.Locals[i],
                        Subject.RestPose.Locals[i],
                        Secondary.Pose.Locals[i],
                        Math.Clamp(Weight, 0f, 1f));
                }

                break;

            default:
                Posed.CopyFrom(Subject.Pose);
                break;
        }

        // Driving means the clip stops moving the body and the transform starts. Leaving the root
        // animated AND applying the delta moves a travelling clip twice.
        if (DriveRoot) RootMotion.Strip(rig.Skeleton, Posed, Subject.RestPose);

        StudioRig.ComputeBoneWorlds(rig.Skeleton, Posed, boneWorlds);
    }

    /// <summary>A quaternion's turn magnitude in degrees.</summary>
    public static float DegreesOf(Quaternion q) =>
        2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * (180f / MathF.PI);
}
