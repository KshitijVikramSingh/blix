using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;

namespace RTSGame.Rendering;

/// <summary>
/// The settlement's people, rigged: one instanced draw per primitive, every body in its own pose.
/// </summary>
/// <remarks>
/// <b>Why this is instanced rather than a draw per person.</b> The obvious shape for skinning is one draw
/// per character with one bone palette bound — that is what <c>Blix.Demos.VulkanLit</c> and the runner do,
/// and it is correct for one hero. A settlement has dozens of bodies and a stress scene has hundreds, and
/// the RTS already draws them as a single instanced batch, so a draw per person would have traded the whole
/// instancing win for a gait.
/// <para>
/// It does not have to. The palette holds <em>every</em> body's bones end to end and a body finds its slice
/// by instance index — <c>gl_InstanceIndex * bonesPerBody</c> — so nothing per-body is bound or pushed and
/// the draw stays one draw. That is worth stating plainly because it collapses a decision: the cheap
/// "prove it reads first" path and the proper path are the same code here, and indexing a palette by
/// instance costs nothing over indexing it by zero.
/// </para>
/// <para>
/// <b>Geometry and poses only</b>, on the rule <see cref="PropModel"/> keeps: no pipeline, no shader, no
/// push-constant layout, no opinion about lighting. The caller brings both pipelines and both push payloads.
/// It lives in the game rather than in Blix.Render for the reason PropModel's own note gives — TankArena and
/// Bulwark each grew a private version of that before it was extracted — so this moves down at the second
/// consumer and not before.
/// </para>
/// <para>
/// <b>The normalising scale is in the model matrix, not baked into the vertices.</b> PropModel bakes, which
/// is right for static art and wrong here: the palette carries inverse-bind matrices and therefore expects
/// positions in bind space, so pre-scaled vertices would be transformed twice — once by the bake and again
/// by every bone. So the composition is <c>meshNode * normalise * placement</c>, applied in that order.
/// </para>
/// </remarks>
internal sealed class SkinnedBodies : IDisposable
{
    /// <summary>
    /// How many posed bodies one palette holds.
    /// </summary>
    /// <remarks>
    /// A village is thirty and the movement stress scenes are three hundred. The cap is what the SSBO is
    /// sized for, and bodies past it are drawn unposed by the caller's fallback rather than dropped —
    /// silently skipping them would read as people vanishing in a crowd, which is the one failure a crowd
    /// renderer must not have. At 31 bones this is 256 × 31 × 64 B ≈ 508 KB per frame slot.
    /// </remarks>
    public const int MaxBodies = 256;


    /// <summary>Whether the one-line "bodies reached a draw" proof has been printed.</summary>
    private bool announced;

    private readonly VulkanGraphicsDevice device;
    private readonly Part[] parts;
    private readonly MaterialBindings palette;
    private readonly byte[] palettePayload;
    private readonly Matrix4x4[] paletteScratch;
    private readonly Skeleton skeleton;
    private readonly BonePalette bonePalette;
    private readonly Pose pose;
    private readonly Pose restPose;
    private readonly Pose blendPose;

    /// <summary>How long the tail of a clip is faded into its own head.</summary>
    /// <remarks>
    /// Short enough that the motion is not softened, long enough that the join is not a jump. A quarter of
    /// a second is about the shortest a human eye reads as a movement rather than a cut.
    /// </remarks>
    private const float WrapBlendSeconds = 0.22f;
    private readonly Matrix4x4 meshNode;
    private readonly Matrix4x4 normalise;
    private readonly Dictionary<string, AnimationClip> clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly int leftHand;
    private readonly int rightHand;
    private readonly int chest;
    private readonly AnimationClip[] byIndex;
    private readonly AnimationClip?[] bound;
    private int count;
    private bool disposed;

    /// <summary>One primitive of the body: its own mesh, its own tint, its own instance buffers.</summary>
    private sealed record Part(
        Mesh Mesh,
        Vector4 Tint,
        TextureHandle Albedo,
        ShaderTextureBinding[] Bindings,
        InstancedBatch Scene,
        InstanceBuffer SceneBuffer,
        InstancedBatch[] Casters,
        InstanceBuffer[] CasterBuffers,
        List<InstanceData> Instances,
        List<InstanceData>[] CasterInstances);

    /// <summary>
    /// The yaw to add so this asset faces the way the steering layer thinks it does.
    /// </summary>
    /// <remarks>
    /// <b>Measured off the rig, not guessed and not a per-asset constant.</b> The draw's yaw assumes a model
    /// whose forward is +X, which was true of the prop villager it was written for; a glTF humanoid is
    /// usually authored facing along Z, so every rigged character arrives turned ninety degrees and it reads
    /// from the chair as bodies walking sideways. A dial would need setting per asset and would be wrong the
    /// first time somebody forgot.
    /// <para>
    /// So it is derived: <b>on any humanoid, the direction from the ankle to the toe is forward.</b> Every
    /// rig in circulation here names those bones something containing "foot" and "toe", and the bind pose
    /// has the feet flat and pointing ahead. Bind-space offsets do not matter because this is a difference
    /// between two positions in the same space.
    /// </para>
    /// <para>
    /// Zero when the rig has no toe — reported at load rather than assumed correct, because a rig without
    /// one needs a human to say which way it looks.
    /// </para>
    /// </remarks>
    public float FacingOffsetRadians { get; private init; }

    /// <summary>
    /// How far this asset's walk cycle carries a body, in normalised body heights.
    /// </summary>
    /// <remarks>
    /// <b>Measured out of the walk clip rather than picked.</b> The gait's phase advances by metres covered
    /// (that is what stops feet skating), which needs to know how many metres one cycle of THIS clip
    /// represents. That number was a constant I guessed at 1.5 m, and a guess there is exactly what makes
    /// feet scuff: too small and they gabble, too large and they slide.
    /// <para>
    /// It is recoverable from the animation. Root motion is stripped, so the body does not travel — but a
    /// foot still swings, and <b>the distance a foot travels fore-and-aft relative to the hips, over one
    /// cycle, is the stride.</b> Sampled across the clip and taken along the rig's own forward axis. Zero
    /// if there is no walk clip or no foot, in which case the caller's constant stands in.
    /// </para>
    /// </remarks>
    public float StridePerCycle { get; private init; }

    /// <summary>
    /// Where in the strike clip the blow actually lands, as a fraction of its duration.
    /// </summary>
    /// <remarks>
    /// <b>Measured, because guessing it would be worse than not syncing at all.</b> §199. §198 gave harm a
    /// moment — a swing that charges and lands — so the strike clip can finally be aligned to it. But the
    /// impact is not at the clip's start or its end; it is somewhere in the middle, and a sync that puts the
    /// picture four tenths of a second away from the harm reads as a bug, whereas a free-running clip reads
    /// as noise. A deliberate-looking error is worse than an obvious one.
    /// <para>
    /// So it comes off the rig, the way <see cref="FacingOffsetRadians"/> and
    /// <see cref="StridePerCycle"/> already do: the hand's speed through the swing peaks at the moment of
    /// the blow, which is what a swing <em>is</em>. Zero when no strike clip or no hand bone can be found,
    /// and the caller then leaves the clip free-running rather than aligning it to a fiction.
    /// </para>
    /// </remarks>
    public float ImpactFraction { get; private init; }

    /// <summary>How many bone matrices one body owns — the stride the skinned shaders index by.</summary>
    public int BonesPerBody => skeleton.BoneCount;

    /// <summary>The palette's material, handed to the batches as their caller-supplied extra binding.</summary>
    public MaterialHandle Palette => palette.Handle;

    /// <summary>How many bodies were added this frame.</summary>
    public int Count => count;

    /// <summary>Every clip the asset shipped, in the order the file declared them.</summary>
    public IReadOnlyList<AnimationClip> Clips => byIndex;

    /// <summary>Triangles in one body, across every primitive.</summary>
    public int TriangleCount
    {
        get
        {
            var total = 0;
            foreach (var part in parts) total += part.Mesh.IndexCount / 3;
            return total;
        }
    }

    private SkinnedBodies(
        VulkanGraphicsDevice device,
        Part[] parts,
        MaterialBindings palette,
        Skeleton skeleton,
        Matrix4x4 meshNode,
        Matrix4x4 normalise,
        AnimationClip[] animations)
    {
        this.device = device;
        this.parts = parts;
        this.palette = palette;
        this.skeleton = skeleton;
        this.meshNode = meshNode;
        this.normalise = normalise;
        byIndex = animations;
        bonePalette = new BonePalette(skeleton.BoneCount);
        pose = skeleton.CreateRestPose();
        restPose = skeleton.CreateRestPose();
        blendPose = skeleton.CreateRestPose();
        paletteScratch = new Matrix4x4[MaxBodies * skeleton.BoneCount];
        palettePayload = new byte[MaxBodies * skeleton.BoneCount * 64];
        foreach (var clip in animations)
        {
            // "HumanArmature|Man_Walk" — the armature prefix is the exporter's, not ours.
            var bar = clip.Name.LastIndexOf('|');
            clips[bar >= 0 ? clip.Name[(bar + 1)..] : clip.Name] = clip;
        }

        // Where a load rides, found by vocabulary like every other bone lookup here.
        leftHand = IndexOf(skeleton, HandLeftNames);
        rightHand = IndexOf(skeleton, HandRightNames);
        chest = IndexOf(skeleton, ChestNames);

        bound = new AnimationClip?[CharacterClips.Count];
        BindActions();
    }

    /// <summary>
    /// Resolves every action against what the file actually contains, and says so.
    /// </summary>
    /// <remarks>
    /// <b>Printed, including the misses, because a silent fallback is how a content pipeline goes wrong
    /// quietly.</b> Dropping in a character whose walk is called something unanticipated should read as one
    /// line in the terminal saying so, not as a villager who slides for a session until somebody notices.
    /// The name that bound is printed too, so a stand-in is visible as a stand-in.
    /// </remarks>
    private void BindActions()
    {
        var report = new List<string>(CharacterClips.Count);
        var standingIn = 0;
        foreach (var (action, names, standIns) in CharacterClips.Table)
        {
            AnimationClip? found = null;
            var which = "MISSING";
            var isStandIn = false;

            foreach (var name in names)
            {
                if (!clips.TryGetValue(name, out var clip)) continue;
                found = clip;
                which = name;
                break;
            }

            if (found is null)
            {
                foreach (var name in standIns)
                {
                    if (!clips.TryGetValue(name, out var clip)) continue;
                    found = clip;
                    which = name;
                    isStandIn = true;
                    standingIn++;
                    break;
                }
            }

            bound[(int)action] = found;
            report.Add($"{action}={which}{(isStandIn ? "*" : string.Empty)}");
        }

        Console.WriteLine(
            $"  bodies: clips bound — {string.Join(", ", report)}" +
            (standingIn > 0 ? "  (* = no real clip; something else is standing in)" : string.Empty));
        var missing = 0;
        foreach (var clip in bound)
        {
            if (clip is null) missing++;
        }

        if (missing > 0)
        {
            Console.WriteLine(
                $"  bodies: {missing} action(s) have no clip and will fall back to whatever Idle bound — " +
                "add the name to CharacterClips.Table or the clip to the asset");
        }
    }

    /// <summary>The clip serving this action, or Idle's, or the first clip the file had.</summary>
    public AnimationClip? For(BodyAction action) =>
        bound[(int)action] ?? bound[(int)BodyAction.Idle] ?? (byIndex.Length > 0 ? byIndex[0] : null);

    /// <summary>
    /// Holds the body where the simulation put it, whatever the clip thinks.
    /// </summary>
    /// <remarks>
    /// <b>The root bone is a placement handle, not a body part.</b> Where a body is belongs to the
    /// simulation and to nothing else, so a clip's root translation is discarded outright — all three axes.
    /// <para>
    /// <b>It was horizontal-only first, and that was wrong twice over.</b> The argument for keeping the
    /// vertical was that a gait's bob and rise live there and flattening them would make a walk read as a
    /// shuffle. That is true of a well-authored in-place clip whose root sits at the origin; it is false of
    /// a library where each clip's root carries its own constant offset. This character's roots sit five
    /// units below the floor and differ from clip to clip — so a body normalised against its idle and then
    /// drawn walking came out at a different height, reported from the chair as floating in the air. A
    /// gait's vertical motion is in the hips and the spine anyway, which is where it survives.
    /// </para>
    /// <para>
    /// <b>Orientation as well as position, and that was the whole facing saga.</b> Only the translation was
    /// discarded at first, so every clip brought its own authored heading with it — and clips from two
    /// libraries are not authored facing the same way. Reported from the chair with the detail that settled
    /// it: a yaw of two quarter turns looked right while walking and zero looked right while chopping. No
    /// single offset can fix a per-clip disagreement, which is why four attempts at one failed. Where a
    /// body is AND which way it points belong to the simulation; a clip may say neither.
    /// </para>
    /// <para>
    /// The cost is a clip that turns in place, whose turn now goes nowhere. Nothing in this game has one,
    /// and if something does the turn belongs in the steering layer anyway.
    /// </para>
    /// <para>
    /// Static, and used by the measurement as well as the draw, which is the point: a body is measured
    /// through exactly the transform it is drawn through, so the two cannot disagree about where it is.
    /// </para>
    /// </remarks>
    private static void StripRootMotion(Skeleton skeleton, Pose pose, Pose rest)
    {
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].ParentIndex >= 0) continue;
            // The root reverts to its rest transform entirely — position, orientation and scale.
            pose.Locals[i] = rest.Locals[i];
        }
    }

    /// <summary>The clip with this name, ignoring the exporter's armature prefix; null if absent.</summary>
    public AnimationClip? Clip(string name) => clips.GetValueOrDefault(name);

    /// <summary>
    /// Loads a rigged glTF body, or returns null if there is not one to load.
    /// </summary>
    /// <remarks>
    /// Best-effort, exactly as the unrigged villager was: a settlement that will not load because a
    /// character file is missing is worse than a settlement of cylinders. The caller keeps its fallback.
    /// </remarks>
    public static SkinnedBodies? Load(
        VulkanGraphicsDevice device,
        string directory,
        string fileName,
        ShaderProgramHandle sceneShader,
        PipelineHandle scenePipeline,
        ShaderProgramHandle casterShader,
        PipelineHandle casterPipeline,
        int casterPassCount,
        float metresTall,
        int boneCapacity,
        IReadOnlyDictionary<string, Vector4>? tints = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return null;

        GltfModel model;
        try
        {
            model = new GltfImporter().Import(
                new AssetImportContext(AssetId.Parse("models/villager-animated"), path));
            if (model.Skeleton is null || model.Skeleton.BoneCount == 0)
            {
                throw new InvalidOperationException("no skeleton");
            }
            if (model.Animations.Length == 0) throw new InvalidOperationException("no animations");
            if (model.Skeleton.BoneCount > boneCapacity)
            {
                // Refused with a reason rather than silently writing past the palette: a skeleton bigger
                // than the buffer would corrupt every body drawn after the first.
                throw new InvalidOperationException(
                    $"{model.Skeleton.BoneCount} bones exceeds the palette's capacity of {boneCapacity}");
            }
        }
        catch (Exception ex)
        {
            // <b>The innermost message, not the outermost.</b> An importer that loads images in parallel
            // throws AggregateException, whose own Message is "One or more errors occurred." and tells you
            // nothing — which cost a boot to work out. Unwrap to whatever actually failed.
            Console.WriteLine(
                $"  bodies: {fileName} unusable, keeping the unrigged villager: {Innermost(ex)}");
            return null;
        }

        // <b>Measured from the body as it will be POSED, not as it is stored.</b> §168.
        //
        // The obvious thing is to read each primitive's bounds and normalise against those, and it works
        // right up until an asset arrives whose bind pose is not its rest pose. glTF permits that freely —
        // the inverse-bind matrices are what bridge the two — and a Blender round trip produces it as a
        // matter of course: the same character that was stored standing at the origin came back with its
        // bind geometry three to five units below the floor, and the file is not wrong. Normalising against
        // that put a correct, correctly-scaled villager several hundred metres from the village. Invisible,
        // twice, for a reason no matrix in the draw could show.
        //
        // So this poses the body and measures that, which is the only thing that answers the question being
        // asked. It costs one pass over the vertices at load, once.
        var restForMeasure = model.Skeleton.CreateRestPose();
        var measureClip = PickMeasuringClip(model);
        measureClip?.Sample(0.0, restForMeasure);
        // The same strip the draw applies, or the box describes a body that is never drawn.
        StripRootMotion(model.Skeleton, restForMeasure, model.Skeleton.CreateRestPose());
        var measurePalette = new BonePalette(model.Skeleton.BoneCount);
        model.Skeleton.ComputeBonePalette(restForMeasure, measurePalette);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var measured = 0;
        foreach (var primitive in model.Primitives)
        {
            measured += PosedBounds(
                primitive.Mesh, measurePalette, model.MeshNodeTransform, ref min, ref max);
        }

        if (measured == 0)
        {
            Console.WriteLine($"  bodies: {fileName} has no readable vertices, keeping the prop villager");
            return null;
        }

        var tall = MathF.Max(0.0001f, max.Y - min.Y);
        // Feet to the origin and the body over it, then scaled to unit height for the caller's placement to
        // size. <b>Horizontally centred as well as vertically floored</b>, because a bind pose can be
        // offset in any axis and correcting only the one that happened to be wrong is how this hid.
        var centre = (min + max) * 0.5f;
        var normalise = Matrix4x4.CreateTranslation(-centre.X, -min.Y, -centre.Z) *
                        Matrix4x4.CreateScale(metresTall / tall);

        var boneLayout = new UniformBlockLayout(
            TotalSize: MaxBodies * model.Skeleton.BoneCount * 64,
            Members: new[]
            {
                new UniformBlockMember(
                    "bones", 0, MaxBodies * model.Skeleton.BoneCount * 64, ElementStride: 64),
            });
        // Set 2: the world's own textures are set 0 and the instance buffer is set 3, so this is the slot
        // the engine leaves to a caller. Replicated across frames in flight, like every per-frame material.
        var palette = device.CreateMaterial(
            sceneShader,
            setIndex: 2,
            framesInFlight: device.MaxFramesInFlightCount,
            name: "rts.bodies.bonepalette");

        var parts = new List<Part>(model.Primitives.Length);
        foreach (var primitive in model.Primitives)
        {
            var name = primitive.Material?.Name ?? primitive.Mesh.Name;
            var mesh = device.CreateMesh(primitive.Mesh, name);
            // <b>The asset's own colour, not a guess from its material name.</b> The name guess below was
            // written against a pack whose materials were called Skin/Shirt/Pants/Hair, and it silently
            // turns any other vocabulary into one flat wool brown — reported from the chair as "no
            // colours", which is exactly what eleven materials all resolving to the same default looks
            // like. glTF carries a base colour per material; that is the answer and the guess is the
            // fallback for a file that has none.
            // <b>A textured material's tint is white, because the tint multiplies the picture.</b> Tinting
            // it brown as well would paint mud over the artwork; the name guess and the authored factor are
            // for materials that have no image of their own.
            var authored = primitive.Material?.BaseColorFactor;
            var textured = primitive.Material?.BaseColorTexture is not null;
            var tint = tints is not null && tints.TryGetValue(name ?? string.Empty, out var found)
                ? found
                : textured
                    ? new Vector4(1f, 1f, 1f, MaterialClassBody)
                    : authored is { } colour && colour.W > 0f
                        ? new Vector4(colour.X, colour.Y, colour.Z, MaterialClassBody)
                        : DefaultTint(name);
            var sceneBuffer = new InstanceBuffer(device, sceneShader, $"bodies.{name}");
            var casterBuffers = new InstanceBuffer[casterPassCount];
            var casters = new InstancedBatch[casterPassCount];
            for (var c = 0; c < casterPassCount; c++)
            {
                casterBuffers[c] = new InstanceBuffer(
                    device, casterShader, $"bodies.{name}.caster{c}");
                casters[c] = new InstancedBatch(mesh, casterPipeline, casterBuffers[c]);
            }

            var casterLists = new List<InstanceData>[casterPassCount];
            for (var c = 0; c < casterPassCount; c++) casterLists[c] = new List<InstanceData>(MaxBodies);
            var albedo = UploadAlbedo(device, primitive.Material?.BaseColorTexture, name);
            parts.Add(new Part(
                mesh, tint, albedo,
                // Sized for the five shared maps plus this part's albedo. Pre-allocated so the draw does
                // not allocate per part per frame; BindingsFor falls back if the caller's count changes.
                new ShaderTextureBinding[6],
                new InstancedBatch(mesh, scenePipeline, sceneBuffer), sceneBuffer,
                casters, casterBuffers,
                new List<InstanceData>(MaxBodies), casterLists));
        }

        Console.WriteLine(
            $"  bodies: {fileName} — {model.Skeleton.BoneCount} bones, {model.Primitives.Length} " +
            $"primitive(s), {model.Animations.Length} clip(s), {tall:F2} authored units scaled to " +
            $"{metresTall:F2} m");
        // <b>The measured box, printed, because a body in the air is this box being wrong.</b> Floating
        // means the floor correction lifted too far, which means the lowest thing measured was below the
        // feet — and there is no way to tell that from a height alone.
        Console.WriteLine(
            $"  bodies: posed box X[{min.X:F2},{max.X:F2}] Y[{min.Y:F2},{max.Y:F2}] " +
            $"Z[{min.Z:F2},{max.Z:F2}] from {measured} vertices in " +
            $"'{measureClip?.Name ?? "the rest pose"}'");

        var (facing, how) = MeasureFacing(model.Skeleton);
        Console.WriteLine(
            $"  bodies: forward measured {facing * 180f / MathF.PI:F0}° off +X {how}");

        var stride = MeasureStride(model, facing, model.MeshNodeTransform) * (metresTall / tall);
        Console.WriteLine(
            stride > 0f
                ? $"  bodies: walk stride measured {stride:F2} m per cycle at {metresTall:F2} m tall"
                : "  bodies: no walk stride measurable; the caller's constant stands in");

        var impact = MeasureImpact(model);
        Console.WriteLine(
            impact > 0f
                ? $"  bodies: strike impact measured {impact * 100f:F0}% through the swing clip"
                : "  bodies: no strike impact measurable; the strike clip runs free");

        return new SkinnedBodies(
            device, parts.ToArray(), palette, model.Skeleton,
            model.MeshNodeTransform, normalise, model.Animations)
        {
            FacingOffsetRadians = facing,
            StridePerCycle = stride,
            ImpactFraction = impact,
        };
    }

    /// <summary>
    /// The clip to measure the body in: its idle if it has one, else whatever it has.
    /// </summary>
    /// <remarks>
    /// A body is measured standing, because that is the pose its height means something in. Falling back
    /// to the first clip is fine and falling back to none is fine too — an asset whose bind pose IS its
    /// rest pose measures identically either way, which is why this went unnoticed for an asset that had
    /// no animation problem at all.
    /// </remarks>
    private static AnimationClip? PickMeasuringClip(GltfModel model)
    {
        foreach (var (action, names, standIns) in CharacterClips.Table)
        {
            if (action != BodyAction.Idle) continue;
            foreach (var wanted in names.Concat(standIns))
            {
                foreach (var clip in model.Animations)
                {
                    var bar = clip.Name.LastIndexOf('|');
                    var bare = bar >= 0 ? clip.Name[(bar + 1)..] : clip.Name;
                    if (bare.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return clip;
                }
            }
        }

        return model.Animations.Length > 0 ? model.Animations[0] : null;
    }

    /// <summary>
    /// Every vertex of a primitive, skinned by this palette and lifted through the mesh node.
    /// </summary>
    /// <remarks>
    /// Reads the packed skinned vertex directly: position at 0, joint indices at float 8, weights at
    /// float 12, twenty floats to a vertex. Tied to
    /// <c>VertexPosition3NormalTextureSkin4Tangent</c>, which is the only layout the glTF importer emits
    /// for a skinned mesh — a layout change would land here as a wrong measurement rather than a crash, so
    /// the stride is asserted rather than assumed.
    /// </remarks>
    private static int PosedBounds(
        MeshData mesh, BonePalette palette, Matrix4x4 meshNode, ref Vector3 min, ref Vector3 max)
    {
        const int Floats = 20;
        var stride = mesh.Layout.Stride;
        if (stride != Floats * sizeof(float)) return 0;

        var floats = MemoryMarshal.Cast<byte, float>(mesh.VertexBytes.AsSpan());
        var count = floats.Length / Floats;
        for (var i = 0; i < count; i++)
        {
            var at = i * Floats;
            var position = new Vector3(floats[at], floats[at + 1], floats[at + 2]);
            var skinned = Vector3.Zero;
            var total = 0f;
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = floats[at + 12 + influence];
                if (weight <= 0f) continue;
                var bone = (int)floats[at + 8 + influence];
                if (bone < 0 || bone >= palette.BoneCount) continue;
                skinned += Vector3.Transform(position, palette.Matrices[bone]) * weight;
                total += weight;
            }

            // An unweighted vertex is rigid, not at the origin.
            if (total <= 0f) skinned = position;
            var at3 = Vector3.Transform(skinned, meshNode);
            min = Vector3.Min(min, at3);
            max = Vector3.Max(max, at3);
        }

        return count;
    }

    /// <summary>How far a foot swings fore-and-aft relative to the hips over one walk cycle.</summary>
    /// <summary>
    /// Where the blow lands in the strike clip: the moment the hand is moving fastest.
    /// </summary>
    /// <remarks>
    /// A swing's impact is its peak hand speed — that is what makes it a swing rather than a gesture — so
    /// this samples the clip, differences the hand's position, and returns the fraction at which the
    /// difference is largest. Sampled in raw bone space on purpose: the answer is a <em>fraction of the
    /// clip</em>, and a scale that multiplies every sample equally cannot move which sample is the largest.
    /// That is the one measurement in this file for which the mesh-node transform genuinely does not
    /// matter, and it is worth saying so given three separate bugs here came from measuring through the
    /// wrong transform.
    /// </remarks>
    private static float MeasureImpact(GltfModel model)
    {
        var skeleton = model.Skeleton;
        var hand = IndexOf(skeleton, HandRightNames);
        if (hand < 0) hand = IndexOf(skeleton, HandLeftNames);
        if (hand < 0) return 0f;

        AnimationClip? strike = null;
        foreach (var (action, names, _) in CharacterClips.Table)
        {
            if (action != BodyAction.Strike) continue;
            foreach (var wanted in names)
            {
                foreach (var clip in model.Animations)
                {
                    var bar = clip.Name.LastIndexOf('|');
                    var bare = bar >= 0 ? clip.Name[(bar + 1)..] : clip.Name;
                    if (bare.Equals(wanted, StringComparison.OrdinalIgnoreCase)) strike = clip;
                    if (strike is not null) break;
                }

                if (strike is not null) break;
            }

            if (strike is not null) break;
        }

        if (strike is null || strike.Duration <= 0.0) return 0f;
        if (!Matrix4x4.Invert(skeleton.Bones[hand].InverseBindPose, out var handBind)) return 0f;

        var rest = skeleton.CreateRestPose();
        var pose = skeleton.CreateRestPose();
        var palette = new BonePalette(skeleton.BoneCount);

        const int Samples = 48;
        var at = new Vector3[Samples];
        for (var i = 0; i < Samples; i++)
        {
            pose.CopyFrom(rest);
            strike.Sample(strike.Duration * i / (Samples - 1.0), pose);
            StripRootMotion(skeleton, pose, rest);
            skeleton.ComputeBonePalette(pose, palette);
            at[i] = Vector3.Transform(handBind.Translation, palette.Matrices[hand]);
        }

        var fastest = 0f;
        var where = 0;
        for (var i = 1; i < Samples; i++)
        {
            var moved = Vector3.Distance(at[i], at[i - 1]);
            if (moved <= fastest) continue;
            fastest = moved;
            where = i;
        }

        return fastest <= 0f ? 0f : where / (Samples - 1f);
    }

    private static float MeasureStride(GltfModel model, float facingOffset, Matrix4x4 meshNode)
    {
        var skeleton = model.Skeleton;
        var foot = IndexOf(skeleton, FootNames);
        var hips = IndexOf(skeleton, HipNames);
        if (foot < 0 || hips < 0) return 0f;

        AnimationClip? walk = null;
        foreach (var (action, names, standIns) in CharacterClips.Table)
        {
            if (action != BodyAction.Walk) continue;
            foreach (var wanted in names)
            {
                foreach (var clip in model.Animations)
                {
                    var bar = clip.Name.LastIndexOf('|');
                    var bare = bar >= 0 ? clip.Name[(bar + 1)..] : clip.Name;
                    if (bare.Equals(wanted, StringComparison.OrdinalIgnoreCase)) walk = clip;
                    if (walk is not null) break;
                }

                if (walk is not null) break;
            }
        }

        if (walk is null || walk.Duration <= 0.0) return 0f;

        // The rig's forward, in its own space: undo the offset that turns it to +X.
        var ahead = new Vector2(MathF.Cos(-facingOffset), MathF.Sin(-facingOffset));
        var pose = skeleton.CreateRestPose();
        var palette = new BonePalette(skeleton.BoneCount);
        var least = float.MaxValue;
        var most = float.MinValue;

        var restForStride = skeleton.CreateRestPose();
        const int Samples = 24;
        for (var i = 0; i < Samples; i++)
        {
            pose.CopyFrom(restForStride);
            walk.Sample(walk.Duration * i / (Samples - 1.0), pose);
            StripRootMotion(skeleton, pose, restForStride);
            skeleton.ComputeBonePalette(pose, palette);
            if (!Matrix4x4.Invert(skeleton.Bones[foot].InverseBindPose, out var footBind)) return 0f;
            if (!Matrix4x4.Invert(skeleton.Bones[hips].InverseBindPose, out var hipsBind)) return 0f;

            // <b>Through meshNode, like everything else that is measured here.</b> The height this is
            // scaled against was measured in mesh-node space, and this asset's mesh node carries a
            // hundredfold scale — so a stride taken in raw bone space is a hundred times too small and
            // arrives as 0.00 m, which is how a silent measurement failure looks. Third time this session
            // that measuring through a different transform from the drawing was the bug.
            var footAt = Vector3.Transform(
                Vector3.Transform(footBind.Translation, palette.Matrices[foot]), meshNode);
            var hipsAt = Vector3.Transform(
                Vector3.Transform(hipsBind.Translation, palette.Matrices[hips]), meshNode);
            var along = (footAt.X - hipsAt.X) * ahead.X + (footAt.Z - hipsAt.Z) * ahead.Y;
            least = MathF.Min(least, along);
            most = MathF.Max(most, along);
        }

        var swing = most - least;
        // <b>One foot's swing is half a stride</b>, because the other foot covers the rest of it.
        return swing > 0f ? swing * 2f : 0f;
    }

    /// <summary>
    /// The first bone whose name contains any of these, in the order given.
    /// </summary>
    /// <remarks>
    /// <b>A vocabulary, not a literal, for the same reason <see cref="CharacterClips"/> is.</b> Every rig
    /// family names the same joint differently — hips are <c>Hips</c>, <c>DEF-hips</c> or <c>pelvis</c>;
    /// the toe is <c>toe</c> or, in the Unreal convention, <c>ball</c> — and a single spelling means every
    /// new character family silently loses a measurement. Both of these did exactly that on the first
    /// Unreal-skeleton asset: facing fell back to "assume +X" and the stride to a guessed constant, and the
    /// only reason it was caught is that both say so out loud when they fail.
    /// </remarks>
    private static int IndexOf(Skeleton skeleton, params string[] anyOf)
    {
        foreach (var wanted in anyOf)
        {
            for (var i = 0; i < skeleton.BoneCount; i++)
            {
                var name = skeleton.Bones[i].Name ?? string.Empty;
                if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
        }

        return -1;
    }

    private static bool ContainsAny(string name, string[] anyOf)
    {
        foreach (var wanted in anyOf)
        {
            if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    /// <summary>Names the toe-end joint goes by. Unreal calls it the ball of the foot.</summary>
    private static readonly string[] ToeNames = { "toe", "ball" };

    /// <summary>Names the ankle goes by.</summary>
    private static readonly string[] FootNames = { "foot", "ankle" };

    /// <summary>Names the root of the legs goes by. Unreal calls it the pelvis.</summary>
    private static readonly string[] HipNames = { "hips", "pelvis" };

    /// <summary>Names a left hand goes by.</summary>
    private static readonly string[] HandLeftNames = { "hand_l", "Wrist.L", "Palm.L", "DEF-hand.L" };

    /// <summary>Names a right hand goes by.</summary>
    private static readonly string[] HandRightNames = { "hand_r", "Wrist.R", "Palm.R", "DEF-hand.R" };

    /// <summary>Names the upper chest goes by, for a load with only one hand to hang it from.</summary>
    private static readonly string[] ChestNames = { "spine_03", "Chest", "Torso", "DEF-spine.003" };

    /// <summary>
    /// Which way this rig faces, from the ankle-to-toe direction of <em>both</em> feet.
    /// </summary>
    /// <remarks>
    /// <b>Both feet, because one foot is splayed.</b> A left toe points slightly out to the left and a
    /// right toe slightly out to the right — that is how feet are — so measuring either one alone returns
    /// forward plus that foot's splay angle. Reported from the chair as facing being "slightly broken"
    /// after the ninety-degree error was fixed: the big error was gone and the splay was left. Summing the
    /// two vectors cancels it, because the splay is equal and opposite by construction.
    /// </remarks>
    private static (float Radians, string How) MeasureFacing(Skeleton skeleton)
    {
        var ahead = Vector2.Zero;
        var used = 0;
        var how = string.Empty;

        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var name = skeleton.Bones[i].Name ?? string.Empty;
            if (!ContainsAny(name, ToeNames)) continue;

            var parent = skeleton.Bones[i].ParentIndex;
            if (parent < 0) continue;
            var parentName = skeleton.Bones[parent].Name ?? string.Empty;
            if (!ContainsAny(parentName, FootNames)) continue;

            if (!Matrix4x4.Invert(skeleton.Bones[i].InverseBindPose, out var toe)) continue;
            if (!Matrix4x4.Invert(skeleton.Bones[parent].InverseBindPose, out var foot)) continue;

            var step = new Vector2(
                toe.Translation.X - foot.Translation.X,
                toe.Translation.Z - foot.Translation.Z);
            if (step.LengthSquared() < 1e-8f) continue;

            // Normalised before summing, so a longer foot does not outvote the other one.
            ahead += Vector2.Normalize(step);
            used++;
            if (used == 1) how = $"from {parentName} → {name}";
        }

        if (used == 0 || ahead.LengthSquared() < 1e-8f)
        {
            return (0f, "— no toe bones; assuming the model already faces +X");
        }

        // The draw computes yaw as -atan2(facing.z, facing.x) for a model whose forward is +X. This rig's
        // own forward, expressed the same way, is what has to be cancelled out.
        // <b>The derivation was right; a different bug made it look wrong.</b> The draw computes yaw as
        // -atan2(z, x) for a model whose forward is +X, so this rig's own forward expressed the same way is
        // what has to be cancelled out. That is all this is.
        //
        // It was reported wrong from the chair twice and I flipped the sign in response, which made it
        // wrong in the other direction — because the actual fault was elsewhere: the draw was turning
        // bodies to face `Facing`, the steering layer's heading, which in a crowd lags or opposes the
        // direction of travel. Once the heading came from velocity, this reading was correct as first
        // written, and the chair's yaw dial settled at zero. A symptom chased in the wrong file costs two
        // changes: the wrong one, and undoing it.
        var mine = -MathF.Atan2(ahead.Y, ahead.X);
        return (-mine, $"{how} and {used - 1} more (splay cancelled)");
    }

    /// <summary>
    /// This material's base-colour image on the GPU, or a plain white one if it has none.
    /// </summary>
    /// <remarks>
    /// <b>White rather than nothing, because the binding is not optional.</b> The skinned stage always
    /// samples <c>uBodyAlbedo</c>, so a material with no image still needs something there — and white is
    /// the identity for a multiply, which leaves such a material shaded by its tint exactly as it was
    /// before textures existed.
    /// </remarks>
    private static TextureHandle UploadAlbedo(
        VulkanGraphicsDevice device, GltfTexture? texture, string? name)
    {
        if (texture?.MipBytes is { Count: > 0 } mips)
        {
            return device.CreateTexture2D(
                new TextureDescription(
                    texture.Width, texture.Height, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
                mips[0],
                $"bodies.{name}.albedo");
        }

        var white = new byte[4 * 4 * 4];
        Array.Fill(white, (byte)255);
        return device.CreateTexture2D(
            new TextureDescription(4, 4, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            white,
            $"bodies.{name}.albedo.white");
    }

    /// <summary>Every underlying failure of an exception, flattened to one readable line.</summary>
    private static string Innermost(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            var flat = aggregate.Flatten();
            return string.Join(" | ", flat.InnerExceptions.Select(Innermost));
        }

        // <b>Where, as well as what.</b> The first frame of the trace is worth more than the message when
        // the message turns out to be a run of NUL bytes, which is how this one arrived.
        var where = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "no stack";
        var message = new string((ex.Message ?? string.Empty)
            .Where(c => !char.IsControl(c) && c != '\0').ToArray());
        if (message.Length == 0) message = "(the message was empty or unprintable)";

        return ex.InnerException is { } inner
            ? $"{ex.GetType().Name}: {message} [{where}] <- {Innermost(inner)}"
            : $"{ex.GetType().Name}: {message} [{where}]";
    }

    private static IEnumerable<Vector3> Corners(Bounds3 bounds)
    {
        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
        }
    }

    /// <summary>
    /// A colour per primitive, read off the glTF material's own name.
    /// </summary>
    /// <remarks>
    /// The pack names its primitives <c>Skin</c>, <c>Shirt</c>, <c>Pants</c>, <c>Hair</c>, <c>Eyes</c>,
    /// which is exactly the vocabulary wanted — the same trick the building kit already plays with
    /// <c>Walls</c>/<c>Wood</c>/<c>Stone</c>. Kept in the pack's own muted range rather than at the albedo
    /// the file ships: an 0.8 albedo under a sun of two and a bit tonemaps to very nearly white, which is
    /// the mistake the unrigged villager's comment records having already made once.
    /// </remarks>
    private static Vector4 DefaultTint(string? material)
    {
        var name = material ?? string.Empty;
        if (name.StartsWith("Skin", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(0.52f, 0.38f, 0.30f, MaterialClassBody);
        }
        if (name.StartsWith("Hair", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(0.17f, 0.12f, 0.09f, MaterialClassBody);
        }
        if (name.StartsWith("Eyes", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(0.09f, 0.08f, 0.08f, MaterialClassBody);
        }
        if (name.StartsWith("Pants", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Shoes", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Socks", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(0.22f, 0.19f, 0.16f, MaterialClassBody);
        }
        // Shirts, ties, everything else: wool and linen, the range the unrigged body settled on.
        return new Vector4(0.33f, 0.25f, 0.19f, MaterialClassBody);
    }

    /// <summary>
    /// <c>MaterialClass.Body</c>, restated because that classifier is private to the art assembly.
    /// </summary>
    private const float MaterialClassBody = 4f;

    /// <summary>
    /// Starts a frame.
    /// </summary>
    /// <remarks>
    /// Instances accumulate in plain lists and the batches are not touched until the draw, which is
    /// <see cref="PropModel"/>'s shape and is load-bearing rather than stylistic: each cascade needs its own
    /// push payload, and a batch takes its push at Begin — so beginning three caster batches here would
    /// mean knowing all three light matrices before a single body had been added.
    /// </remarks>
    public void Begin()
    {
        count = 0;
        foreach (var part in parts)
        {
            part.Instances.Clear();
            foreach (var list in part.CasterInstances) list.Clear();
        }
    }

    /// <summary>
    /// Adds one body, in one pose, to the scene draw and to every cascade at once.
    /// </summary>
    /// <remarks>
    /// One call fills every list, which is <see cref="PropModel"/>'s property and worth keeping for the same
    /// reason: a caster that can be filled separately from what it casts for is a shadow that ends up under
    /// nobody. Returns false when the palette is full so the caller can fall back rather than drop a body.
    /// </remarks>
    public bool Add(
        Matrix4x4 placement,
        Vector4? tint,
        AnimationClip clip,
        double atSeconds,
        int casterMask,
        out Vector3 carrySocket)
    {
        ArgumentNullException.ThrowIfNull(clip);
        carrySocket = placement.Translation;
        if (count >= MaxBodies) return false;

        // <b>Reset, sample, palette — and the reset is not optional.</b> A clip writes only the bones it has
        // tracks for, and one Pose is shared by every body here, so without this each body inherits whatever
        // the last one left in the bones its own clip does not touch. The symptom is precise and was reported
        // from the chair: an idle villager walking on the spot with its legs flailing, because the idle clip
        // has no leg tracks and the body sampled before it was mid-stride.
        pose.CopyFrom(restPose);
        clip.Sample(atSeconds, pose);

        // <b>Cross-fade the wrap, because most clips are not loops. §176.</b>
        //
        // A continuous action needs a pose every frame for minutes, and the library's clips are mostly
        // one-shots — `Fixing_Kneeling` kneels, works and stands. Repeat that and the last frame snaps back
        // to the first every cycle, which is what "glitching mid construction" is and what no amount of
        // stabilising WHICH action is chosen can fix. Reported four times; the first three fixes were all
        // about the choice and none of them touched the playback.
        //
        // So the tail of a clip is blended into its own head. The blend is short enough not to soften the
        // motion and long enough to hide the join, and it costs one extra sample on the frames where it
        // applies. A clip whose ends already agree — anything named _Loop — is unaffected, because blending
        // a pose with itself is that pose.
        var duration = (float)clip.Duration;
        if (duration > WrapBlendSeconds * 2f)
        {
            var intoTail = duration - (float)atSeconds;
            if (intoTail < WrapBlendSeconds)
            {
                blendPose.CopyFrom(restPose);
                clip.Sample(WrapBlendSeconds - intoTail, blendPose);
                // Fully the tail at the start of the fade, fully the head by the wrap itself.
                PoseBlend.Lerp(pose, blendPose, 1f - intoTail / WrapBlendSeconds, pose);
            }
        }

        StripRootMotion(skeleton, pose, restPose);
        skeleton.ComputeBonePalette(pose, bonePalette);
        bonePalette.Matrices.AsSpan().CopyTo(
            paletteScratch.AsSpan(count * skeleton.BoneCount, skeleton.BoneCount));

        var model = meshNode * normalise * placement;

        // <b>Where a load rides: between the hands, in the pose the body is actually in.</b> A carried sack
        // was drawn a fixed height above the body's origin, which put it on the villager's head and left it
        // there while the arms moved underneath. The palette knows where the hands are this frame, so the
        // midpoint of the two is the right place and it moves with them.
        if (leftHand >= 0 && rightHand >= 0)
        {
            carrySocket = Vector3.Transform((BoneAt(leftHand) + BoneAt(rightHand)) * 0.5f, model);
        }
        else if (chest >= 0)
        {
            carrySocket = Vector3.Transform(BoneAt(chest), model);
        }
        foreach (var part in parts)
        {
            var colour = tint ?? part.Tint;
            // The material class rides in alpha and must survive a caller's recolour, or a tinted body
            // stops being shaded as a body — which is how a selected villager turns to plaster.
            colour.W = part.Tint.W;
            part.Instances.Add(new InstanceData(model, colour));
            // Cast only into the cascades whose box contains this body — the mask is the caller's, computed
            // the same way it is for every prop, so a villager and a barn agree about which maps they are in.
            for (var c = 0; c < part.CasterInstances.Length; c++)
            {
                if ((casterMask & (1 << c)) != 0) part.CasterInstances[c].Add(new InstanceData(model, colour));
            }
        }

        count++;
        return true;
    }

    /// <summary>
    /// Uploads the frame's palette and hands every batch its instances. Call once after the last
    /// <see cref="Add"/> and before any draw.
    /// </summary>
    /// <remarks>
    /// <b>Staging is separate from drawing because a pass callback does not run when it is registered.</b>
    /// The graph records passes as closures and executes them later, so a batch fed inside its own callback
    /// reads instance lists that the next frame's <see cref="Begin"/> has already cleared — which is exactly
    /// how thirteen villagers became an empty draw with every matrix in the frame correct. Every other batch
    /// in the game already obeys this: accumulate into lists, hand them over in the build phase where
    /// <c>SetInstances</c> copies into the batch's own staging, then let the callback record and nothing more.
    /// <para>
    /// <paramref name="casterPushes"/> is per cascade and must already hold this frame's light matrices.
    /// </para>
    /// </remarks>
    private Vector3 BoneAt(int bone)
    {
        if (!Matrix4x4.Invert(skeleton.Bones[bone].InverseBindPose, out var bind)) return Vector3.Zero;
        return Vector3.Transform(bind.Translation, bonePalette.Matrices[bone]);
    }

    public void Stage(ReadOnlySpan<byte> scenePush, IReadOnlyList<byte[]> casterPushes)
    {
        staged = count;
        if (count == 0) return;

        var used = count * skeleton.BoneCount;
        MemoryMarshal.AsBytes(paletteScratch.AsSpan(0, used)).CopyTo(palettePayload);
        palette.WriteBuffer(device.CurrentFrameSlot, binding: 0, palettePayload.AsSpan(0, used * 64));

        foreach (var part in parts)
        {
            part.Scene.Begin(scenePush);
            part.Scene.SetInstances(CollectionsMarshal.AsSpan(part.Instances));
            for (var c = 0; c < part.Casters.Length && c < casterPushes.Count; c++)
            {
                part.Casters[c].Begin(casterPushes[c]);
                part.Casters[c].SetInstances(CollectionsMarshal.AsSpan(part.CasterInstances[c]));
            }
        }
    }

    /// <summary>How many bodies were handed to the batches this frame, read by the draws.</summary>
    private int staged;

    /// <summary>
    /// The caller's shared maps, plus this part's own albedo appended.
    /// </summary>
    /// <remarks>
    /// Rebuilt into a buffer the part owns rather than allocated per frame: the shared list is the graph's
    /// depth textures and cannot be cached, but the array holding them can be.
    /// </remarks>
    private static ShaderTextureBinding[] BindingsFor(
        Part part, IReadOnlyList<ShaderTextureBinding>? shared)
    {
        var count = shared?.Count ?? 0;
        var into = part.Bindings.Length == count + 1
            ? part.Bindings
            : new ShaderTextureBinding[count + 1];

        for (var i = 0; i < count; i++) into[i] = shared![i];
        into[count] = new ShaderTextureBinding("uBodyAlbedo", part.Albedo, Slot: 5);
        return into;
    }

    /// <summary>Records this cascade's depth-only draw for every primitive.</summary>
    public void DrawShadow(RenderPassBuilder pass, int cascade)
    {
        if (staged == 0) return;
        foreach (var part in parts)
        {
            if (cascade >= part.Casters.Length) continue;
            part.Casters[cascade].End(pass, null, palette.Handle);
        }
    }

    /// <summary>Records the lit draw for every primitive.</summary>
    public void DrawScene(RenderPassBuilder pass, IReadOnlyList<ShaderTextureBinding>? textures)
    {
        // <b>Said once, the first time bodies actually reach a draw.</b> Not a debug leftover: an empty
        // skinned draw is invisible and looks identical to a rendering fault, which cost a session to tell
        // apart — the batches were fed inside their own pass callback, which runs after the next frame has
        // already cleared the lists. One line proving posed bodies reached the GPU is worth keeping.
        if (!announced && staged > 0)
        {
            announced = true;
            Console.WriteLine($"  bodies: {staged} posed, {parts.Length} instanced draw(s) per pass");
        }
        if (staged == 0) return;
        foreach (var part in parts)
        {
            part.Scene.End(pass, BindingsFor(part, textures), palette.Handle);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var part in parts)
        {
            part.SceneBuffer.Dispose();
            foreach (var buffer in part.CasterBuffers) buffer.Dispose();
        }
    }
}
