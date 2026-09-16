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

        // When a cooked .blixmesh sibling exists, the runtime only needs the
        // material descriptors + image URIs from the .gltf -- not the .bin
        // buffer data that SharpGLTF's default ModelRoot.Load eagerly reads
        // and validates against (for ~95% of a big scene's parse time).
        // ReadContext.Create + ValidationMode.Skip + a callback that returns
        // empty bytes for non-.gltf resources skips the buffer reads
        // entirely: 4500ms -> 11ms on Sponza main. Per-accessor reads would
        // fail under this model, but BlixMeshReader.Read replaces them.
        var blixmeshPath = Path.ChangeExtension(context.SourcePath, ".blixmesh");
        var useCookedMesh = File.Exists(blixmeshPath);

        // <b>The decision this whole arc is about, finally said out loud.</b> Which branch is taken
        // here has always been invisible: the check is File.Exists and there is no return value,
        // log line or field that says which way it went, so "is this asset on the fast path" was
        // not a question anything could ask. The stopwatch is started unconditionally because it is
        // three instructions and the alternative is a branch in a hot path to save them.
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var gltfFullPath = Path.GetFullPath(context.SourcePath);
        var gltfDirInfo = Path.GetDirectoryName(gltfFullPath) ?? string.Empty;
        var gltfFileName = Path.GetFileName(gltfFullPath);
        ModelRoot model;
        if (useCookedMesh)
        {
            var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip };
            ArraySegment<byte> Reader(string assetName)
            {
                // SharpGLTF asks for the CONTAINER first; supply it. Then it asks for any external
                // .bin / image files; return empty so the parser stops short of reading them.
                // Material and image metadata survives because it all lives in the JSON.
                //
                // <b>Matched by path, not by extension, and that distinction was a real bug.</b>
                // This tested `extension == ".gltf"`, so a cooked .glb got an empty buffer for its
                // own container and died with "JSon is empty". It went unnoticed because the path
                // was written for Sponza, which is .gltf plus external .bin, and the only cooked
                // .glb in the tree was loaded through the rigged importer, which has no cooked
                // path at all. It surfaced the moment asset coverage became complete and
                // TankArena's four .glb files got siblings.
                //
                // For a .glb the saving is smaller by nature — the buffer is inside the container,
                // so reading the JSON means reading the file — but the geometry still comes from
                // the .blixmesh rather than from accessor interpretation, which is the larger half.
                var full = Path.Combine(gltfDirInfo, assetName);
                if (string.Equals(Path.GetFullPath(full), gltfFullPath, StringComparison.Ordinal)
                    || Path.GetExtension(full) is { } ext
                       && (ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".glb", StringComparison.OrdinalIgnoreCase)))
                {
                    return new ArraySegment<byte>(File.ReadAllBytes(full));
                }
                return ArraySegment<byte>.Empty;
            }
            model = AssetImportException.Refusing(context.SourcePath, () =>
                SharpGLTF.Schema2.ReadContext.Create(Reader)
                    .WithSettingsFrom(settings)
                    .ReadSchema2(gltfFileName));
        }
        else
        {
            model = AssetImportException.Refusing(context.SourcePath, () => ModelRoot.Load(context.SourcePath));
        }

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

        // Cooked-mesh fast path. The blixmesh sibling was detected above
        // (used to short-circuit ModelRoot.Load's buffer reads); now read
        // its cooked vertex + index bytes instead of walking glTF accessors.
        // Material resolution still uses the lite (JSON-only) model.
        if (useCookedMesh)
        {
            var cooked = BlixMeshReader.Read(blixmeshPath);
            foreach (var p in cooked.Primitives)
            {
                // Heal degenerate UVs in cooked files too (in-place mutation
                // is fine, we own the buffer after BlixMeshReader returns it).
                // Lets us fix asset-level UV corruption without re-running the
                // cook step.
                // UV offset comes from the cooked layout (32-byte → 24,
                // 48-byte tangent → 40), not hardcoded.
                //
                // <b>Found by the first cooked mesh in this engine without tangents.</b> This looked the UV
                // up by location 3, which is the texture coordinate in the 48-byte tangent layout and the
                // <em>tangent</em> in nothing — in the 32-byte layout the texture coordinate is location 2
                // and there is no location 3 at all, so First threw and a cooked non-tangent mesh could not
                // be loaded. Every cooked asset so far came from VulkanSponza, which cooks with --tangents,
                // so the path had never been walked. Matched by format instead: the texture coordinate is
                // the only Float2 in either layout.
                var uvAttr = cooked.Layout.Attributes.First(
                    a => a.Format == VertexAttributeFormat.Float2);
                SanitizePackedUVs(p.VertexBytes, p.VertexCount, stride: cooked.Layout.Stride, uvOffset: uvAttr.Offset, p.Name);
                // LOD0 is the default index buffer; the full chain rides along
                // in Lods for the demo's distance-based selection.
                var lod0 = p.Lods[0];
                var lods = new MeshLod[p.Lods.Count];
                for (var l = 0; l < p.Lods.Count; l++)
                    lods[l] = new MeshLod(p.Lods[l].Indices16, p.Lods[l].Indices32, p.Lods[l].Error);
                var meshData = new MeshData(
                    p.Name,
                    p.VertexBytes,
                    lod0.Indices16 ?? Array.Empty<ushort>(),
                    cooked.Layout,
                    p.Bounds,
                    Indices32: lod0.Indices32,
                    Lods: lods);
                var gltfMat = p.MaterialIndex >= 0 && p.MaterialIndex < model.LogicalMaterials.Count
                    ? model.LogicalMaterials[p.MaterialIndex]
                    : null;
                var material = GltfShared.ExtractMaterial(gltfMat, materialCache, textureCache);
                primitives.Add(new GltfPrimitive(meshData, material));
            }
        }
        else
        {
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
            var cookedStamp = useCookedMesh ? CookedFile.TryReadHeader(blixmeshPath) : null;
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: context.SourcePath,
                CookedPath: useCookedMesh ? blixmeshPath : null,
                Mode: useCookedMesh ? AssetLoadMode.Cooked : AssetLoadMode.Source,
                Bytes: SafeLength(useCookedMesh ? blixmeshPath : context.SourcePath),
                LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Recipe: cookedStamp?.Stamp.Recipe,
                Warning: useCookedMesh ? null : "no .blixmesh sibling — glTF accessors were walked"));
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

        return new GltfNodeModel(nodes);
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

        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        // Authored tangents (glTF vec4: xyz dir + w handedness). MikkTSpace-
        // compatible per spec, so forwarding beats recomputing.
        var tangents = includeTangents ? primitive.GetVertexAccessor("TANGENT")?.AsVector4Array() : null;
        // AsColorArray, not AsVector4Array: COLOR_0 is legally float, ushort-normalised or
        // byte-normalised, and vec3 as well as vec4. This accessor collapses all six spellings to
        // 0..1 RGBA with alpha defaulted to opaque, which is the only reading that is correct for
        // every one of them.
        var colours = includeColour ? primitive.GetVertexAccessor("COLOR_0")?.AsColorArray() : null;

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
            var verts = new VertexPosition3NormalTextureColor[vertexCount];
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
                verts[v] = new VertexPosition3NormalTextureColor(
                    new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                    new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                    new GraphicsVector2(uv.X, uv.Y),
                    c);
                minB = Vector3.Min(minB, pWorld);
                maxB = Vector3.Max(maxB, pWorld);
            }
            packed = VertexPosition3NormalTextureColor.Pack(verts);
            layout = VertexPosition3NormalTextureColor.Layout;
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
