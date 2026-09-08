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
    private readonly Matrix4x4 meshNode;
    private readonly Matrix4x4 normalise;
    private readonly Dictionary<string, AnimationClip> clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly AnimationClip[] byIndex;
    private readonly AnimationClip?[] bound;
    private int count;
    private bool disposed;

    /// <summary>One primitive of the body: its own mesh, its own tint, its own instance buffers.</summary>
    private sealed record Part(
        Mesh Mesh,
        Vector4 Tint,
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
        paletteScratch = new Matrix4x4[MaxBodies * skeleton.BoneCount];
        palettePayload = new byte[MaxBodies * skeleton.BoneCount * 64];
        foreach (var clip in animations)
        {
            // "HumanArmature|Man_Walk" — the armature prefix is the exporter's, not ours.
            var bar = clip.Name.LastIndexOf('|');
            clips[bar >= 0 ? clip.Name[(bar + 1)..] : clip.Name] = clip;
        }

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
    /// Static, and used by the measurement as well as the draw, which is the point: a body is measured
    /// through exactly the transform it is drawn through, so the two cannot disagree about where it is.
    /// </para>
    /// </remarks>
    private static void StripRootMotion(Skeleton skeleton, Pose pose)
    {
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].ParentIndex >= 0) continue;
            pose.Locals[i] = pose.Locals[i] with { Translation = Vector3.Zero };
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
            Console.WriteLine($"  bodies: {fileName} unusable, keeping the unrigged villager: {ex.Message}");
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
        StripRootMotion(model.Skeleton, restForMeasure);
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
            var authored = primitive.Material?.BaseColorFactor;
            var tint = tints is not null && tints.TryGetValue(name ?? string.Empty, out var found)
                ? found
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
            parts.Add(new Part(
                mesh, tint,
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

        return new SkinnedBodies(
            device, parts.ToArray(), palette, model.Skeleton,
            model.MeshNodeTransform, normalise, model.Animations)
        {
            FacingOffsetRadians = facing,
            StridePerCycle = stride,
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
    private static float MeasureStride(GltfModel model, float facingOffset, Matrix4x4 meshNode)
    {
        var skeleton = model.Skeleton;
        var foot = IndexOf(skeleton, "foot");
        var hips = IndexOf(skeleton, "hips");
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

        const int Samples = 24;
        for (var i = 0; i < Samples; i++)
        {
            pose.CopyFrom(skeleton.CreateRestPose());
            walk.Sample(walk.Duration * i / (Samples - 1.0), pose);
            StripRootMotion(skeleton, pose);
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

    private static int IndexOf(Skeleton skeleton, string contains)
    {
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var name = skeleton.Bones[i].Name ?? string.Empty;
            if (name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return i;
        }

        return -1;
    }

    /// <summary>Which way this rig faces, from the ankle-to-toe direction of either foot.</summary>
    private static (float Radians, string How) MeasureFacing(Skeleton skeleton)
    {
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var name = skeleton.Bones[i].Name ?? string.Empty;
            if (name.IndexOf("toe", StringComparison.OrdinalIgnoreCase) < 0) continue;

            var parent = skeleton.Bones[i].ParentIndex;
            if (parent < 0) continue;
            var parentName = skeleton.Bones[parent].Name ?? string.Empty;
            if (parentName.IndexOf("foot", StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (!Matrix4x4.Invert(skeleton.Bones[i].InverseBindPose, out var toe)) continue;
            if (!Matrix4x4.Invert(skeleton.Bones[parent].InverseBindPose, out var foot)) continue;

            var ahead = new Vector2(
                toe.Translation.X - foot.Translation.X,
                toe.Translation.Z - foot.Translation.Z);
            if (ahead.LengthSquared() < 1e-8f) continue;

            // The draw computes yaw as -atan2(facing.z, facing.x) for a model whose forward is +X. This
            // rig's own forward, expressed the same way, is what has to be cancelled out.
            var mine = -MathF.Atan2(ahead.Y, ahead.X);
            return (-mine, $"from {parentName} → {name}");
        }

        return (0f, "— no toe bone; assuming the model already faces +X");
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
        int casterMask = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (count >= MaxBodies) return false;

        // <b>Reset, sample, palette — and the reset is not optional.</b> A clip writes only the bones it has
        // tracks for, and one Pose is shared by every body here, so without this each body inherits whatever
        // the last one left in the bones its own clip does not touch. The symptom is precise and was reported
        // from the chair: an idle villager walking on the spot with its legs flailing, because the idle clip
        // has no leg tracks and the body sampled before it was mid-stride.
        pose.CopyFrom(restPose);
        clip.Sample(atSeconds, pose);
        StripRootMotion(skeleton, pose);
        skeleton.ComputeBonePalette(pose, bonePalette);
        bonePalette.Matrices.AsSpan().CopyTo(
            paletteScratch.AsSpan(count * skeleton.BoneCount, skeleton.BoneCount));

        var model = meshNode * normalise * placement;
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
        foreach (var part in parts) part.Scene.End(pass, textures, palette.Handle);
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
