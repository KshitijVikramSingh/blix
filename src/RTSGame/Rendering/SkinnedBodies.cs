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
    /// <b>Root motion is somebody else's job.</b> A walk clip authored to travel carries the character
    /// forward in its own translation tracks — which is right in an animation package and wrong here,
    /// because position is the simulation's and only the simulation's. Left in, a body slides away from the
    /// coordinates the locomotion layer, the collision layer and the mouse all agree it occupies; the
    /// reported symptom is a sway.
    /// <para>
    /// Horizontal only. The vertical component is a gait's own bob and rise, which is motion in place and
    /// wanted — flattening it too would make the walk read as a shuffle.
    /// </para>
    /// </remarks>
    private void StripRootMotion()
    {
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].ParentIndex >= 0) continue;
            var local = pose.Locals[i];
            pose.Locals[i] = local with
            {
                Translation = new Vector3(0f, local.Translation.Y, 0f),
            };
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

        // <b>Height measured through the mesh node, because that is where the file's scale lives.</b> This
        // asset is authored at a hundredth and scaled back up by its ancestor chain, so the primitives'
        // own bounds are not in metres and normalising against them would give a body a centimetre tall.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var primitive in model.Primitives)
        {
            foreach (var corner in Corners(primitive.Mesh.Bounds))
            {
                var at = Vector3.Transform(corner, model.MeshNodeTransform);
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
        }

        var tall = MathF.Max(0.0001f, max.Y - min.Y);
        // Feet to the origin, then scaled to the height the locomotion layer was calibrated against. A
        // person is the one thing in the settlement whose scale is set by how tall it is rather than by its
        // footprint — see the note on the unrigged villager, which said the same thing first.
        var normalise = Matrix4x4.CreateTranslation(0f, -min.Y, 0f) *
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
            var tint = tints is not null && tints.TryGetValue(name ?? string.Empty, out var found)
                ? found
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

        return new SkinnedBodies(
            device, parts.ToArray(), palette, model.Skeleton,
            model.MeshNodeTransform, normalise, model.Animations);
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
        StripRootMotion();
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
