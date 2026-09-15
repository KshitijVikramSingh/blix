using System.Numerics;

namespace Blix.Tools.Preview;

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
/// <b>Extracted because the capture tool had grown its own copy.</b> Both lab executables compose a
/// pose from one or two clips, strip root motion when driving, pack one palette per instance at the
/// stride the shader reads, and count how many distinct poses came out. The viewer does it live and
/// the capture does it a fixed step at a time — the same decisions on two clocks, which is the
/// second consumer conventions §4 asks for.
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
public sealed class RigSession
{
    private readonly LabRig rig;
    private readonly Matrix4x4[] boneWorlds;
    private readonly Matrix4x4[] restWorlds;
    private readonly Matrix4x4[] scratchWorlds;
    private BonePaletteSet? poseCheck;

    public RigSession(LabRig rig, int instances)
    {
        ArgumentNullException.ThrowIfNull(rig);
        this.rig = rig;

        InstanceCount = Math.Clamp(instances, 1, LabRig.MaxInstances);
        Subject = new ClipPlayer(rig.Skeleton, rig.Clips.Count > 0 ? rig.Clips[0] : null);
        Secondary = new ClipPlayer(rig.Skeleton, rig.Clips.Count > 1 ? rig.Clips[1] : null);
        Posed = rig.Skeleton.CreateRestPose();
        Palettes = new BonePaletteSet(rig.Skeleton.BoneCount, LabRig.MaxInstances);

        Echoes = new ClipPlayer[Math.Max(0, InstanceCount - 1)];
        for (var i = 0; i < Echoes.Length; i++) Echoes[i] = new ClipPlayer(rig.Skeleton);

        boneWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        restWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        scratchWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        LabRig.ComputeBoneWorlds(rig.Skeleton, Subject.RestPose, restWorlds);
    }

    /// <summary>The body the panel's transport, bone list and root-motion readout describe.</summary>
    public ClipPlayer Subject { get; }

    /// <summary>The second clip, for blend and additive. Its clock only runs in those modes.</summary>
    public ClipPlayer Secondary { get; }

    /// <summary>The other bodies. Each runs its OWN clip on its OWN clock — see <see cref="Advance"/>.</summary>
    public ClipPlayer[] Echoes { get; }

    /// <summary>The subject's composed pose, after blending and any root strip.</summary>
    public Pose Posed { get; }

    /// <summary>Every live instance's palette, sliced at the rig's bone count.</summary>
    public BonePaletteSet Palettes { get; }

    public int InstanceCount { get; }

    public PoseMode Mode { get; set; } = PoseMode.Single;

    /// <summary>Blend weight, or additive overlay strength. Clamped on use.</summary>
    public float Weight { get; set; } = 0.5f;

    /// <summary>Which bones <see cref="PoseMode.Masked"/> reaches. Null masks nothing, so B is ignored.</summary>
    public BoneMask? Mask { get; set; }

    /// <summary>The bone the mask's subtree starts at, kept so a panel can show and change it.</summary>
    public string MaskRoot { get; private set; } = string.Empty;

    /// <summary>How many bones the mask fades over, up the chain from its root.</summary>
    public int MaskFalloff { get; private set; }

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
    public bool Lockstep { get; set; }

    /// <summary>Strip the root and let the caller move the body by <see cref="RootTravel"/> instead.</summary>
    public bool DriveRoot { get; set; }

    /// <summary>Accumulated root travel, in the rig's post-mesh-node space.</summary>
    public Vector3 RootTravel { get; private set; }

    /// <summary>Net turn accumulated — composed, not a sum of magnitudes.</summary>
    public Quaternion RootTurn { get; private set; } = Quaternion.Identity;

    /// <summary>Total turning done to get there, which is a different question from the net turn.</summary>
    public float RootTurnPathDegrees { get; private set; }

    /// <summary>How many genuinely different poses the live instances hold. See <see cref="Lockstep"/>.</summary>
    public int DistinctPoses { get; private set; } = 1;

    /// <summary>The subject's bone transforms in object space — for drawing, not for skinning.</summary>
    public IReadOnlyList<Matrix4x4> BoneWorlds => boneWorlds;

    /// <summary>The rest pose's, for the ghost overlay.</summary>
    public IReadOnlyList<Matrix4x4> RestWorlds => restWorlds;

    /// <summary>Where each live instance stands, in the order their palettes were packed.</summary>
    public IReadOnlyList<Matrix4x4> Placements => placements;

    private readonly List<Matrix4x4> placements = new();

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

        for (var i = 0; i < Echoes.Length; i++)
        {
            var echo = Echoes[i];
            if (Lockstep)
            {
                echo.Clip = Subject.Clip;
                echo.ScrubTo(Subject.Time);
                continue;
            }

            echo.Clip = rig.Clips.Count > 0 ? rig.Clips[EchoClipIndex(i)] : null;
            echo.Paused = Subject.Paused;
            echo.Rate = Subject.Rate * (1f + ((i + 1) * 0.17f));
            echo.Advance(delta);
        }
    }

    /// <summary>Jump the subject to an absolute clip time. A scrub is not travel — the delta is cleared.</summary>
    public void Scrub(double clipTime)
    {
        Subject.ScrubTo(clipTime);
        Compose();
    }

    /// <summary>Recompose without moving any clock — after a mode, weight or clip change.</summary>
    public void Refresh() => Compose();

    /// <summary>Forget the accumulated root motion.</summary>
    public void ResetTravel()
    {
        RootTravel = Vector3.Zero;
        RootTurn = Quaternion.Identity;
        RootTurnPathDegrees = 0f;
    }

    /// <summary>
    /// Packs one palette per instance, laid out in a row about <paramref name="origin"/>.
    /// </summary>
    /// <remarks>
    /// Each instance's placement is baked into its palette, because a per-draw push constant cannot
    /// vary per instance. That is Bulwark's shape; RTSGame keeps its palette in model space and
    /// carries the placement alongside. <see cref="BonePaletteSet"/> takes no view — it owns the
    /// stride and nothing else.
    /// </remarks>
    public void PackInstances(Matrix4x4 origin, float spacing)
    {
        Palettes.Reset();
        placements.Clear();

        var half = (InstanceCount - 1) * 0.5f;
        var subjectPlacement = Matrix4x4.CreateTranslation(-half * spacing, 0f, 0f) * origin;
        Palettes.Add(rig.Skeleton, Posed, rig.MeshNodeTransform * subjectPlacement);
        placements.Add(subjectPlacement);

        for (var i = 0; i < Echoes.Length; i++)
        {
            var pose = Echoes[i].Pose;
            if (DriveRoot) RootMotion.Strip(rig.Skeleton, pose, Echoes[i].RestPose);
            var placement = Matrix4x4.CreateTranslation((i + 1 - half) * spacing, 0f, 0f) * origin;
            Palettes.Add(rig.Skeleton, pose, rig.MeshNodeTransform * placement);
            placements.Add(placement);
        }

        DistinctPoses = CountDistinctPoses();
    }

    /// <summary>One echo's bone transforms, into a shared scratch. Valid until the next call.</summary>
    /// <remarks>
    /// A scratch rather than an array per instance: the overlay reads it and is done before the next
    /// echo overwrites it, which is the whole difference between a draw-time scratch and a recorded
    /// payload that has to survive to Execute.
    /// </remarks>
    public IReadOnlyList<Matrix4x4> EchoBoneWorlds(int echoIndex)
    {
        LabRig.ComputeBoneWorlds(rig.Skeleton, Echoes[echoIndex].Pose, scratchWorlds);
        return scratchWorlds;
    }

    /// <summary>The clip index echo <paramref name="i"/> plays — the subject's, stepped along the list.</summary>
    public int EchoClipIndex(int i)
    {
        if (rig.Clips.Count == 0) return 0;
        var start = 0;
        for (var c = 0; c < rig.Clips.Count; c++)
        {
            if (!ReferenceEquals(rig.Clips[c], Subject.Clip)) continue;
            start = c;
            break;
        }

        return (start + i + 1) % rig.Clips.Count;
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

        LabRig.ComputeBoneWorlds(rig.Skeleton, Posed, boneWorlds);
    }

    // Placement is baked into every drawn palette, so bodies standing apart are never bit-identical
    // whatever their poses are. Counting from the drawn slices would make the lockstep control
    // incapable of failing, so the poses are re-packed at identity purely to be counted.
    private int CountDistinctPoses()
    {
        poseCheck ??= new BonePaletteSet(rig.Skeleton.BoneCount, LabRig.MaxInstances);
        poseCheck.Reset();
        poseCheck.Add(rig.Skeleton, Posed, Matrix4x4.Identity);
        foreach (var echo in Echoes) poseCheck.Add(rig.Skeleton, echo.Pose, Matrix4x4.Identity);

        var seen = new HashSet<ulong>();
        for (var i = 0; i < poseCheck.Count; i++) seen.Add(poseCheck.Fingerprint(i));
        return seen.Count;
    }

    /// <summary>A quaternion's turn magnitude in degrees.</summary>
    public static float DegreesOf(Quaternion q) =>
        2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * (180f / MathF.PI);
}
