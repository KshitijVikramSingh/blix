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
    /// <remarks>Unlike whole-pose blending, the mask supplies a per-bone blend weight.</remarks>
    Masked,
}

/// <summary>
/// One rig animation state: clip players, composed pose, bone worlds, and root-motion accumulation.
/// </summary>
/// <remarks>
/// <para>
/// The caller supplies elapsed or absolute time; this type owns no frame clock, drawing, UI, camera,
/// instance layout, or palette packing. Multi-body policy belongs to <see cref="RigInstances"/>.
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

    /// <summary>
    /// The second clip, for <see cref="PoseMode.Blend"/>, <see cref="PoseMode.Additive"/> and
    /// <see cref="PoseMode.Masked"/>. Built on first use.
    /// </summary>
    /// <remarks>Allocated lazily because single-clip bodies never need a second player.</remarks>
    public ClipPlayer Secondary => secondary ??= new ClipPlayer(
        rig.Skeleton, rig.Clips.Count > 1 ? rig.Clips[1] : null);

    /// <summary>The subject's composed pose, after blending and any root strip.</summary>
    public Pose Posed { get; }

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
    /// <remarks>Bone naming is not standardized. This tries common spine/chest spellings; callers
    /// must expose or supply the final choice.</remarks>
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

    /// <summary>Strip the root and let the caller move the body by <see cref="RootTravel"/> instead.</summary>
    /// <remarks>For multi-body use, set <see cref="RigInstances.DriveRoot"/> so every body follows
    /// one coherent policy.</remarks>
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


    /// <summary>Advance the active players by <paramref name="delta"/> and recompose.</summary>
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
    /// <remarks>The named change lets mask inputs rebuild the mask without repeating that work for
    /// unrelated tuning changes.</remarks>
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
