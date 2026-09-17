using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;
using Blix.Cooked;

namespace Blix;

// Static-mesh sibling of GltfImporter. Loads any .glb/.gltf containing untransformed
// or transformed mesh nodes (no skinning required) and emits a GltfModel whose
// Primitives use the VertexPosition3NormalTexture layout. Skeleton/Animations come
// back empty so existing renderer code can branch on Skeleton.Bones.Length == 0.
//
// Each mesh node's world-space transform is baked into the vertex positions at
// import time so the renderer can draw every primitive with a shared identity
// model matrix. That's the right call for static scene assets like Sponza where
// instancing isn't a goal and the alternative (per-primitive uModel) would force
// the demo to track a transform alongside each Mesh handle.
//
// Materials reuse the GltfMaterial record produced by the rigged importer; the
// extraction logic is duplicated rather than shared because hoisting it would
// pull GltfImporter's private internals into a third file. ~30 lines of dupe is
// the lighter cost.
public sealed class GltfStaticImporter : IAssetImporter<GltfModel>
{
    public string Name => "static-mesh.gltf";

    public GltfModel Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new AssetImportException(context.SourcePath, null, "no file there.");
        }
        // <b>The refusal covers the IMPORT, not only the parse.</b> Wrapping ModelRoot.Load alone
        // catches what SharpGLTF rejects and nothing this importer rejects itself — so a file the
        // parser accepts and glTF's own rules do not, like a primitive with no POSITION, escaped as
        // an unhandled exception and took the process down with a stack trace. The generator's
        // Mesh_NoPosition is exactly that asset: valid glTF JSON, an invalid mesh. A reader whose
        // conformance checks abort instead of refusing has no usable negative behaviour to assert.
        return AssetImportException.Refusing(context.SourcePath, () => ImportCore(context));
    }

    /// <summary>Loads a cooked mesh and nothing else — no glTF is opened, and none need exist.</summary>
    /// <remarks>
    /// <b>The payoff of the image table.</b> Geometry and materials came from the cooked file
    /// already; what was missing was where each material's pixels live, and that number used to be
    /// a glTF logical image index — resolvable only by reopening the source. With the table it is a
    /// row naming a relative resource, so the source can be deleted and the asset still loads.
    /// <para>
    /// <b>The texture cache is keyed by ROW here</b>, and <c>MaterialFromCooked</c> looks textures
    /// up by the same number, so neither side needs to know a glTF index ever existed.
    /// </para>
    /// </remarks>
    private GltfModel ImportCooked(string requestedPath, string blixmeshPath)
    {
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var cooked = BlixMeshReader.Read(blixmeshPath);
        var cookedDir = Path.GetDirectoryName(Path.GetFullPath(blixmeshPath)) ?? string.Empty;

        var textureCache = new Dictionary<int, GltfTexture>();
        var materialCache = new Dictionary<int, GltfMaterial>();
        GltfShared.LoadImagesFromTable(cooked.ImageTable, cooked.MaterialTable, cookedDir, textureCache);

        // The texture coordinate is the only Float2 in either cooked layout, so the UV offset is
        // matched by FORMAT rather than by attribute location — location 3 is the texture
        // coordinate in the 48-byte tangent layout and the tangent in nothing, and looking it up
        // that way meant a cooked mesh without tangents could not be loaded at all.
        var uvAttr = cooked.Layout.Attributes.First(a => a.Format == VertexAttributeFormat.Float2);

        var primitives = new List<GltfPrimitive>(cooked.Primitives.Count);
        foreach (var p in cooked.Primitives)
        {
            // Heal degenerate UVs in cooked files too — in-place is fine, the buffer is ours once
            // BlixMeshReader returns it. Lets asset-level UV corruption be fixed without re-cooking.
            SanitizePackedUVs(
                p.VertexBytes, p.VertexCount, stride: cooked.Layout.Stride,
                uvOffset: uvAttr.Offset, p.Name);

            var lod0 = p.Lods[0];
            var lods = new MeshLod[p.Lods.Count];
            for (var l = 0; l < p.Lods.Count; l++)
            {
                lods[l] = new MeshLod(p.Lods[l].Indices16, p.Lods[l].Indices32, p.Lods[l].Error);
            }

            primitives.Add(new GltfPrimitive(
                new MeshData(
                    p.Name, p.VertexBytes, lod0.Indices16 ?? Array.Empty<ushort>(),
                    cooked.Layout, p.Bounds, Indices32: lod0.Indices32, Lods: lods),
                GltfShared.MaterialFromCooked(
                    cooked.MaterialTable, p.MaterialIndex, materialCache, textureCache)));
        }

        if (AssetLoadLog.Enabled)
        {
            // <b>Keyed by what the CALLER asked for, not by the file that answered.</b> A report
            // exists to say what one requested load did; keying it by the artifact that happened to
            // satisfy it makes an asset's history unsearchable by its own name — and made every
            // instrument that looks a load up by the path it passed in go blind at once.
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: requestedPath,
                CookedPath: blixmeshPath,
                Mode: AssetLoadMode.Cooked,
                Bytes: SafeLength(blixmeshPath),
                LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Recipe: cooked.Cooked?.Stamp.Recipe));
        }

        return new GltfModel(
            primitives.ToArray(),
            new Skeleton(Array.Empty<Bone>()),
            Array.Empty<AnimationClip>(),
            Matrix4x4.Identity);
    }

    private GltfModel ImportCore(AssetImportContext context)
    {
        // <b>A cooked .blixmesh is opened on its own, with no glTF anywhere.</b> As of v6 it names
        // everything a load needs — geometry, materials, and where every image's pixels are — so the
        // source is not consulted, not parsed, and need not exist. That is the difference between a
        // cook that is an optimisation and one that produces a shippable artifact.
        //
        // <b>This replaced a "lite model" read</b> that opened the glTF with validation off and a
        // resource callback returning empty bytes for every non-container file, to get material and
        // image metadata out of the JSON without paying for buffers (4500 ms -> 11 ms on Sponza).
        // It was a good trick and it is now unnecessary: the metadata it went there for is in the
        // cooked file. Deleted rather than kept beside the new path, because two ways to load the
        // same asset is how the two drift.
        //
        // A .blixmesh may also be named DIRECTLY, which is what makes "open a cooked asset" a
        // coherent request for the first time.
        var direct = Path.GetExtension(context.SourcePath)
            .Equals(".blixmesh", StringComparison.OrdinalIgnoreCase);
        var blixmeshPath = direct
            ? context.SourcePath
            : Path.ChangeExtension(context.SourcePath, ".blixmesh");
        if (File.Exists(blixmeshPath)) return ImportCooked(context.SourcePath, blixmeshPath);

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var model = AssetImportException.Refusing(
            context.SourcePath, () => ModelRoot.Load(context.SourcePath));

        var textureCache = new Dictionary<int, GltfTexture>();
        var materialCache = new Dictionary<int, GltfMaterial>();
        var primitives = new List<GltfPrimitive>();

        // Parallel texture decode. PNG/JPEG decode via StbImageSharp is the
        // dominant cost for heavy assets (Modern Sponza spends most of its
        // multi-minute load here), and StbImageSharp doesn't share state
        // across calls, so we can decode every unique source image in
        // parallel and pre-populate the textureCache. Cooked .blixtex
        // siblings (see tools/Blix.Tools.Cook + BlixTex format) skip the
        // decode entirely; PreDecodeImages prefers them when present.
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) ?? string.Empty;
        GltfShared.PreDecodeImages(model, textureCache, gltfDir);

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null) continue;
            // F-016: engine is now row-vector form throughout — same as SharpGLTF.
            // No transpose needed; just use the world matrix directly.
            var world = node.WorldMatrix;
            var normalMatrix = ComputeNormalMatrix(world);

            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                var meshData = BuildStaticMeshData(meshName, prim, world, normalMatrix, context.FlipTextureV, context.IncludeTangents, context.IncludeColour);
                var material = GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache);
                primitives.Add(new GltfPrimitive(meshData, material));
            }
        }

        if (primitives.Count == 0)
        {
            throw new InvalidOperationException(
                $"glTF '{context.SourcePath}' contains no mesh nodes.");
        }

        // Empty skeleton + zero animations. Identity meshNodeTransform — every
        // vertex has already had its node transform baked in.
        // Reported before returning, so the cost covers the whole load rather than one phase of it.
        if (AssetLoadLog.Enabled)
        {
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: context.SourcePath,
                CookedPath: null,
                Mode: AssetLoadMode.Source,
                Bytes: SafeLength(context.SourcePath),
                LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Warning: "no .blixmesh sibling — glTF accessors were walked"));
        }

        return new GltfModel(
            primitives.ToArray(),
            new Skeleton(Array.Empty<Bone>()),
            Array.Empty<AnimationClip>(),
            Matrix4x4.Identity);
    }

    // Node-hierarchy-preserving import (see GltfNodeModel). Unlike Import, this does
    // NOT bake world transforms: each node's mesh comes back in its own LOCAL space,
    // alongside the node's name, local transform, and parent index — so a consumer
    // can map named nodes onto its own articulated rig (hull/turret/barrel) and drive
    // them. Loads the whole buffer (no cooked-mesh fast path — articulated props are
    // small) so accessor reads succeed.
    public GltfNodeModel ImportNodes(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new AssetImportException(context.SourcePath, null, "no file there.");
        }
        // <b>The refusal covers the IMPORT, not only the parse.</b> Wrapping ModelRoot.Load alone
        // catches what SharpGLTF rejects and nothing this importer rejects itself — so a file the
        // parser accepts and glTF's own rules do not, like a primitive with no POSITION, escaped as
        // an unhandled exception and took the process down with a stack trace. The generator's
        // Mesh_NoPosition is exactly that asset: valid glTF JSON, an invalid mesh. A reader whose
        // conformance checks abort instead of refusing has no usable negative behaviour to assert.
        return AssetImportException.Refusing(context.SourcePath, () => ImportNodesCore(context));
    }

    private GltfNodeModel ImportNodesCore(AssetImportContext context)
    {
        var model = AssetImportException.Refusing(context.SourcePath, () => ModelRoot.Load(context.SourcePath));
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) ?? string.Empty;
        var textureCache = new Dictionary<int, GltfTexture>();
        var materialCache = new Dictionary<int, GltfMaterial>();
        GltfShared.PreDecodeImages(model, textureCache, gltfDir);

        var glNodes = model.LogicalNodes.ToList();
        var indexOf = new Dictionary<Node, int>(glNodes.Count);
        for (var i = 0; i < glNodes.Count; i++) indexOf[glNodes[i]] = i;

        var nodes = new GltfNode[glNodes.Count];
        for (var i = 0; i < glNodes.Count; i++)
        {
            var node = glNodes[i];
            var parent = node.VisualParent is { } vp && indexOf.TryGetValue(vp, out var pi) ? pi : -1;

            var prims = Array.Empty<GltfPrimitive>();
            if (node.Mesh is { } mesh)
            {
                prims = new GltfPrimitive[mesh.Primitives.Count];
                for (var j = 0; j < mesh.Primitives.Count; j++)
                {
                    var prim = mesh.Primitives[j];
                    var name = $"{node.Name ?? mesh.Name ?? "node"}.{j}";
                    // Identity world + normal matrix → vertices stay in node-local space.
                    var meshData = BuildStaticMeshData(name, prim, Matrix4x4.Identity, Matrix4x4.Identity, context.FlipTextureV, context.IncludeTangents, context.IncludeColour);
                    prims[j] = new GltfPrimitive(meshData, GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache));
                }
            }

            nodes[i] = new GltfNode(node.Name ?? $"node{i}", parent, node.LocalMatrix, prims);
        }

        return new GltfNodeModel(nodes, GltfShared.CollectIgnored(model));
    }

    /// <param name="includeColour">
    /// Read <c>COLOR_0</c> into a 36-byte vertex. <b>Opt-in, because the layout is the contract with
    /// a pipeline that was already created.</b> Vulkan reads vertices at the stride the PIPELINE
    /// declares, so widening every static import would make six applications walk 36-byte vertices
    /// with a 32-byte stride — not a crash, not a compile error, just geometry that comes out wrong.
    /// The caller that opts in is the caller that built a pipeline to match.
    /// </param>
    public static MeshData BuildStaticMeshData(string name, MeshPrimitive primitive, Matrix4x4 world, Matrix4x4 normalMatrix, bool flipTextureV = false, bool includeTangents = false, bool includeColour = false)
    {
        if (includeTangents && includeColour)
        {
            // Refused rather than silently dropping one. No vertex type in the tree carries both,
            // because nothing has ever wanted both: tangents are Sponza's normal-mapped interiors
            // and COLOR_0 is the nature kit's baked occlusion, and no asset in this tree ships the
            // pair. The day one does, this throw is where to add the fourth layout — which is a
            // better thing to find than a mesh that quietly lost its ambient occlusion.
            throw new NotSupportedException(
                $"'{name}': tangents and vertex colour cannot be imported together — no vertex layout carries both.");
        }

        // ── What glTF 2.0 defines, and what this reads ──────────────────────────────────────
        // Spec §3.7.2.1 "Meshes / Overview", the mesh.primitive attribute table:
        //   https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#meshes-overview
        //
        // The set is closed and small — POSITION, NORMAL, TANGENT, TEXCOORD_n, COLOR_n, JOINTS_n,
        // WEIGHTS_n, plus application-specific semantics which must be underscore-prefixed. Every
        // multi-set semantic is read at INDEX 0 ONLY and the rest are dropped without a word:
        //
        //   TEXCOORD_1+  a second UV set — lightmaps, detail layers. Not read.
        //   COLOR_1+     a second colour set. Not read.
        //   JOINTS_1+ /  MORE THAN FOUR BONE INFLUENCES PER VERTEX. Not read, which means such a
        //   WEIGHTS_1+   skin is silently TRUNCATED to its first four and the remaining weights
        //                are lost — the one gap here with a wrong-looking result rather than a
        //                missing feature.
        //
        // <b>Written down because the alternative was finding it by grepping assets.</b> COLOR_0
        // was discovered that way — by noticing 49 primitives carried a channel nothing read — and
        // that method only ever finds what happens to be in the content already. The table above
        // is the complete surface, and it holds whether or not this tree owns an asset that uses it.
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        // Authored tangents (glTF vec4: xyz dir + w handedness). MikkTSpace-
        // compatible per spec, so forwarding beats recomputing.
        var tangents = includeTangents ? primitive.GetVertexAccessor("TANGENT")?.AsVector4Array() : null;
        // AsColorArray, not AsVector4Array: COLOR_0 is legally float, ushort-normalised or
        // byte-normalised, and vec3 as well as vec4. This accessor collapses all six spellings to
        // 0..1 RGBA with alpha defaulted to opaque, which is the only reading correct for every one.
        //
        // <b>Verified rather than assumed.</b> glTF-Asset-Generator's Mesh_PrimitiveVertexColor is
        // exactly those six permutations, one per file. All six render BYTE-IDENTICALLY through this
        // path — and the control that makes that mean something: the render carries 415,625 strongly
        // coloured pixels, so six identical WHITE images (which would also agree) cannot pass it.
        // The vec3 cases are the ones worth the trip: their alpha has to arrive as 1.0, and a 0 would
        // be invisible here and fatal the moment anything multiplied by it.
        var colours = includeColour ? primitive.GetVertexAccessor("COLOR_0")?.AsColorArray() : null;

        // <b>Read alongside the colour, on the same flag.</b> Both widen the same studio layout and
        // both are opt-in for the same reason: a consumer that pinned a narrower stride reads
        // garbage otherwise. Splitting them into two flags would let a caller ask for a layout that
        // no vertex type in this tree declares.
        var uv1 = includeColour ? primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array() : null;

        var vertexCount = positions.Count;

        var minB = new Vector3(float.PositiveInfinity);
        var maxB = new Vector3(float.NegativeInfinity);

        // V canonicalisation (V -> 1-V), opt-in per import via FlipTextureV.
        // Bottom-up (OpenGL-authored) sources sample vertically inverted on a
        // top-down (Vulkan / D3D) sampler; baking the flip here is the single
        // chokepoint the runtime importer and the mesh recipe share (Blix.Recipes.MeshRecipe).
        Vector2 BuildUv(int v)
        {
            var uv = uvs is null ? Vector2.Zero : uvs[v];
            return new Vector2(uv.X, flipTextureV ? 1.0f - uv.Y : uv.Y);
        }
        Vector3 BuildNormal(int v) =>
            Vector3.Normalize(GraphicsMatrices.TransformDirection(normalMatrix, normals is null ? Vector3.UnitY : normals[v]));

        byte[] packed;
        VertexLayout layout;
        int uvOffset;
        if (includeTangents)
        {
            var verts = new VertexPosition3NormalTangentTexture[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var pWorld = GraphicsMatrices.TransformPoint(world, positions[v]);
                var nWorld = BuildNormal(v);
                // Tangent direction transforms by the model's linear part (NOT
                // the inverse-transpose used for normals). Preserve the w sign.
                Vector3 tDir;
                float tSign;
                if (tangents is not null)
                {
                    var t = tangents[v];
                    tDir = Vector3.Normalize(GraphicsMatrices.TransformDirection(world, new Vector3(t.X, t.Y, t.Z)));
                    tSign = t.W < 0f ? -1f : 1f;
                }
                else
                {
                    // No authored tangent — pick any axis perpendicular to N.
                    var up = MathF.Abs(nWorld.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
                    tDir = Vector3.Normalize(Vector3.Cross(up, nWorld));
                    tSign = 1f;
                }
                var uv = BuildUv(v);
                verts[v] = new VertexPosition3NormalTangentTexture(
                    new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                    new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                    new GraphicsVector4(tDir.X, tDir.Y, tDir.Z, tSign),
                    new GraphicsVector2(uv.X, uv.Y));
                minB = Vector3.Min(minB, pWorld);
                maxB = Vector3.Max(maxB, pWorld);
            }
            packed = VertexPosition3NormalTangentTexture.Pack(verts);
            layout = VertexPosition3NormalTangentTexture.Layout;
            uvOffset = 10 * sizeof(float);
        }
        else if (includeColour)
        {
            var verts = new VertexPosition3NormalTexture2Color[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var pWorld = GraphicsMatrices.TransformPoint(world, positions[v]);
                var nWorld = BuildNormal(v);
                var uv = BuildUv(v);
                // White when the primitive has no COLOR_0 of its own. Asked for and absent is the
                // COMMON case, not an error: CommonTree_1 carries occlusion on its trunk and
                // nothing on its leaf card, and both arrive through this branch in one model.
                var c = colours is null
                    ? VertexPosition3NormalTextureColor.White
                    : VertexPosition3NormalTextureColor.Pack(colours[v].X, colours[v].Y, colours[v].Z, colours[v].W);
                // A mesh with no second set gets its first one mirrored, not zeroed: a material
                // that names set 1 anyway then samples the same place rather than collapsing the
                // whole surface onto one texel.
                var uv1v = uv1 is null ? uv : new Vector2(uv1[v].X, flipTextureV ? 1.0f - uv1[v].Y : uv1[v].Y);
                verts[v] = new VertexPosition3NormalTexture2Color(
                    new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                    new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                    new GraphicsVector2(uv.X, uv.Y),
                    new GraphicsVector2(uv1v.X, uv1v.Y),
                    c);
                minB = Vector3.Min(minB, pWorld);
                maxB = Vector3.Max(maxB, pWorld);
            }
            packed = VertexPosition3NormalTexture2Color.Pack(verts);
            layout = VertexPosition3NormalTexture2Color.Layout;
            uvOffset = 6 * sizeof(float);
        }
        else
        {
            var verts = new VertexPosition3NormalTexture[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var pWorld = GraphicsMatrices.TransformPoint(world, positions[v]);
                var nWorld = BuildNormal(v);
                var uv = BuildUv(v);
                verts[v] = new VertexPosition3NormalTexture(
                    new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                    new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                    new GraphicsVector2(uv.X, uv.Y));
                minB = Vector3.Min(minB, pWorld);
                maxB = Vector3.Max(maxB, pWorld);
            }
            packed = VertexPosition3NormalTexture.Pack(verts);
            layout = VertexPosition3NormalTexture.Layout;
            uvOffset = 6 * sizeof(float);
        }

        var indicesSrc = primitive.GetIndices();
        // Pick the narrowest width that fits. UInt16 covers virtually every
        // authored asset; UInt32 kicks in for large packs like Khronos
        // Sponza Modern's curtains (66k vertices in a single primitive).
        var needsUInt32 = vertexCount > ushort.MaxValue;
        ushort[] indices16;
        uint[]? indices32;
        if (needsUInt32)
        {
            indices16 = Array.Empty<ushort>();
            indices32 = new uint[indicesSrc.Count];
            for (var i = 0; i < indicesSrc.Count; i++)
            {
                indices32[i] = indicesSrc[i];
            }
        }
        else
        {
            indices32 = null;
            indices16 = new ushort[indicesSrc.Count];
            for (var i = 0; i < indicesSrc.Count; i++)
            {
                indices16[i] = (ushort)indicesSrc[i];
            }
        }

        var bounds = vertexCount == 0 ? Bounds3.Empty : new Bounds3(minB, maxB);
        SanitizePackedUVs(packed, vertexCount, layout.Stride, uvOffset, name);
        return new MeshData(
            name,
            packed,
            indices16,
            layout,
            bounds,
            Indices32: indices32);
    }

    // Some authored assets ship one or two vertices with extreme UV values
    // (we've seen -42470 in the Khronos Intel Sponza source). With wrap=
    // Repeat the GPU still tiles, but adjacent triangles' UV interpolation
    // drags across thousands of units, blowing up dFdx/dFdy so the sampler
    // picks the coarsest mip everywhere -> washed-out garbage. Fold any
    // out-of-range vertex back into [0,1) with `frac` so the LOD calc and
    // texture cache stay sane; tiled textures still tile correctly because
    // frac is the same value modulo 1.
    private const float MaxReasonableUV = 100.0f;

    private static int SanitizePackedUVs(byte[] vertexBytes, int vertexCount, int stride, int uvOffset, string ownerName)
    {
        var touched = 0;
        var span = vertexBytes.AsSpan();
        for (var v = 0; v < vertexCount; v++)
        {
            var slot = span.Slice(v * stride + uvOffset, 8);
            var u = System.Runtime.InteropServices.MemoryMarshal.Read<float>(slot);
            var vv = System.Runtime.InteropServices.MemoryMarshal.Read<float>(slot.Slice(4));
            var fix = false;
            if (MathF.Abs(u) > MaxReasonableUV) { u -= MathF.Floor(u); fix = true; }
            if (MathF.Abs(vv) > MaxReasonableUV) { vv -= MathF.Floor(vv); fix = true; }
            if (fix)
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(slot, in u);
                System.Runtime.InteropServices.MemoryMarshal.Write(slot.Slice(4), in vv);
                touched++;
            }
        }
        if (touched > 0)
        {
            Console.WriteLine(
                $"[GltfStaticImporter] sanitized {touched} vertex UV(s) in '{ownerName}' " +
                $"(values exceeded |UV|>{MaxReasonableUV}; folded with frac to [0,1))");
        }
        return touched;
    }




    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true } f ? f.Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
    }

    /// <summary>The inverse-transpose of a model matrix, for transforming normals.</summary>
    /// <remarks>
    /// <b>Public because the mesh recipe needs it, and that is the whole test.</b> When the recipe
    /// left the engine it needed exactly two things from it — this and
    /// <see cref="BuildStaticMeshData"/> — and both are public API rather than something reached
    /// through <c>InternalsVisibleTo</c>. An internals grant would have made Blix's own recipe able
    /// to do what a project's recipe cannot, which is the blessing-by-privilege this arc exists to
    /// remove, rebuilt one level down.
    /// </remarks>
    public static Matrix4x4 ComputeNormalMatrix(Matrix4x4 model)
    {
        // F-016: engine row-vector convention. For a direction transform,
        // GraphicsMatrices.TransformDirection treats the matrix as row-vector
        // and uses the rotation 3x3 (M11-M33). For invariance under non-uniform
        // scale we need the inverse-transpose. In row-vector form that's
        // Transpose(Invert(model)).
        if (!Matrix4x4.Invert(model, out var inverse)) return Matrix4x4.Identity;
        return Matrix4x4.Transpose(inverse);
    }


}
