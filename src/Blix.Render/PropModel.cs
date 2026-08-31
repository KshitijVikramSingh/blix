using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// L3 — one static art asset, drawn many times, in as many materials as it has.
//
// The gap this fills: InstancedBatch draws ONE mesh with ONE pipeline many times, which
// is exactly right for a cube and useless for an imported model. A low-poly building is
// one mesh split into a primitive per material — walls, roof, timber, thatch — and there
// is no per-vertex colour, so the material's base colour has to arrive as the instance
// tint. That means N batches which must all be given the SAME set of instances, and
// keeping those in step by hand is how a roof ends up on a different building from its
// walls. Both TankArena and Bulwark grew a private version of this before it lived here.
//
// It also draws each part TWICE — once lit into a scene pass, once depth-only into a
// shadow pass — because a prop that does not cast is a prop that does not look like it
// is standing on the ground. Every list is filled by the same Add call, which is the
// property worth having: a caster cannot drift from what it casts for.
//
// The caster may be built from DIFFERENT geometry than the scene draw, which is the one
// place the two are allowed to disagree — see the casterParts argument to Create. A
// shadow is a silhouette resolved to a shadow map's texels, so it can afford geometry
// the camera could not: a four-hundred-triangle tree casts a shadow indistinguishable
// from the four-thousand-triangle one it stands in for. What is NOT allowed is a
// different instance list, because that is how a shadow ends up under nothing.
//
// Geometry only. It owns meshes, instance buffers and batches; it does NOT own a
// pipeline, a shader, a push-constant layout, or an opinion about lighting. The caller
// brings its scene pipeline, its caster pipeline and both push payloads, exactly as
// ParticleBatch and InstancedBatch require.
//
// Construct with Create() from already-imported meshes: this assembly cannot see the
// glTF importer (Blix depends on Blix.Render, not the other way round), and that is the
// right layering — importing is the asset layer's job and drawing is this one's.
//
// Usage per frame:
//     prop.Begin();
//     foreach (var thing in things) prop.Add(thing.Model);
//     prop.Stage(scenePush, shadowPush);
//     graph.Pass(shadowPass, scope => prop.DrawShadow(scope));
//     graph.Pass(scenePass,  scope => prop.DrawScene(scope, shadowBinding));
public sealed class PropModel : IDisposable
{
    private readonly Part[] parts;

    /// <summary>
    /// One entry per shadow pass, each owning its own geometry, buffers and instance list.
    /// </summary>
    /// <remarks>
    /// <b>Per pass rather than per part, because the far cascades want cheaper geometry than the near one.</b>
    /// The previous shape hung an array of batches off each part and pointed all of them at one mesh, which
    /// gave each cascade its own instances and forced the same triangles on all three. Measured on a wooded
    /// map at the 118 m standoff: 16.7M submitted caster triangles against the scene's 8.1M, and an ablation
    /// that denied the two coarse cascades their casters returned 12.2 ms of a 46 ms frame. A shadow is a
    /// silhouette resolved to that map's texels — 45 cm in the far cascade — so it can afford geometry the
    /// near map could not, and the only way to say so is to let each pass own its own mesh set.
    /// <para>
    /// The instances are also kept once per pass rather than once per part per pass, which is what the old
    /// shape did: a two-material caster stored every placement twice and appended to both lists.
    /// </para>
    /// </remarks>
    private readonly CasterPass[] casterPasses;

    private PropModel(
        string name,
        Part[] parts,
        CasterPass[] casterPasses,
        Bounds3 bounds,
        int triangles,
        VulkanGraphicsDevice sceneDevice,
        ShaderProgramHandle sceneShader,
        PipelineHandle scenePipeline)
    {
        Name = name;
        this.parts = parts;
        this.casterPasses = casterPasses;
        Bounds = bounds;
        TriangleCount = triangles;
        // Kept so a part can grow another buffer's worth of copies mid-frame. See StageScene.
        this.sceneDevice = sceneDevice;
        this.sceneShader = sceneShader;
        this.scenePipeline = scenePipeline;
    }

    private readonly VulkanGraphicsDevice sceneDevice;
    private readonly ShaderProgramHandle sceneShader;
    private readonly PipelineHandle scenePipeline;

    public string Name { get; }

    // Extent of the assembled model after whatever transform the caller baked in.
    public Bounds3 Bounds { get; }

    // Triangles for one copy, summed over the parts — the number a caller needs to
    // decide whether four hundred of these is reasonable.
    public int TriangleCount { get; }

    // Triangles one copy costs the sun's pass, which is TriangleCount unless the caller
    // gave the caster its own geometry. Worth reporting separately: the shadow pass draws
    // every caster in the box whether or not the camera can see it, so this is often the
    // larger of the two numbers and the one a frame budget trips over.
    //
    // <b>This is the FIRST pass's figure.</b> Once a caller gives the later passes coarser geometry the
    // model no longer has one caster cost, and a load computed as instances x this number understates the
    // cheap passes and overstates nothing — which is a lie in the safe direction and still a lie. Anything
    // reporting a total wants CasterTriangleCountIn per pass; see StagedCasterLoad in RTSGame.
    public int CasterTriangleCount => casterPasses.Length > 0 ? casterPasses[0].TriangleCount : 0;

    /// <summary>Triangles one copy costs ONE shadow pass, which differs per pass once geometry does.</summary>
    public int CasterTriangleCountIn(int pass) =>
        pass >= 0 && pass < casterPasses.Length ? casterPasses[pass].TriangleCount : 0;

    public int PartCount => parts.Length;

    // Copies staged this frame.
    public int InstanceCount => parts.Length == 0 ? 0 : parts[0].Instances.Count;

    /// <summary>
    /// How many copies are casting this frame, which is not the same as how many are drawn.
    /// </summary>
    /// <remarks>
    /// <b>Reported separately because assuming it equals <see cref="InstanceCount"/> made a caster cull
    /// invisible.</b> A caller placing copies with <see cref="AddUnlit"/> draws them and casts nothing, so a
    /// load figure computed as <c>instances x casterTriangles</c> answers "what would this cost if everything
    /// cast" — which is a fair question and not the one being asked when you are trying to find out whether
    /// the cull worked.
    /// </remarks>
    public int CasterInstanceCount
    {
        get
        {
            var total = 0;
            foreach (var pass in casterPasses) total += pass.Instances.Count;
            return total;
        }
    }

    /// <summary>How many passes the caster geometry is kept per, so a caller can walk them by index.</summary>
    public int CasterPassCount => casterPasses.Length;

    /// <summary>
    /// How many copies are casting into ONE pass this frame.
    /// </summary>
    /// <remarks>
    /// <b>The number <see cref="CasterInstanceCount"/> hides by summing.</b> Once each cascade keeps its own
    /// instance list, the total says what the sun's passes cost together and nothing about whether the split
    /// did anything: three cascades holding the same 2.9M triangles and three holding 0.8M/2.6M/2.9M sum to
    /// figures a total cannot tell apart. A per-cascade partition can only be judged per cascade.
    /// </remarks>
    public int CasterInstanceCountIn(int pass) =>
        pass >= 0 && pass < casterPasses.Length ? casterPasses[pass].Instances.Count : 0;

    // Build from a set of (mesh, tint) parts — one per material of the source asset.
    //
    // `bake` is applied to every part's geometry once, here, rather than to every
    // instance every frame. Normalising an asset (recentre it, stand it on the origin,
    // scale it to a unit footprint) is a fact about the asset, and baking it means an
    // instance matrix is only ever the placement the game actually cares about.
    //
    // Pass casterPipeline/casterShader null for a prop that receives light but does not
    // cast — ground decals, plots, anything flat enough that its own shadow is noise.
    //
    // `casterParts` substitutes geometry for the shadow pass only, and defaults to the
    // scene geometry. It takes the same `bake`, which is what keeps a substitute standing
    // where the thing it casts for stands; a caster normalised to its own bounds would
    // sit a few centimetres off and put the shadow beside the trunk.
    public static PropModel Create(
        VulkanGraphicsDevice device,
        string name,
        IEnumerable<(MeshData Mesh, Vector4 Tint)> parts,
        ShaderProgramHandle sceneShader,
        PipelineHandle scenePipeline,
        ShaderProgramHandle? casterShader = null,
        PipelineHandle? casterPipeline = null,
        Matrix4x4? bake = null,
        IEnumerable<MeshData>? casterParts = null,
        int casterPassCount = 1,
        IReadOnlyList<IEnumerable<MeshData>?>? casterPartsByPass = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(parts);
        if (casterShader.HasValue != casterPipeline.HasValue)
        {
            throw new ArgumentException(
                "A shadow caster needs both a shader and a pipeline: the instance buffer's material is " +
                "created against the shader, and the draw is recorded against the pipeline.",
                nameof(casterPipeline));
        }
        if (casterPassCount is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(casterPassCount), casterPassCount, "A prop needs between one and thirty caster passes.");
        }
        if (casterPartsByPass is not null && casterPartsByPass.Count != casterPassCount)
        {
            throw new ArgumentException(
                $"Per-pass caster geometry must cover every pass: {casterPartsByPass.Count} sets were given " +
                $"for {casterPassCount} passes. A pass with no entry would cast nothing, which is a missing " +
                "shadow rather than a saving.",
                nameof(casterPartsByPass));
        }
        if (casterPartsByPass is not null && casterParts is not null)
        {
            throw new ArgumentException(
                "Give either one caster geometry for every pass or one per pass, not both.",
                nameof(casterPartsByPass));
        }

        var built = new List<Part>();
        var meshes = new List<MeshData>();
        var sceneUploads = new List<Mesh>();
        var triangles = 0;
        var index = 0;
        var casting = casterShader is not null && casterPipeline is not null;
        foreach (var (source, tint) in parts)
        {
            var mesh = bake is { } transform
                ? source.Transformed(transform, $"{name}.{index}")
                : source;
            if (mesh.IndexCount == 0) continue;
            meshes.Add(mesh);
            triangles += mesh.IndexCount / 3;
            var uploaded = Upload(device, mesh);
            sceneUploads.Add(uploaded);
            var sceneBuffer = new InstanceBuffer(device, sceneShader, $"{name}.{index}.scene");
            var part = new Part
            {
                // <b>The caller's fourth channel is the caller's business.</b> This forced it to one, which
                // is right for a colour and wrong for a channel: an opaque pass has no use for alpha, so a
                // game is free to put something else there — RTSGame carries a material class in it, which
                // is how a barn's plaster and a tree's canopy end up shaded differently without this
                // primitive learning what either of those is. Geometry only; the meaning is the caller's.
                Tint = tint,
                SceneBuffer = sceneBuffer,
                Scene = new InstancedBatch(uploaded, scenePipeline, sceneBuffer),
                SceneMesh = uploaded,
            };
            built.Add(part);
            index++;
        }

        // <b>Substituted caster geometry is its own set rather than a second batch hung off the scene parts,
        // because a decimated model need not have the same number of primitives as the model it came from</b>
        // — pruning can drop one entirely, and a synthesised proxy has no relation to the original's parts at
        // all — so pairing them by index would silently mis-tint or crash.
        var passes = new List<CasterPass>();
        if (casting && casterShader is { } shader && casterPipeline is { } pipeline)
        {
            // One upload per distinct MeshData, so the common cases stay free: every pass sharing one
            // substitute uploads it once, and a pass that takes the scene geometry reuses the scene's upload
            // rather than putting the same vertices on the device twice.
            // Keyed on the RAW source mesh, before the bake: the bake is the same transform for every pass,
            // so two passes handed the same source want one upload — but each would call Transformed for
            // itself and produce a different object, which a key on the baked mesh would never match.
            var uploads = new List<(MeshData Source, Mesh Uploaded)>();
            for (var c = 0; c < casterPassCount; c++)
            {
                // A null entry means this pass casts from the scene geometry, which is the same thing
                // passing no substitute at all means — and it is worth having, because it is how a near
                // cascade keeps the real silhouette while the coarse ones take a stand-in, without the
                // caller having to hand back geometry that is already on the device.
                var source = casterPartsByPass is not null
                    ? casterPartsByPass[c]
                    : casterParts;
                var pass = new CasterPass();
                if (source is null)
                {
                    // No substitute: this pass casts from the scene geometry it was built beside.
                    pass.Create(device, sceneUploads, shader, pipeline, $"{name}.caster.{c}");
                    pass.TriangleCount = triangles;
                }
                else
                {
                    var passMeshes = new List<Mesh>();
                    var passTriangles = 0;
                    var slot = 0;
                    foreach (var raw in source)
                    {
                        var cached = uploads.FindIndex(entry => ReferenceEquals(entry.Source, raw));
                        Mesh uploaded;
                        if (cached >= 0)
                        {
                            uploaded = uploads[cached].Uploaded;
                            passTriangles += uploaded.IndexCount / 3;
                        }
                        else
                        {
                            var mesh = bake is { } transform
                                ? raw.Transformed(transform, $"{name}.caster.{c}.{slot}")
                                : raw;
                            if (mesh.IndexCount == 0) continue;
                            passTriangles += mesh.IndexCount / 3;
                            uploaded = Upload(device, mesh);
                            uploads.Add((raw, uploaded));
                        }

                        passMeshes.Add(uploaded);
                        slot++;
                    }

                    pass.Create(device, passMeshes, shader, pipeline, $"{name}.caster.{c}");
                    pass.TriangleCount = passTriangles;
                }

                passes.Add(pass);
            }
        }

        return new PropModel(
            name,
            built.ToArray(),
            passes.ToArray(),
            meshes.CombinedBounds(),
            triangles,
            device,
            sceneShader,
            scenePipeline);
    }

    // Drop every copy staged last frame. Call once, before the frame's Adds.
    public void Begin()
    {
        foreach (var part in parts) part.Clear();
        foreach (var pass in casterPasses) pass.Instances.Clear();
    }

    // Place one copy. The transform is whatever the caller's shader expects to multiply
    // a vertex by — this class never reads it.
    public void Add(Matrix4x4 model)
    {
        Add(model, int.MaxValue);
    }

    /// <summary>
    /// Places one copy and casts it only into the passes selected by <paramref name="casterMask"/>.
    /// </summary>
    /// <remarks>
    /// A distinct list and GPU buffer belong to every pass. Sharing the buffer is subtly incorrect: writes
    /// target the current frame slot, so every recorded draw would otherwise see the final subset uploaded.
    /// </remarks>
    public void Add(Matrix4x4 model, int casterMask)
    {
        foreach (var part in parts) part.Instances.Add(new InstanceData(model, part.Tint));
        // <b>One caster instance per pass, not per part per pass.</b> The tint goes along because the
        // instance layout carries one; a depth-only pass never reads it, so the first part's is as good as
        // any and better than inventing a colour.
        AddCasters(model, parts.Length > 0 ? parts[0].Tint : Vector4.Zero, casterMask);
    }

    private void AddCasters(Matrix4x4 model, Vector4 tint, int casterMask)
    {
        for (var c = 0; c < casterPasses.Length; c++)
        {
            if ((casterMask & (1 << c)) != 0) casterPasses[c].Instances.Add(new InstanceData(model, tint));
        }
    }

    /// <summary>
    /// Places one copy that is drawn but casts nothing.
    /// </summary>
    /// <remarks>
    /// <b>For anything standing outside the light's own box.</b> A caster submitted beyond the shadow map's
    /// extent is geometry rasterised into a texture it cannot land in — the whole cost and none of the effect.
    /// Measured on a wooded map at maximum zoom: 8.2M of 18.8M triangles submitted were casters, and most
    /// belonged to trees between the box's edge and the draw bound.
    /// <para>
    /// A separate method rather than a flag, so the decision is visible at the call site. Whether a thing is
    /// inside the light's box is the caller's knowledge, not the model's.
    /// </para>
    /// </remarks>
    public void AddUnlit(Matrix4x4 model)
    {
        foreach (var part in parts) part.Instances.Add(new InstanceData(model, part.Tint));
    }

    // Place one copy in a single colour, overriding every material. For a faction tint,
    // a highlight, or a ghost — the cases where the asset's own palette is not wanted.
    public void Add(Matrix4x4 model, Vector4 tint)
    {
        Add(model, tint, int.MaxValue);
    }

    public void Add(Matrix4x4 model, Vector4 tint, int casterMask)
    {
        foreach (var part in parts) part.Instances.Add(new InstanceData(model, tint));
        AddCasters(model, tint, casterMask);
    }

    // Hand this frame's instances and push payloads to the batches. Split from the draws
    // because the two draws happen in two different passes, and a batch's Begin/End pair
    // brackets a single pass.
    public void Stage(ReadOnlySpan<byte> scenePush, ReadOnlySpan<byte> shadowPush)
    {
        foreach (var part in parts) StageScene(part, scenePush);
        if (casterPasses.Length > 0) casterPasses[0].Stage(shadowPush);
    }

    /// <summary>
    /// Hands one part's copies to its batches, in as many buffers' worth as it takes.
    /// </summary>
    /// <remarks>
    /// The scene half of the same problem the caster half hit: a buffer holds
    /// <see cref="InstanceBuffer.MaxInstances"/> copies, and a model that stands in for four levels of detail
    /// can be asked to draw more than that in one frame. The first chunk uses the part's original batch so the
    /// ordinary case is unchanged; the rest are added on demand and kept.
    /// </remarks>
    private void StageScene(Part part, ReadOnlySpan<byte> scenePush)
    {
        var instances = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.Instances);
        var capacity = InstanceBuffer.MaxInstances;
        var first = Math.Min(capacity, instances.Length);
        part.Scene!.Begin(scenePush);
        part.Scene.SetInstances(instances[..first]);

        var remaining = instances.Length - first;
        var extras = remaining <= 0 ? 0 : (remaining + capacity - 1) / capacity;
        while (part.ExtraBuffers.Count < extras)
        {
            var buffer = new InstanceBuffer(
                sceneDevice, sceneShader, $"{Name}.scene.extra{part.ExtraBuffers.Count}");
            part.ExtraBuffers.Add(buffer);
            part.ExtraBatches.Add(new InstancedBatch(part.SceneMesh, scenePipeline, buffer));
        }

        part.ExtraInUse = extras;
        for (var c = 0; c < extras; c++)
        {
            var offset = first + c * capacity;
            var length = Math.Min(capacity, instances.Length - offset);
            part.ExtraBatches[c].Begin(scenePush);
            part.ExtraBatches[c].SetInstances(instances.Slice(offset, length));
        }
    }

    /// <summary>Stages distinct caster storage for every shadow pass.</summary>
    public void StageCascades(ReadOnlySpan<byte> scenePush, IReadOnlyList<byte[]> shadowPushes)
    {
        if (casterPasses.Length > 0 && casterPasses.Length != shadowPushes.Count)
        {
            throw new ArgumentException(
                $"Prop caster has {casterPasses.Length} passes but received {shadowPushes.Count} push " +
                "payloads.",
                nameof(shadowPushes));
        }

        foreach (var part in parts) StageScene(part, scenePush);
        for (var c = 0; c < casterPasses.Length; c++) casterPasses[c].Stage(shadowPushes[c]);
    }

    public void DrawScene(RenderPassBuilder pass, IReadOnlyList<ShaderTextureBinding>? textures = null)
    {
        foreach (var part in parts)
        {
            part.Scene!.End(pass, textures);
            for (var c = 0; c < part.ExtraInUse; c++) part.ExtraBatches[c].End(pass, textures);
        }
    }

    public void DrawShadow(RenderPassBuilder pass)
    {
        DrawShadow(pass, 0);
    }

    /// <summary>Closes the already-staged caster batch for one shadow pass.</summary>
    public void DrawShadow(RenderPassBuilder pass, int casterPass)
    {
        if (casterPass < 0 || casterPass >= casterPasses.Length) return;
        casterPasses[casterPass].Draw(pass);
    }

    /// <summary>
    /// Casts the same instances again, into another pass, under another matrix.
    /// </summary>
    /// <remarks>
    /// <b>For cascaded shadow maps, where the geometry is one set and the projections are several.</b>
    /// <see cref="Stage"/> begins each caster batch with a single shadow push, which is exactly right for one
    /// map and one short of the answer for three. This re-begins from the instances already collected, so the
    /// scene-side staging is not repeated and the instance list is walked once per cascade rather than rebuilt.
    /// <para>
    /// Only valid after <see cref="DrawShadow"/> has closed the staged batch — a batch cannot be begun while
    /// active, and that rule is worth keeping loud rather than working around.
    /// </para>
    /// </remarks>
    public void DrawShadow(RenderPassBuilder pass, ReadOnlySpan<byte> shadowPush)
    {
        if (casterPasses.Length == 0) return;
        casterPasses[0].Stage(shadowPush);
        casterPasses[0].Draw(pass);
    }

    public void Dispose()
    {
        foreach (var part in parts)
        {
            part.SceneBuffer?.Dispose();
            foreach (var buffer in part.ExtraBuffers) buffer.Dispose();
        }
        foreach (var pass in casterPasses)
        {
            foreach (var buffer in pass.AllBuffers) buffer.Dispose();
        }
    }

    // A transform that normalises an asset the way a game placing it on the ground
    // wants: centred on its own footprint, standing on Y = 0, and scaled so the wider
    // of its two ground dimensions is exactly one unit.
    //
    // Which makes an instance matrix `Scale(width) * RotateY(yaw) * Translate(x, y, z)`
    // and nothing else — width in metres, y the terrain height under the position. No
    // per-asset constants, because every number here is measured from the geometry: a
    // pack of a hundred and thirty models authored to slightly different conventions all
    // arrive the same way up and the same size, and adding the hundred and thirty-first
    // needs no fitting pass.
    public static Matrix4x4 NormaliseToUnitFootprint(Bounds3 bounds)
    {
        var size = bounds.Max - bounds.Min;
        var footprint = MathF.Max(size.X, size.Z);
        var scale = footprint > 1e-4f ? 1f / footprint : 1f;
        var centre = new Vector3(
            (bounds.Min.X + bounds.Max.X) * 0.5f,
            bounds.Min.Y,
            (bounds.Min.Z + bounds.Max.Z) * 0.5f);
        return Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateScale(scale);
    }

    private static Mesh Upload(VulkanGraphicsDevice device, MeshData mesh)
    {
        var vertices = device.CreateVertexBuffer(
            new VertexBufferData(
                new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static),
                mesh.VertexBytes),
            $"{mesh.Name}.vb");
        // A low-poly building fits u16 comfortably; a dense one (a field of wheat, a
        // rock group) does not, and the importer already decides which by vertex count.
        var (indices, count) = mesh.Indices32 is { } wide
            ? (device.CreateIndexBuffer(wide, name: $"{mesh.Name}.ib"), wide.Length)
            : (device.CreateIndexBuffer(mesh.Indices, name: $"{mesh.Name}.ib"), mesh.Indices.Length);
        return new Mesh(mesh.Name, vertices, indices, count, mesh.Bounds);
    }

    private sealed class Part
    {
        public Vector4 Tint;
        public InstanceBuffer? SceneBuffer;
        public InstancedBatch? Scene;

        /// <summary>The uploaded geometry, kept so extra chunks can be built against the same mesh.</summary>
        public Mesh SceneMesh = null!;

        public readonly List<InstanceData> Instances = new();

        /// <summary>Extra buffers and batches for the copies past the first buffer's worth. See CasterPass.</summary>
        public readonly List<InstanceBuffer> ExtraBuffers = new();

        public readonly List<InstancedBatch> ExtraBatches = new();

        public int ExtraInUse;

        public void Clear() => Instances.Clear();
    }

    /// <summary>One shadow pass's geometry, buffers and instances.</summary>
    /// <remarks>
    /// A pass may hold several meshes — one per material of the substituted geometry, or per material of the
    /// scene model when nothing was substituted — and they all take the same instance list, because they are
    /// parts of one thing standing in one place. That is the invariant the old per-part arrangement kept by
    /// appending to several lists in step; keeping one list is the same property without the bookkeeping.
    /// </remarks>
    private sealed class CasterPass
    {
        /// <summary>
        /// One mesh's buffers and batches, one pair per chunk of instances it has needed so far.
        /// </summary>
        /// <remarks>
        /// <b>Chunked because a caller can legitimately have more copies than a buffer holds.</b> An instance
        /// buffer tops out at InstanceBuffer.MaxInstances, which was never a problem while a species had four
        /// levels of detail and the copies spread across forty models — and became one the moment RTSGame's
        /// cheap-tree swap collapsed those forty into six and the zoom was unpinned far enough to show 24,000
        /// trees at once. It threw: "given 16714 instances; max is 16384", at a 450 m standoff.
        /// <para>
        /// Raising the constant was the wrong answer — the buffer's storage block is sized from it, so every
        /// one of the dozens of buffers in a frame would grow, hundreds of megabytes to serve one model. A
        /// primitive asked to draw N copies of a mesh should draw them in as many submissions as its buffers
        /// require, and nothing above it should have to know the number.
        /// </para>
        /// <para>
        /// Buffers are added on demand and kept: a frame that needed six chunks once will very likely need
        /// them again, and allocating on the way up beats allocating every frame.
        /// </para>
        /// </remarks>
        private sealed class MeshChunks
        {
            public Mesh Geometry = null!;
            public readonly List<InstanceBuffer> Buffers = new();
            public readonly List<InstancedBatch> Batches = new();
            public int InUse;
        }

        private MeshChunks[] meshes = Array.Empty<MeshChunks>();
        private VulkanGraphicsDevice device = null!;
        private ShaderProgramHandle shader;
        private PipelineHandle pipeline;
        private string name = string.Empty;

        /// <summary>Triangles one copy costs THIS pass.</summary>
        public int TriangleCount;

        public readonly List<InstanceData> Instances = new();

        public IEnumerable<InstanceBuffer> AllBuffers
        {
            get
            {
                foreach (var mesh in meshes)
                foreach (var buffer in mesh.Buffers)
                {
                    yield return buffer;
                }
            }
        }

        public void Create(
            VulkanGraphicsDevice graphicsDevice,
            IReadOnlyList<Mesh> geometry,
            ShaderProgramHandle casterShader,
            PipelineHandle casterPipeline,
            string label)
        {
            device = graphicsDevice;
            shader = casterShader;
            pipeline = casterPipeline;
            name = label;
            meshes = new MeshChunks[geometry.Count];
            for (var i = 0; i < geometry.Count; i++)
            {
                meshes[i] = new MeshChunks { Geometry = geometry[i] };
                // The first chunk eagerly, so the common case allocates exactly what it used to.
                AddChunk(meshes[i], i);
            }
        }

        private void AddChunk(MeshChunks mesh, int meshIndex)
        {
            // <b>A buffer per chunk, not per mesh.</b> InstanceBuffer writes one current-frame slot, so two
            // batches sharing one buffer would both draw whatever was uploaded last — the same failure the
            // per-cascade split was made to avoid, one level further down.
            var buffer = new InstanceBuffer(device, shader, $"{name}.{meshIndex}.{mesh.Buffers.Count}");
            mesh.Buffers.Add(buffer);
            mesh.Batches.Add(new InstancedBatch(mesh.Geometry, pipeline, buffer));
        }

        public void Stage(ReadOnlySpan<byte> push)
        {
            var instances = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Instances);
            var capacity = InstanceBuffer.MaxInstances;
            // At least one chunk even when empty, so Draw ends exactly what Stage began and a pass with no
            // instances still records its (empty) draw as it always did.
            var chunks = Math.Max(1, (instances.Length + capacity - 1) / capacity);
            for (var m = 0; m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                while (mesh.Buffers.Count < chunks) AddChunk(mesh, m);
                mesh.InUse = chunks;
                for (var c = 0; c < chunks; c++)
                {
                    var offset = c * capacity;
                    var length = Math.Min(capacity, Math.Max(0, instances.Length - offset));
                    mesh.Batches[c].Begin(push);
                    mesh.Batches[c].SetInstances(instances.Slice(offset, length));
                }
            }
        }

        public void Draw(RenderPassBuilder pass)
        {
            foreach (var mesh in meshes)
            {
                for (var c = 0; c < mesh.InUse; c++) mesh.Batches[c].End(pass);
            }
        }
    }
}
