using System.Numerics;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Render;

namespace Blix;

// Built scene from a GltfModel: every primitive uploaded as a Mesh, every
// glTF material constructed into a runtime Material with the right pipeline
// for its alpha mode and double-sided flag. The result is two flat lists --
// submeshes and unique materials -- plus a world-space bounds covering
// everything drawn.
//
// Why this exists: the Walkthrough demo grew a per-primitive material-build
// loop with hardcoded uniform names and a "tag the marble floor by bounds
// heuristic" detour. Once Sponza Modern + add-ons land we'll have many more
// primitives plus alphaMode and doubleSided that the per-primitive loop
// would have to encode anyway. GltfSceneInstance turns the inline loop into
// "supply N pipelines + a callback that binds scene-wide textures"; the
// demo keeps full control of those without owning the bookkeeping.
//
// Pipeline selection: glTF gives us three alpha modes (OPAQUE / MASK / BLEND)
// times double-sided (off/on) = six variants. The renderer rarely needs all
// six; demos declare the ones they want and the rest fall back to Opaque
// with a warning. Saves demos from having to declare a "doubleSided alpha
// blend" pipeline when they have no curtains.
//
// Material customization runs through `OnMaterialBuilt` -- the place to
// bind shadow maps, IBL probes, BRDF LUT, env cube, etc. that every
// material in the scene shares. Without it GltfSceneInstance would need
// to know about every scene-wide texture, which couples it to the
// scene-renderer it shouldn't know about.
public sealed class GltfSceneInstance : IDebugGeometrySource, IDebugSelectable, IDebugInspectable
{
    // Static AABB color for the per-submesh outlines. Pale cyan keeps
    // contrast against typical lit albedo without dominating.
    private static readonly GraphicsColor SubmeshAabbColor = new(0.30f, 0.85f, 0.95f, 1.0f);

    // Physical render units — what PbrSceneRenderer iterates and draws.
    // When batching is on, multiple glTF primitives can share one
    // SubmeshInstance via concatenated VBO/IBO/bounds.
    public IReadOnlyList<SubmeshInstance> Submeshes { get; }

    // Logical units — one per glTF primitive, regardless of batching.
    // What diagnostics enumerate: picking selects ONE primitive, the
    // selection viz shows its TIGHT bounds, IDebugInspectable surfaces
    // the primitive's own name/material/alpha state. The renderer
    // doesn't touch these; cross-referencing the batch that actually
    // draws this primitive is via PrimitiveSource.BatchIndex.
    public IReadOnlyList<PrimitiveSource> Primitives { get; }

    public Bounds3 Bounds { get; }
    public MaterialSet Materials { get; }

    // The producer's identity. Defaults to "scene/<options.Prefix>" so
    // any number of registered scene instances live under one toggleable
    // root ("scene") in the Layers panel while keeping per-instance
    // sub-toggles ("scene/sponza-main", "scene/curtains").
    public string DebugName { get; }

    private GltfSceneInstance(
        SubmeshInstance[] submeshes, PrimitiveSource[] primitives,
        Bounds3 bounds, MaterialSet materials, string debugName)
    {
        Submeshes = submeshes;
        Primitives = primitives;
        Bounds = bounds;
        Materials = materials;
        DebugName = debugName;
    }

    // IDebugGeometrySource: emits one Aabb per submesh under a path of
    // "scene/<name>/submesh-N/bounds". Skipped entirely when the layer
    // is off (DebugSystem.Run gates IDebugGeometrySource by
    // State.IsPathVisible(DebugName)). The auto-scope already pushes
    // DebugName, so emissions resolve to the right path without manual
    // scope manipulation here.
    public void EmitGeometry(DebugContext debug)
    {
        // Emit one AABB per LOGICAL primitive (not per render batch).
        // After batching, a single batch can contain dozens of
        // primitives; emitting per-batch would show the user one giant
        // box covering a whole material group. Per-primitive emission
        // matches what the picker sees, what the user clicked on, and
        // what they're trying to debug.
        //
        // Skip the AABB for whichever primitive (if any) is currently
        // selected — the selection sweep draws its own bright outline
        // there and we'd otherwise paint a faint cyan box on top.
        var selected = debug.SelectedPath;
        var selectedPrefix = DebugName + "/submesh-";
        var selectedIndex = -1;
        if (selected is not null && selected.StartsWith(selectedPrefix, StringComparison.Ordinal))
        {
            int.TryParse(selected.AsSpan(selectedPrefix.Length), out selectedIndex);
        }

        for (var i = 0; i < Primitives.Count; i++)
        {
            if (i == selectedIndex)
            {
                continue;
            }
            var p = Primitives[i];
            debug.Draw.Aabb($"submesh-{i}/bounds", p.Bounds.Min, p.Bounds.Max, SubmeshAabbColor);
        }
    }

    // IDebugSelectable: appends one DebugSelectable per LOGICAL primitive
    // (not per batch). Picking a chair selects that chair, not "every
    // submesh sharing the chair material" — which is what happened
    // pre-split when this enumerated Submeshes (batches) instead.
    public void CollectSelectables(List<DebugSelectable> destination)
    {
        for (var i = 0; i < Primitives.Count; i++)
        {
            var p = Primitives[i];
            destination.Add(new DebugSelectable(
                EntityPath: $"{DebugName}/submesh-{i}",
                Bounds: p.Bounds));
        }
    }

    // IDebugInspectable: emit material + alpha + double-sided info when
    // the selected path is one of our primitives. Looks up by primitive
    // index in O(1); also surfaces the batch index so the user can see
    // which physical batch actually carries this primitive's geometry.
    public void Inspect(string entityPath, DebugContext debug)
    {
        var prefix = DebugName + "/submesh-";
        if (!entityPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }
        var indexText = entityPath.AsSpan(prefix.Length);
        if (!int.TryParse(indexText, out var index) || (uint)index >= (uint)Primitives.Count)
        {
            return;
        }
        var p = Primitives[index];
        debug.Values.Value("scene", DebugName);
        debug.Values.Value("primitive-index", index);
        debug.Values.Value("batch-index", p.BatchIndex);
        debug.Values.Value("name", p.Name);
        debug.Values.Value("material", p.Material.Name);
        debug.Values.Value("alpha-mode", p.AlphaMode);
        debug.Values.Value("double-sided", p.DoubleSided);
        debug.Values.Value("bounds-min", p.Bounds.Min);
        debug.Values.Value("bounds-max", p.Bounds.Max);
    }

    public static GltfSceneInstance Build(IGraphicsDevice device, GltfModel model, GltfSceneOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        // Memoise the OverrideMaterial result per source ref so each unique
        // GltfMaterial gets one rewritten record reused across every primitive
        // that references it. Without memoisation, two primitives with the
        // same source would each compute (potentially diverging) overrides
        // and the materialCache below would key on the original ref but get
        // mismatched values.
        var overrideCache = options.OverrideMaterial is null
            ? null
            : new Dictionary<GltfMaterial, GltfMaterial>(ReferenceEqualityComparer.Instance);
        var textureCache = new Dictionary<GltfTexture, TextureHandle>(ReferenceEqualityComparer.Instance);
        // Tracks textures currently being uploaded but whose handle hasn't
        // landed yet. Subsequent bindings of the same GltfTexture attach
        // their `apply` callback to the existing upload instead of starting
        // a duplicate one (which would also fail since the CPU mip bytes
        // were released after the first enqueue). The dictionary value is
        // a mutable list because more than two materials can share one
        // texture (e.g. a normal map reused by several wall variants).
        var pendingTextures = new Dictionary<GltfTexture, List<Action<TextureHandle>>>(
            ReferenceEqualityComparer.Instance);
        var materialCache = new Dictionary<GltfMaterial, Material>(ReferenceEqualityComparer.Instance);

        var worldMin = new Vector3(float.PositiveInfinity);
        var worldMax = new Vector3(float.NegativeInfinity);
        var fallbackPipelineWarnings = new HashSet<string>();

        // Phase 1: resolve material + alpha state per glTF primitive.
        // We don't create GL buffers yet — when BatchMergeByMaterial is
        // on, we want to concatenate same-material primitives into one
        // VBO/IBO per group. The original per-primitive path falls out
        // when the merge phase below sees groups of size 1.
        var resolved = new ResolvedPrimitive[model.Primitives.Length];
        for (var i = 0; i < model.Primitives.Length; i++)
        {
            var prim = model.Primitives[i];
            // Apply OverrideMaterial once per unique source. The cache key is
            // the ORIGINAL source ref; subsequent lookups for the same source
            // return the already-rewritten record so all later code paths see
            // a consistent alphaMode + doubleSided + cutoff per primitive.
            var sourceMaterial = prim.Material;
            if (sourceMaterial is not null && overrideCache is not null)
            {
                if (!overrideCache.TryGetValue(sourceMaterial, out var rewritten))
                {
                    rewritten = options.OverrideMaterial!(sourceMaterial);
                    overrideCache[sourceMaterial] = rewritten;
                }
                sourceMaterial = rewritten;
            }

            var alphaMode = sourceMaterial?.AlphaMode ?? GltfAlphaMode.Opaque;
            var doubleSided = sourceMaterial?.DoubleSided ?? false;
            var pipeline = SelectPipeline(options, alphaMode, doubleSided, fallbackPipelineWarnings);

            Material material;
            if (sourceMaterial is { } gm)
            {
                // Cache by ORIGINAL prim.Material reference so primitives that
                // share a source (after override) share one runtime Material.
                var cacheKey = prim.Material!;
                if (!materialCache.TryGetValue(cacheKey, out material!))
                {
                    material = BuildMaterial(device, gm, pipeline, options, textureCache, pendingTextures);
                    options.OnMaterialBuilt?.Invoke(material, gm);
                    materialCache[cacheKey] = material;
                }
            }
            else
            {
                // Unnamed default material -- shared across every prim that
                // imports with a null material reference.
                var defaultKey = new GltfMaterial(
                    Name: $"{options.Prefix}.default",
                    BaseColorFactor: new Vector4(1.0f),
                    BaseColorTexture: null, NormalTexture: null,
                    MetallicRoughnessTexture: null, MetallicFactor: 1.0f, RoughnessFactor: 1.0f,
                    OcclusionTexture: null, OcclusionStrength: 1.0f,
                    EmissiveTexture: null, EmissiveFactor: Vector3.Zero, EmissiveStrength: 1.0f,
                    AlphaMode: GltfAlphaMode.Opaque, AlphaCutoff: 0.5f, DoubleSided: false);
                material = BuildMaterial(device, defaultKey, pipeline, options, textureCache, pendingTextures);
                options.OnMaterialBuilt?.Invoke(material, null);
            }

            resolved[i] = new ResolvedPrimitive(prim, material, sourceMaterial, alphaMode, doubleSided);

            worldMin = Vector3.Min(worldMin, prim.Mesh.Bounds.Min);
            worldMax = Vector3.Max(worldMax, prim.Mesh.Bounds.Max);
        }

        // Phase 2: turn ResolvedPrimitives into SubmeshInstances (the
        // render batches) AND PrimitiveSources (the logical units
        // diagnostics enumerate). Batched mode merges N primitives
        // into 1 SubmeshInstance but still emits N PrimitiveSources
        // — one per original glTF primitive — each pointing at the
        // batch that draws it via BatchIndex.
        SubmeshInstance[] submeshes;
        PrimitiveSource[] primitives;
        if (options.BatchMergeByMaterial)
        {
            (submeshes, primitives) = BuildBatchedSubmeshes(device, resolved, options.Prefix, options.MaxPrimitivesPerBatch);
        }
        else
        {
            (submeshes, primitives) = BuildUnbatchedSubmeshes(device, resolved, options.Prefix);
        }

        var bounds = model.Primitives.Length == 0
            ? Bounds3.Empty
            : new Bounds3(worldMin, worldMax);
        var materialSet = new MaterialSet(materialCache.Values.ToArray());

        // Diagnostic dump: surface per-material classification so we can
        // spot misbehaviour like an opaque wall declared alphaMode=BLEND
        // (renders see-through), or a textured material whose albedo
        // import dropped to null (renders as the default white pixel).
        // Gated behind an env var so the regular Walkthrough demo's
        // console doesn't get spammed.
        if (Environment.GetEnvironmentVariable("BLIX_DUMP_MATERIALS") != null)
        {
            Console.WriteLine($"[GltfSceneInstance:{options.Prefix}] {materialCache.Count} materials, {submeshes.Length} submeshes:");
            Console.WriteLine($"  {"name",-50} {"alpha",-7} {"2-side",-7} {"baseFac.a",-10} {"alb",-3} {"nrm",-3} {"mr",-3} {"ao",-3} {"em",-3}");
            foreach (var (gm, _) in materialCache.OrderBy(kv => kv.Key.Name))
            {
                Console.WriteLine(
                    $"  {Trunc(gm.Name, 50),-50} {gm.AlphaMode,-7} {gm.DoubleSided,-7} " +
                    $"{gm.BaseColorFactor.W,-10:0.000} " +
                    $"{(gm.BaseColorTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.NormalTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.MetallicRoughnessTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.OcclusionTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.EmissiveTexture is null ? "-" : "Y"),-3}");
            }
            var byMode = materialCache.Keys.GroupBy(m => m.AlphaMode)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}");
            Console.WriteLine($"  By alphaMode: {string.Join(", ", byMode)}");
        }

        return new GltfSceneInstance(submeshes, primitives, bounds, materialSet, debugName: $"scene/{options.Prefix}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";

    private static PipelineHandle SelectPipeline(
        GltfSceneOptions options, GltfAlphaMode mode, bool doubleSided,
        HashSet<string> warnedFor)
    {
        PipelineHandle? requested = (mode, doubleSided) switch
        {
            (GltfAlphaMode.Opaque, false) => options.Opaque,
            (GltfAlphaMode.Opaque, true)  => options.OpaqueDoubleSided,
            (GltfAlphaMode.Mask,   false) => options.AlphaMask,
            (GltfAlphaMode.Mask,   true)  => options.AlphaMaskDoubleSided,
            (GltfAlphaMode.Blend,  false) => options.AlphaBlend,
            (GltfAlphaMode.Blend,  true)  => options.AlphaBlendDoubleSided,
            _ => options.Opaque,
        };

        if (requested.HasValue) return requested.Value;

        var key = $"{mode}/{doubleSided}";
        if (warnedFor.Add(key))
        {
            Console.WriteLine(
                $"[GltfSceneInstance] No pipeline provided for {mode}/doubleSided={doubleSided}; " +
                "falling back to Opaque. Visual artefacts likely on the affected primitives.");
        }
        return options.Opaque;
    }

    // Per-glTF-primitive resolved state from Build()'s Phase 1. The
    // Phase 2 batcher reads these to decide grouping + concatenate
    // vertex/index data.
    private readonly record struct ResolvedPrimitive(
        GltfPrimitive Primitive,
        Material Material,
        GltfMaterial? Source,
        GltfAlphaMode AlphaMode,
        bool DoubleSided);

    // Unbatched path — preserves the pre-merge "one VBO/IBO per glTF
    // primitive" layout. Useful as a fallback when investigating cull
    // fidelity or other batch-related issues (flip
    // GltfSceneOptions.BatchMergeByMaterial off to enable).
    //
    // Primitives and Submeshes are 1:1 here — each PrimitiveSource
    // points at its own SubmeshInstance via BatchIndex == its array
    // index. Inspecting a primitive shows the same numbers as
    // inspecting its "batch".
    private static (SubmeshInstance[], PrimitiveSource[]) BuildUnbatchedSubmeshes(
        IGraphicsDevice device, ResolvedPrimitive[] resolved, string prefix)
    {
        var submeshes = new SubmeshInstance[resolved.Length];
        var primitives = new PrimitiveSource[resolved.Length];
        for (var i = 0; i < resolved.Length; i++)
        {
            var rp = resolved[i];
            var meshName = $"{prefix}.mesh.{rp.Primitive.Mesh.Name}";
            var vb = device.CreateVertexBuffer(
                new VertexBufferData(
                    new VertexBufferDescription(rp.Primitive.Mesh.Layout, rp.Primitive.Mesh.VertexCount, GraphicsBufferUsage.Static),
                    rp.Primitive.Mesh.VertexBytes),
                name: $"{meshName}.vb");
            var ib = rp.Primitive.Mesh.IndexFormat == IndexFormat.UInt32
                ? device.CreateIndexBuffer(rp.Primitive.Mesh.Indices32!, name: $"{meshName}.ib")
                : device.CreateIndexBuffer(rp.Primitive.Mesh.Indices, name: $"{meshName}.ib");
            var mesh = new Mesh(meshName, vb, ib, rp.Primitive.Mesh.IndexCount, rp.Primitive.Mesh.Bounds);
            submeshes[i] = new SubmeshInstance(
                Name: rp.Primitive.Mesh.Name,
                Mesh: mesh,
                Material: rp.Material,
                Source: rp.Source,
                WorldBounds: rp.Primitive.Mesh.Bounds,
                AlphaMode: rp.AlphaMode,
                DoubleSided: rp.DoubleSided);
            primitives[i] = new PrimitiveSource(
                Name: rp.Primitive.Mesh.Name,
                Bounds: rp.Primitive.Mesh.Bounds,
                Material: rp.Material,
                Source: rp.Source,
                AlphaMode: rp.AlphaMode,
                DoubleSided: rp.DoubleSided,
                BatchIndex: i);
        }
        return (submeshes, primitives);
    }

    // Batched path — primitives sharing (runtime Material, vertex
    // layout) are concatenated into one VBO/IBO/Mesh. The merged
    // SubmeshInstance carries:
    //   - VertexBytes = concatenation of every source primitive's bytes
    //   - Indices32   = concatenation, rebased by each primitive's
    //                   vertex offset within the merged buffer
    //   - Bounds      = union of every source primitive's bounds
    //
    // Merged indices ALWAYS use UInt32 even when sources are UInt16 —
    // a group's combined vertex count can easily exceed the ushort
    // range (65,535) once you concatenate a dozen wall primitives.
    //
    // Ordering: groups are emitted in order of first-occurrence of
    // each (material, layout) key, so material-set diagnostics still
    // see a stable order. Within a group, primitives stay in source
    // order — preserves index-buffer locality for the GPU.
    private static (SubmeshInstance[], PrimitiveSource[]) BuildBatchedSubmeshes(
        IGraphicsDevice device, ResolvedPrimitive[] resolved, string prefix, int maxPerBatch)
    {
        // Pass 1: group key uses Material reference equality (the runtime
        // Material instance shared via materialCache) and vertex layout
        // value equality. Two primitives with the same runtime Material
        // but different layouts can't merge — their VBOs are
        // structurally different.
        var groupIndices = new Dictionary<(Material, VertexLayout), int>(new GroupKeyComparer());
        var groups = new List<List<int>>();
        for (var i = 0; i < resolved.Length; i++)
        {
            var key = (resolved[i].Material, resolved[i].Primitive.Mesh.Layout);
            if (!groupIndices.TryGetValue(key, out var gi))
            {
                gi = groups.Count;
                groupIndices[key] = gi;
                groups.Add(new List<int>());
            }
            groups[gi].Add(i);
        }

        // Pass 2: spatial sub-batching. Without this, a material applied
        // across the whole scene (e.g. stone walls in Sponza) becomes
        // one batch whose union AABB swallows the atrium and never
        // frustum-culls. Sort each group along its longest spatial
        // axis (so sub-batches are regional clusters, not scattered
        // chunks) and chunk into pieces of maxPerBatch.
        var subBatches = new List<List<int>>();
        foreach (var group in groups)
        {
            if (maxPerBatch <= 0 || group.Count <= maxPerBatch)
            {
                subBatches.Add(group);
                continue;
            }
            SortGroupSpatially(resolved, group);
            for (var chunkStart = 0; chunkStart < group.Count; chunkStart += maxPerBatch)
            {
                var chunkEnd = Math.Min(chunkStart + maxPerBatch, group.Count);
                subBatches.Add(group.GetRange(chunkStart, chunkEnd - chunkStart));
            }
        }

        var submeshes = new SubmeshInstance[subBatches.Count];
        // PrimitiveSources are keyed on original glTF order so the
        // entity path ("submesh-N") stays stable across batching modes.
        // Each primitive records the batch (sub-batch) it landed in
        // via BatchIndex so an inspector can cross-reference.
        var primitives = new PrimitiveSource[resolved.Length];
        for (var g = 0; g < subBatches.Count; g++)
        {
            submeshes[g] = BuildOneBatch(device, resolved, subBatches[g], prefix, g);
            foreach (var memberIdx in subBatches[g])
            {
                var rp = resolved[memberIdx];
                primitives[memberIdx] = new PrimitiveSource(
                    Name: rp.Primitive.Mesh.Name,
                    Bounds: rp.Primitive.Mesh.Bounds,
                    Material: rp.Material,
                    Source: rp.Source,
                    AlphaMode: rp.AlphaMode,
                    DoubleSided: rp.DoubleSided,
                    BatchIndex: g);
            }
        }
        return (submeshes, primitives);
    }

    // Sort a group of primitive indices by centroid along the longest
    // axis of the group's bounding box. Picking the longest axis
    // dominates the result: a group that spans 50m in X and 2m in Y
    // gets split into X-coherent ribbons, which is what frustum cull
    // needs to actually reject sub-batches the camera isn't looking
    // at. Cost: one O(N) bounds sweep + one O(N log N) sort per group,
    // both at load time only.
    private static void SortGroupSpatially(ResolvedPrimitive[] resolved, List<int> group)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var idx in group)
        {
            var b = resolved[idx].Primitive.Mesh.Bounds;
            min = Vector3.Min(min, b.Min);
            max = Vector3.Max(max, b.Max);
        }
        var extents = max - min;
        // Prefer X on ties because Sponza's long axis happens to be X;
        // not load-bearing for correctness, just a small bias for the
        // representative scene.
        int axis = (extents.X >= extents.Y && extents.X >= extents.Z) ? 0
                 : (extents.Y >= extents.Z)                            ? 1
                 :                                                       2;
        group.Sort((a, b) =>
        {
            var ca = AxisCentroid(resolved[a].Primitive.Mesh.Bounds, axis);
            var cb = AxisCentroid(resolved[b].Primitive.Mesh.Bounds, axis);
            return ca.CompareTo(cb);
        });
    }

    private static float AxisCentroid(Bounds3 b, int axis) => axis switch
    {
        0 => (b.Min.X + b.Max.X) * 0.5f,
        1 => (b.Min.Y + b.Max.Y) * 0.5f,
        _ => (b.Min.Z + b.Max.Z) * 0.5f,
    };

    private static SubmeshInstance BuildOneBatch(
        IGraphicsDevice device, ResolvedPrimitive[] resolved, List<int> memberIndices,
        string prefix, int batchId)
    {
        var first = resolved[memberIndices[0]];
        var layout = first.Primitive.Mesh.Layout;
        var stride = layout.Stride;

        // Total vertex + index counts up front so we allocate once.
        int totalVerts = 0, totalIndices = 0;
        for (var k = 0; k < memberIndices.Count; k++)
        {
            var m = resolved[memberIndices[k]];
            totalVerts += m.Primitive.Mesh.VertexCount;
            totalIndices += m.Primitive.Mesh.IndexCount;
        }

        // Concatenate vertex bytes. Each source primitive's bytes are
        // a contiguous block at the right offset; no per-vertex
        // rewriting needed because the layout is identical.
        var vertexBytes = new byte[totalVerts * stride];
        // Concatenate indices, rebased by the running vertex count so
        // each source primitive's triangles still address its own
        // vertices in the merged buffer.
        var indices32 = new uint[totalIndices];
        var minSum = new Vector3(float.PositiveInfinity);
        var maxSum = new Vector3(float.NegativeInfinity);
        int vByteOffset = 0;
        int iWriteOffset = 0;
        uint vBase = 0;
        foreach (var memberIdx in memberIndices)
        {
            var m = resolved[memberIdx];
            var mesh = m.Primitive.Mesh;

            Buffer.BlockCopy(mesh.VertexBytes, 0, vertexBytes, vByteOffset, mesh.VertexBytes.Length);
            vByteOffset += mesh.VertexBytes.Length;

            if (mesh.IndexFormat == IndexFormat.UInt32)
            {
                var src = mesh.Indices32!;
                for (var n = 0; n < src.Length; n++) indices32[iWriteOffset + n] = src[n] + vBase;
                iWriteOffset += src.Length;
            }
            else
            {
                var src = mesh.Indices;
                for (var n = 0; n < src.Length; n++) indices32[iWriteOffset + n] = (uint)src[n] + vBase;
                iWriteOffset += src.Length;
            }
            vBase += (uint)mesh.VertexCount;

            // Union bounds. Primitives in Sponza are world-space-baked,
            // so primitive bounds == world bounds — union is just per-
            // axis min/max.
            minSum = Vector3.Min(minSum, mesh.Bounds.Min);
            maxSum = Vector3.Max(maxSum, mesh.Bounds.Max);
        }

        var meshName = $"{prefix}.batch.{batchId}.{first.Material.Name}";
        var vb = device.CreateVertexBuffer(
            new VertexBufferData(
                new VertexBufferDescription(layout, totalVerts, GraphicsBufferUsage.Static),
                vertexBytes),
            name: $"{meshName}.vb");
        var ib = device.CreateIndexBuffer(indices32, name: $"{meshName}.ib");
        var mergedBounds = new Bounds3(minSum, maxSum);
        var mesh2 = new Mesh(meshName, vb, ib, totalIndices, mergedBounds);

        return new SubmeshInstance(
            Name: $"merged.{first.Material.Name}.{memberIndices.Count}-prim",
            Mesh: mesh2,
            Material: first.Material,
            // All members of the group share the same runtime Material
            // and therefore the same overridden GltfMaterial source —
            // pick any. Convention: the first.
            Source: first.Source,
            WorldBounds: mergedBounds,
            AlphaMode: first.AlphaMode,
            DoubleSided: first.DoubleSided);
    }

    // Equality comparer for the (Material, VertexLayout) group key.
    // Material is matched by reference (the cache returns the same
    // instance for shared sources); VertexLayout is a record whose
    // default equality compares Stride + Attributes structurally.
    private sealed class GroupKeyComparer : IEqualityComparer<(Material, VertexLayout)>
    {
        public bool Equals((Material, VertexLayout) x, (Material, VertexLayout) y)
            => ReferenceEquals(x.Item1, y.Item1) && x.Item2.Equals(y.Item2);

        public int GetHashCode((Material, VertexLayout) obj)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1), obj.Item2);
    }

    private static Material BuildMaterial(
        IGraphicsDevice device, GltfMaterial gm, PipelineHandle pipeline,
        GltfSceneOptions options, Dictionary<GltfTexture, TextureHandle> textureCache,
        Dictionary<GltfTexture, List<Action<TextureHandle>>> pendingTextures)
    {
        var material = new Material($"{options.Prefix}.mat.{gm.Name}", pipeline);
        material.SetUniform("uBaseColorFactor", new Vector4Uniform(gm.BaseColorFactor));
        material.SetUniform("uMetallicFactor", new FloatUniform(gm.MetallicFactor));
        material.SetUniform("uRoughnessFactor", new FloatUniform(gm.RoughnessFactor));
        // Multiply factor by strength so the lit shader only needs one uniform.
        // KHR_materials_emissive_strength defaults to 1.0 when the extension
        // isn't present, so this stays correct for vanilla glTF.
        material.SetUniform("uEmissiveFactor", new Vector3Uniform(gm.EmissiveFactor * gm.EmissiveStrength));
        material.SetUniform("uOcclusionStrength", new FloatUniform(gm.OcclusionStrength));
        // Effective cutoff: glTF spec says alphaCutoff is only used when
        // alphaMode == MASK. Force 0 for OPAQUE + BLEND so the lit shader's
        // `if (uAlphaCutoff > 0 && a < cutoff) discard;` never fires --
        // OPAQUE materials with low-alpha textures (Modern Sponza authors
        // some that way) would otherwise discard every fragment and the
        // scene renders empty.
        var effectiveCutoff = gm.AlphaMode == GltfAlphaMode.Mask ? gm.AlphaCutoff : 0.0f;
        material.SetUniform("uAlphaCutoff", new FloatUniform(effectiveCutoff));
        // Existing lit shader uses uNormalScale as a "has normal map" gate.
        material.SetUniform("uNormalScale", new FloatUniform(gm.NormalTexture is null ? 0.0f : 1.0f));
        material.SetUniform("uHasMetallicMap", new FloatUniform(gm.MetallicRoughnessTexture is null ? 0.0f : 1.0f));
        material.SetUniform("uHasOcclusionMap", new FloatUniform(gm.OcclusionTexture is null ? 0.0f : 1.0f));
        // Lit shader uses this to negate the geometric normal on back-faces
        // so doubleSided foliage (leaves, fabric) lights correctly from both
        // sides. The derivative-based cotangentFrame naturally inherits the
        // flip via its cross(dp, N) terms, so no separate TBN handedness
        // adjustment is needed.
        material.SetUniform("uDoubleSided", new FloatUniform(gm.DoubleSided ? 1.0f : 0.0f));

        // Bind defaults FIRST. When an uploader is set, the real textures
        // pop in over the next few frames as Drain runs (see ResourceUploader);
        // until then the lit shader samples the default which keeps the
        // material valid for rendering. When no uploader, uploads happen
        // synchronously here and the final SetTexture wins immediately.
        material.SetTexture("uAlbedo", options.Defaults.WhitePixel, 0);
        material.SetTexture("uNormalMap", options.Defaults.FlatNormal, 1);
        material.SetTexture("uMetallicRoughness", options.Defaults.NeutralMetallicRoughness, 2);
        material.SetTexture("uEmissive", options.Defaults.WhitePixel, 3);
        material.SetTexture("uOcclusion", options.Defaults.FullOcclusion, 15);

        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.BaseColorTexture, "albedo",
            real => material.SetTexture("uAlbedo", real, 0));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.NormalTexture, "normal",
            real => material.SetTexture("uNormalMap", real, 1));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.MetallicRoughnessTexture, "mr",
            real => material.SetTexture("uMetallicRoughness", real, 2));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.EmissiveTexture, "emissive",
            real => material.SetTexture("uEmissive", real, 3));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.OcclusionTexture, "ao",
            real => material.SetTexture("uOcclusion", real, 15));

        return material;
    }

    // Per-slot binder. If the source is null, leaves the default placeholder
    // alone (already bound by the caller). If the source has been uploaded
    // before, binds the cached handle immediately. Otherwise either uploads
    // synchronously (no uploader provided) or enqueues for deferred upload
    // (uploader present), invoking apply with the real handle when ready.
    private static void BindMaterialTexture(
        IGraphicsDevice device, GltfSceneOptions options,
        Dictionary<GltfTexture, TextureHandle> textureCache,
        Dictionary<GltfTexture, List<Action<TextureHandle>>> pendingTextures,
        GltfTexture? source, string slotHint,
        Action<TextureHandle> apply)
    {
        if (source is null) return;
        if (textureCache.TryGetValue(source, out var cached))
        {
            apply(cached);
            return;
        }
        // An upload is already in flight for this source -- piggyback on
        // it instead of starting another. Happens when the same image
        // backs more than one glTF material (e.g. a normal map reused
        // across wall variants). Without this, the second BuildMaterial
        // call would crash on the released CPU bytes.
        if (pendingTextures.TryGetValue(source, out var pendingList))
        {
            pendingList.Add(apply);
            return;
        }

        // sRGB-aware format selection. glTF spec: BaseColor + Emissive are
        // sRGB-encoded; Normal + MetallicRoughness + Occlusion are LINEAR.
        // We promote Rgba8 -> Rgba8Srgb for the sRGB slots so GL does sRGB->
        // linear conversion BEFORE filtering (correct), instead of the shader
        // pow(c, 2.2)'ing the already-filtered gamma-space average (wrong on
        // high-contrast textures, producing too-dark mip transitions).
        // Compressed BC7 textures already encode the sRGB flag in their own
        // format enum (Bc7Srgb vs Bc7Unorm) so we pass those through.
        var isSrgbSlot = slotHint == "albedo" || slotHint == "emissive";
        var uploadFormat = source.Format == TextureFormat.Rgba8 && isSrgbSlot
            ? TextureFormat.Rgba8Srgb
            : source.Format;

        if (options.Uploader is { } uploader)
        {
            var subscribers = new List<Action<TextureHandle>> { apply };
            pendingTextures[source] = subscribers;
            var name = $"{options.Prefix}.{slotHint}.{source.Name}";
            void OnUploaded(TextureHandle h)
            {
                textureCache[source] = h;
                pendingTextures.Remove(source);
                foreach (var sub in subscribers) sub(h);
            }

            if (source.LazyHandle is { } lazy)
            {
                // Lazy path: bytes stay on disk until the uploader's pump
                // hits each mip. Captures `lazy` so the mip reads happen
                // at process-time, not enqueue-time.
                // One file open per texture (not per mip): the uploader pumps
                // mips descending to 0, and CreateBufferedMipReader seeks within
                // a single open handle, closing it after the finest mip. Avoids
                // MipCount opens per texture — costly on high-open-latency
                // volumes like an external SSD.
                uploader.EnqueueLazy(
                    uploadFormat, source.Width, source.Height, source.MipCount,
                    mipReader: BlixTexReader.CreateBufferedMipReader(lazy),
                    options.Sampler, name, OnUploaded);
                return;
            }

            // Eager path: source.MipBytes is populated (PNG-decode case).
            // After enqueue we drop OUR reference; the uploader's work
            // items hold theirs only until each mip is processed.
            var mipBytes = source.MipBytes
                ?? throw new InvalidOperationException(
                    $"GltfTexture '{source.Name}' has neither lazy handle nor CPU mip bytes.");
            uploader.Enqueue(
                uploadFormat, source.Width, source.Height, mipBytes, options.Sampler,
                name, OnUploaded);
            source.ReleaseCpuMipBytes();
            return;
        }

        // Synchronous path (no uploader). Lazy textures need to materialise
        // bytes here too.
        IReadOnlyList<byte[]> bytesForSync;
        if (source.LazyHandle is { } syncLazy)
        {
            // One file open for the whole chain, not one per mip.
            bytesForSync = BlixTexReader.ReadAllMips(syncLazy);
        }
        else
        {
            bytesForSync = source.MipBytes
                ?? throw new InvalidOperationException(
                    $"GltfTexture '{source.Name}' has neither lazy handle nor CPU mip bytes.");
        }
        var handle = device.CreateTexture2DMipped(
            new TextureDescription(source.Width, source.Height, uploadFormat, options.Sampler),
            bytesForSync,
            name: $"{options.Prefix}.{slotHint}.{source.Name}");
        textureCache[source] = handle;
        source.ReleaseCpuMipBytes();
        apply(handle);
    }
}

// One uploaded primitive paired with the runtime Material it draws with and
// the GltfMaterial it was built from (kept for inspection + overrides).
//
// When BatchMergeByMaterial is on, ONE SubmeshInstance can carry the
// merged geometry of many source glTF primitives — Name then becomes
// "merged.<material>.<count>-prim", WorldBounds the union, and the
// per-primitive identities live in PrimitiveSource[] instead.
public sealed record SubmeshInstance(
    string Name,
    Mesh Mesh,
    Material Material,
    GltfMaterial? Source,
    Bounds3 WorldBounds,
    GltfAlphaMode AlphaMode,
    bool DoubleSided);

// The LOGICAL unit of a glTF scene — one per source primitive,
// regardless of how the renderer batches them. Diagnostics enumerate
// these (selection, inspection, debug-draw bounds) so picking a chair
// selects that chair, not "all chairs sharing this material". The
// renderer doesn't see these; cross-reference the physical batch that
// actually draws this primitive via BatchIndex into
// GltfSceneInstance.Submeshes.
//
// Bounds is the primitive's TIGHT world-space AABB (Sponza primitives
// have their node transform baked in, so primitive bounds = world
// bounds directly). Material is a reference to the same runtime
// Material the containing batch uses.
public sealed record PrimitiveSource(
    string Name,
    Bounds3 Bounds,
    Material Material,
    GltfMaterial? Source,
    GltfAlphaMode AlphaMode,
    bool DoubleSided,
    int BatchIndex);

// Flat set of unique runtime materials a scene built. Demos use `Find` to
// look up by name when applying targeted overrides (Sponza's marble floor,
// for example).
public sealed class MaterialSet
{
    private readonly Material[] all;
    private readonly Dictionary<string, Material> byName;

    public MaterialSet(IEnumerable<Material> materials)
    {
        all = materials.ToArray();
        byName = all.ToDictionary(m => m.Name, m => m);
    }

    public IReadOnlyList<Material> All => all;

    public Material? Find(string name) => byName.TryGetValue(name, out var m) ? m : null;
}

// Pipelines + defaults + customisation hook fed into GltfSceneInstance.Build.
// Designed so a minimal demo only has to populate `Opaque` + `Defaults` +
// `Sampler`; richer demos opt into MASK / BLEND / DOUBLE_SIDED pipelines
// only for the variants their scene actually uses.
public sealed record GltfSceneOptions
{
    public required PipelineHandle Opaque { get; init; }
    public PipelineHandle? OpaqueDoubleSided { get; init; }
    public PipelineHandle? AlphaMask { get; init; }
    public PipelineHandle? AlphaMaskDoubleSided { get; init; }
    public PipelineHandle? AlphaBlend { get; init; }
    public PipelineHandle? AlphaBlendDoubleSided { get; init; }

    public required GltfDefaultTextures Defaults { get; init; }
    public required SamplerDescription Sampler { get; init; }

    // Demo hook: invoked once per built Material with the source GltfMaterial
    // (null for primitives with no material reference). Bind any scene-wide
    // textures (shadow maps, IBL probes, BRDF LUT, env cube) here.
    public Action<Material, GltfMaterial?>? OnMaterialBuilt { get; init; }

    // Demo hook: rewrite a source GltfMaterial BEFORE pipeline selection and
    // BuildMaterial. Use this to compensate for upstream authoring quirks like
    // foliage tagged alphaMode=BLEND that the runtime should treat as MASK
    // (binary alpha + depth-write so leaves z-sort against themselves without
    // a back-to-front pass). Returns the (possibly rewritten) material. Called
    // at most once per UNIQUE source GltfMaterial -- if multiple primitives
    // reference the same source, the same overridden record is reused so
    // materialCache keying stays stable. Return the input unchanged to opt out.
    public Func<GltfMaterial, GltfMaterial>? OverrideMaterial { get; init; }

    // When set, per-material texture uploads are queued through the uploader
    // instead of running synchronously inside Build. Materials get the
    // default placeholder texture for each slot up front; the uploader
    // swaps in the real texture over the next few frames as Drain processes
    // its queue. Build still returns synchronously with a fully-constructed
    // scene graph -- only the GL upload work is deferred, not the structural
    // work. When null, all uploads happen inline (legacy behaviour).
    public ResourceUploader? Uploader { get; init; }

    public string Prefix { get; init; } = "gltf";

    // When true (default), primitives sharing the same runtime Material
    // + vertex layout are concatenated into a single merged Mesh at
    // Build time — one VBO + IBO per material group instead of one per
    // glTF primitive. On Sponza main (~200 primitives, ~30 unique
    // materials) this cuts opaque-pass draws ~6-7x and cascade-pass
    // draws ~6-7x too. The win is per-draw overhead × (3 cascade
    // passes + opaque) — the dominant cost on macOS GL.
    //
    // Tradeoff: each merged Mesh has the UNION of its source primitives'
    // bounds (potentially much larger), so per-mesh frustum culling
    // rejects fewer of them. MaxPrimitivesPerBatch (below) splits
    // large material groups into spatially-coherent sub-batches to
    // reclaim cull effectiveness. Set BatchMergeByMaterial = false
    // to fall back to the original per-primitive layout entirely.
    public bool BatchMergeByMaterial { get; init; } = true;

    // Maximum number of glTF primitives concatenated into a single
    // merged SubmeshInstance. Without a cap, a widely-used material
    // (e.g. a stone wall material applied to 50 sections across the
    // atrium) becomes ONE batch whose union AABB encompasses the
    // entire scene — frustum cull always passes, every triangle goes
    // through the vertex shader even when only one wall is visible.
    //
    // With a cap of N, each material group is sorted along its
    // longest spatial axis (so sub-batches are coherent regional
    // clusters, not scattered chunks) and broken into pieces of N.
    // Smaller N -> more draws but tighter per-batch bounds (better
    // frustum cull). 0 disables the cap entirely (one batch per
    // material group, original v1 behaviour).
    //
    // 16 is the empirical sweet spot on Sponza: cuts visible
    // triangle counts ~5x when looking at one room while only
    // doubling batch count vs uncapped.
    public int MaxPrimitivesPerBatch { get; init; } = 16;
}

// 1x1 default textures the lit pipeline falls back to when a material doesn't
// author a particular slot. Demos build these once and share across scenes.
public sealed record GltfDefaultTextures(
    // White (255,255,255,255). Used for missing albedo (factor alone drives)
    // AND missing emissive (factor alone drives -- glTF spec: absent texture
    // implies all components = 1.0 so the factor isn't zeroed out).
    TextureHandle WhitePixel,
    // (128,128,255,255). Tangent-space "up" -- decoded as (0,0,1) in shader.
    TextureHandle FlatNormal,
    // (255,255,255,255). Default MR sample is 1.0 in every channel so the
    // factor uniforms control the BRDF on their own; the shader's gate
    // uniform (uHasMetallicMap) is what actually disables sampling.
    TextureHandle NeutralMetallicRoughness,
    // (255,255,255,255). AO = 1.0 (no occlusion). Same gate convention.
    TextureHandle FullOcclusion);
