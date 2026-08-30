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
    private readonly Part[] casters;

    private PropModel(string name, Part[] parts, Part[] casters, Bounds3 bounds, int triangles, int casterTriangles)
    {
        Name = name;
        this.parts = parts;
        this.casters = casters;
        Bounds = bounds;
        TriangleCount = triangles;
        CasterTriangleCount = casterTriangles;
    }

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
    public int CasterTriangleCount { get; }

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
            var source = casters.Length > 0 ? casters[0] : parts.FirstOrDefault(part => part.Casters.Length > 0);
            return source?.CasterInstances.Sum(instances => instances.Count) ?? 0;
        }
    }

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
        int casterPassCount = 1)
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

        var built = new List<Part>();
        var meshes = new List<MeshData>();
        var triangles = 0;
        var index = 0;
        var casting = casterShader is not null && casterPipeline is not null;
        // Only when the caller substituted geometry; otherwise the caster shares the scene mesh's upload,
        // which is the whole reason the default costs nothing.
        var substitutes = casting && casterParts is not null ? casterParts.ToArray() : null;
        foreach (var (source, tint) in parts)
        {
            var mesh = bake is { } transform
                ? source.Transformed(transform, $"{name}.{index}")
                : source;
            if (mesh.IndexCount == 0) continue;
            meshes.Add(mesh);
            triangles += mesh.IndexCount / 3;
            var uploaded = Upload(device, mesh);
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
            };
            if (substitutes is null && casterShader is { } cs && casterPipeline is { } cp)
            {
                part.CreateCasters(device, uploaded, cs, cp, $"{name}.{index}.caster", casterPassCount);
            }

            built.Add(part);
            index++;
        }

        // Substituted casters are their own parts rather than a second batch hung off the scene parts,
        // because a decimated model need not have the same number of primitives as the model it came from
        // — pruning can drop one entirely — and pairing them by index would silently mis-tint or crash.
        var casters = new List<Part>();
        var casterTriangles = 0;
        if (substitutes is { Length: > 0 } && casterShader is { } shader && casterPipeline is { } pipeline)
        {
            var slot = 0;
            foreach (var source in substitutes)
            {
                var mesh = bake is { } transform
                    ? source.Transformed(transform, $"{name}.caster.{slot}")
                    : source;
                if (mesh.IndexCount == 0) continue;
                casterTriangles += mesh.IndexCount / 3;
                var part = new Part();
                part.CreateCasters(
                    device, Upload(device, mesh), shader, pipeline, $"{name}.{slot}.caster", casterPassCount);
                casters.Add(part);
                slot++;
            }
        }

        return new PropModel(
            name,
            built.ToArray(),
            casters.ToArray(),
            meshes.CombinedBounds(),
            triangles,
            casters.Count > 0 ? casterTriangles : casting ? triangles : 0);
    }

    // Drop every copy staged last frame. Call once, before the frame's Adds.
    public void Begin()
    {
        foreach (var part in parts) part.Clear();
        foreach (var part in casters) part.Clear();
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
        foreach (var part in parts) part.Add(new InstanceData(model, part.Tint), casterMask, scene: true);
        foreach (var part in casters) part.Add(new InstanceData(model, part.Tint), casterMask, scene: false);
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
        foreach (var part in parts) part.Add(new InstanceData(model, tint), casterMask, scene: true);
        foreach (var part in casters) part.Add(new InstanceData(model, tint), casterMask, scene: false);
    }

    // Hand this frame's instances and push payloads to the batches. Split from the draws
    // because the two draws happen in two different passes, and a batch's Begin/End pair
    // brackets a single pass.
    public void Stage(ReadOnlySpan<byte> scenePush, ReadOnlySpan<byte> shadowPush)
    {
        foreach (var part in parts)
        {
            var instances = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.Instances);
            part.Scene!.Begin(scenePush);
            part.Scene.SetInstances(instances);
            if (part.Casters.Length == 0) continue;
            part.Casters[0].Begin(shadowPush);
            part.Casters[0].SetInstances(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.CasterInstances[0]));
        }

        foreach (var part in casters)
        {
            var instances = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.CasterInstances[0]);
            part.Casters[0].Begin(shadowPush);
            part.Casters[0].SetInstances(instances);
        }
    }

    /// <summary>Stages distinct caster storage for every shadow pass.</summary>
    public void StageCascades(ReadOnlySpan<byte> scenePush, IReadOnlyList<byte[]> shadowPushes)
    {
        foreach (var part in parts)
        {
            part.Scene!.Begin(scenePush);
            part.Scene.SetInstances(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.Instances));
            StageCasters(part, shadowPushes);
        }

        foreach (var part in casters) StageCasters(part, shadowPushes);
    }

    public void DrawScene(RenderPassBuilder pass, IReadOnlyList<ShaderTextureBinding>? textures = null)
    {
        foreach (var part in parts) part.Scene!.End(pass, textures);
    }

    public void DrawShadow(RenderPassBuilder pass)
    {
        DrawShadow(pass, 0);
    }

    /// <summary>Closes the already-staged caster batch for one shadow pass.</summary>
    public void DrawShadow(RenderPassBuilder pass, int casterPass)
    {
        foreach (var part in parts)
        {
            if (casterPass < part.Casters.Length) part.Casters[casterPass].End(pass);
        }
        foreach (var part in casters) part.Casters[casterPass].End(pass);
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
        foreach (var part in parts)
        {
            if (part.Casters.Length == 0) continue;
            part.Casters[0].Begin(shadowPush);
            part.Casters[0].SetInstances(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.CasterInstances[0]));
            part.Casters[0].End(pass);
        }

        foreach (var part in casters)
        {
            part.Casters[0].Begin(shadowPush);
            part.Casters[0].SetInstances(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.CasterInstances[0]));
            part.Casters[0].End(pass);
        }
    }

    public void Dispose()
    {
        foreach (var part in parts)
        {
            part.SceneBuffer?.Dispose();
            foreach (var buffer in part.CasterBuffers) buffer.Dispose();
        }

        foreach (var part in casters)
        {
            foreach (var buffer in part.CasterBuffers) buffer.Dispose();
        }
    }

    private static void StageCasters(Part part, IReadOnlyList<byte[]> shadowPushes)
    {
        if (part.Casters.Length == 0) return;
        if (part.Casters.Length != shadowPushes.Count)
        {
            throw new ArgumentException(
                $"Prop caster has {part.Casters.Length} passes but received {shadowPushes.Count} push payloads.",
                nameof(shadowPushes));
        }

        for (var c = 0; c < part.Casters.Length; c++)
        {
            part.Casters[c].Begin(shadowPushes[c]);
            part.Casters[c].SetInstances(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.CasterInstances[c]));
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
        public InstanceBuffer[] CasterBuffers = Array.Empty<InstanceBuffer>();
        public InstancedBatch[] Casters = Array.Empty<InstancedBatch>();
        public List<InstanceData>[] CasterInstances = Array.Empty<List<InstanceData>>();
        public readonly List<InstanceData> Instances = new();

        public void CreateCasters(
            VulkanGraphicsDevice device,
            Mesh mesh,
            ShaderProgramHandle shader,
            PipelineHandle pipeline,
            string name,
            int count)
        {
            CasterBuffers = new InstanceBuffer[count];
            Casters = new InstancedBatch[count];
            CasterInstances = new List<InstanceData>[count];
            for (var c = 0; c < count; c++)
            {
                var buffer = new InstanceBuffer(device, shader, $"{name}.{c}");
                CasterBuffers[c] = buffer;
                Casters[c] = new InstancedBatch(mesh, pipeline, buffer);
                CasterInstances[c] = new List<InstanceData>();
            }
        }

        public void Clear()
        {
            Instances.Clear();
            foreach (var instances in CasterInstances) instances.Clear();
        }

        public void Add(InstanceData instance, int casterMask, bool scene)
        {
            if (scene) Instances.Add(instance);
            for (var c = 0; c < CasterInstances.Length; c++)
            {
                if ((casterMask & (1 << c)) != 0) CasterInstances[c].Add(instance);
            }
        }
    }
}
