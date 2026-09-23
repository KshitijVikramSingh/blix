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
// Material, image, and ignored-semantic extraction is shared with the rigged
// importer through GltfShared so both source paths interpret glTF the same way.
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
        // Normalize parser failures and the importer's own content refusals into the same
        // path-bearing boundary for callers and diagnostic tools.
        return AssetImportException.Refusing(context.SourcePath, () => ImportCore(context));
    }

    /// <summary>
    /// Null when the cooked mesh was made with the settings this caller wants; else why not.
    /// </summary>
    /// <remarks>
    /// Only the settings that change the VERTICES are compared. <c>flipV</c> moves texture
    /// coordinates and <c>tangents</c> changes the stride and the attribute set — a mesh cooked
    /// with either the other way is not a slower answer to the question, it is a different one.
    /// The remaining stamped parameters (split, foliage, simplify) change how geometry is divided
    /// or decimated, which every consumer takes as it comes.
    /// </remarks>
    private static string? SettingsMismatch(string blixmeshPath, AssetImportContext context)
    {
        var stamp = CookedFile.TryReadHeader(blixmeshPath)?.Stamp;
        if (stamp is null) return null;

        // Rig recipes use a different parameter vocabulary and are handled by GltfImporter.
        if (!stamp.Value.Parameters.Contains("flipV=", StringComparison.Ordinal)) return null;

        var want = $"flipV={(context.FlipTextureV ? 1 : 0)} tangents={(context.IncludeTangents ? 1 : 0)}";
        if (stamp.Value.Parameters.Contains(want, StringComparison.Ordinal)) return null;

        return $"a .blixmesh sibling exists but was cooked with '{stamp.Value.Parameters}' and this "
             + $"load wants '{want}' — the glTF was walked instead. Re-cook with matching flags.";
    }

    /// <summary>Loads a cooked mesh and nothing else — no glTF is opened, and none need exist.</summary>
    /// <remarks>
    /// The image table gives each material channel a row naming a relative resource, so image
    /// resolution does not require reopening the source glTF.
    /// <para>
    /// The texture cache and <c>MaterialFromCooked</c> use the same image-table row keys.
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

        var primitives = new List<GltfPrimitive>(cooked.Primitives.Count);
        foreach (var p in cooked.Primitives)
        {
            // Sanitize the owned cooked buffer in place. Locate UVs by attribute format because
            // their location differs between the vertex layouts carried by .blixmesh.
            var uvAttr = p.Layout.Attributes.First(a => a.Format == VertexAttributeFormat.Float2);
            SanitizePackedUVs(
                p.VertexBytes, p.VertexCount, stride: p.Layout.Stride,
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
                    p.Layout, p.Bounds, Indices32: lod0.Indices32, Lods: lods),
                GltfShared.MaterialFromCooked(
                    cooked.MaterialTable, p.MaterialIndex, materialCache, textureCache, blixmeshPath)));
        }

        if (AssetLoadLog.Enabled)
        {
            // SourcePath remains the caller's requested identity; CookedPath records the artifact
            // that satisfied it.
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
        // A .blixmesh may be requested directly or discovered as a sibling. It carries geometry,
        // materials, and image resources and therefore loads without opening a source glTF.
        string? cookedMismatch = null;
        var direct = Path.GetExtension(context.SourcePath)
            .Equals(".blixmesh", StringComparison.OrdinalIgnoreCase);
        var blixmeshPath = direct
            ? context.SourcePath
            : Path.ChangeExtension(context.SourcePath, ".blixmesh");
        if (File.Exists(blixmeshPath))
        {
            // A cooked artifact is usable only when its vertex-affecting recipe settings match the
            // requested layout and UV convention.
            // A rigged cooked file holds skinned vertices this importer cannot draw. Refused by
            // name when asked for directly; when it is merely a sibling, the glTF is walked, which
            // is what a caller asking the STATIC importer for a rigged asset has always got.
            if (BlixMeshReader.Read(blixmeshPath).IsRigged)
            {
                if (direct)
                {
                    throw new AssetImportException(
                        context.SourcePath, null,
                        "this .blixmesh holds a rig, so the static importer is the wrong one for it — "
                        + "load it through GltfImporter");
                }

                cookedMismatch = "the .blixmesh sibling holds a rig — the glTF was walked as static geometry";
            }
            else if (SettingsMismatch(blixmeshPath, context) is { } mismatch)
            {
                if (direct)
                {
                    // Named directly, so there is no source to fall back to. Refusing by name beats
                    // drawing something wrong.
                    throw new AssetImportException(context.SourcePath, null, mismatch);
                }

                cookedMismatch = mismatch;
            }
            else
            {
                return ImportCooked(context.SourcePath, blixmeshPath);
            }
        }

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
        GltfShared.PreDecodeImages(model, textureCache, gltfDir, context.SourcePath);

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null) continue;
            // Engine and SharpGLTF both use System.Numerics row-vector form.
            // No transpose needed; just use the world matrix directly.
            var world = node.WorldMatrix;
            var normalMatrix = ComputeNormalMatrix(world);

            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                var meshData = BuildStaticMeshData(meshName, prim, world, normalMatrix, context.FlipTextureV, context.IncludeTangents, context.IncludeColour);
                var material = GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache, context.SourcePath);
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
                Warning: cookedMismatch ?? "no .blixmesh sibling — glTF accessors were walked"));
        }

        return new GltfModel(
            primitives.ToArray(),
            new Skeleton(Array.Empty<Bone>()),
            Array.Empty<AnimationClip>(),
            Matrix4x4.Identity,
            Ignored: GltfShared.CollectIgnored(model, StaticFeatures(context)));
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
        // Normalize parser failures and node-import refusals into the same path-bearing boundary.
        return AssetImportException.Refusing(context.SourcePath, () => ImportNodesCore(context));
    }

    /// <summary>
    /// The node hierarchy of a COOKED mesh, with its baked vertices returned to node-local space.
    /// </summary>
    /// <remarks>
    /// Reconstructs the node-shaped view used by hierarchy-aware tools: names, parents, pivots,
    /// and per-node primitives.
    /// <para>
    /// The flat cook folds each node's world matrix into its vertices. This hierarchy path applies
    /// the recorded node's inverse to recover local-space geometry for consumers that need it.
    /// </para>
    /// <para>
    /// A node whose matrix will not invert keeps its baked vertices and is reported. A degenerate
    /// transform — a zero scale on some axis — has genuinely destroyed information, and returning
    /// silently wrong geometry would be worse than returning geometry that is merely still baked.
    /// </para>
    /// </remarks>
    private static GltfNodeModel ImportCookedNodes(string blixmeshPath, bool includeColour)
    {
        var cooked = BlixMeshReader.Read(blixmeshPath);
        var table = cooked.NodeTable;
        if (table.Count == 0)
        {
            throw new AssetImportException(
                blixmeshPath, null,
                "this .blixmesh carries no node table, so there is no hierarchy in it — re-cook it");
        }

        // World per node, in one forward pass. The reader has already refused a table whose parents
        // do not precede their children, which is what makes one pass enough.
        var world = new Matrix4x4[table.Count];
        for (var i = 0; i < table.Count; i++)
        {
            world[i] = table[i].ParentIndex < 0
                ? table[i].LocalTransform
                : table[i].LocalTransform * world[table[i].ParentIndex];
        }

        var byNode = new List<GltfPrimitive>[table.Count];
        var materialCache = new Dictionary<int, GltfMaterial>();
        var textureCache = new Dictionary<int, GltfTexture>();
        var cookedDir = Path.GetDirectoryName(Path.GetFullPath(blixmeshPath)) ?? string.Empty;
        GltfShared.LoadImagesFromTable(cooked.ImageTable, cooked.MaterialTable, cookedDir, textureCache);

        foreach (var p in cooked.Primitives)
        {
            if (p.NodeIndex < 0 || p.NodeIndex >= table.Count) continue;
            (byNode[p.NodeIndex] ??= new List<GltfPrimitive>()).Add(
                new GltfPrimitive(
                    Unbake(p, world[p.NodeIndex], blixmeshPath, includeColour),
                    GltfShared.MaterialFromCooked(
                        cooked.MaterialTable, p.MaterialIndex, materialCache, textureCache, blixmeshPath),
                    MaterialIndex: p.MaterialIndex));
        }

        var nodes = new GltfNode[table.Count];
        for (var i = 0; i < table.Count; i++)
        {
            nodes[i] = new GltfNode(
                table[i].Name,
                table[i].ParentIndex,
                table[i].LocalTransform,
                byNode[i]?.ToArray() ?? Array.Empty<GltfPrimitive>());
        }

        return new GltfNodeModel(nodes);
    }

    /// <summary>
    /// A cooked primitive as node-local geometry, in the layout the CALLER asked for.
    /// </summary>
    /// <remarks>
    /// Converts the cooked vertex stream to the layout requested by the caller. In particular,
    /// <c>includeColour</c> produces the 44-byte
    /// <c>VertexPosition3NormalTexture2Color</c> layout rather than exposing the cooked stream's
    /// stride to a pipeline that declares another one.
    /// <para>
    /// The defaults match what the source path uses for an asset that declares neither, so a cooked
    /// load and a source load agree: uv1 falls back to uv0, and an absent COLOR_0 is white, which is
    /// glTF's own rule because colour is a multiplier.
    /// </para>
    /// <para>
    /// Attributes are found by FORMAT — first Float3 is position, second is normal, first Float2 is
    /// the texture coordinate — rather than by location, for the same reason the UV heal is: location
    /// numbers differ between the layouts this format emits and the formats do not.
    /// </para>
    /// </remarks>
    private static MeshData Unbake(
        BlixMeshPrimitive p, in Matrix4x4 world, string path, bool includeColour)
    {
        var lod0 = p.Lods[0];
        var indices16 = lod0.Indices16 ?? Array.Empty<ushort>();

        var float3 = p.Layout.Attributes.Where(a => a.Format == VertexAttributeFormat.Float3).ToArray();
        var float2 = p.Layout.Attributes.FirstOrDefault(a => a.Format == VertexAttributeFormat.Float2);
        if (float3.Length < 2 || float2 is null)
        {
            throw new AssetImportException(
                path, null,
                $"primitive '{p.Name}' has a {p.Layout.Stride}-byte layout this reader cannot take apart "
                + "— it needs a position and a normal (Float3) and a texture coordinate (Float2)");
        }

        Matrix4x4.Invert(world, out var inverse);
        var invertible = !world.IsIdentity && Matrix4x4.Invert(world, out inverse);
        if (!world.IsIdentity && !invertible)
        {
            // A degenerate transform — a zero scale on some axis — has genuinely destroyed
            // information. Leaving the vertices baked is wrong in a way a person can see; returning
            // silently wrong geometry is wrong in a way nobody can.
            Console.WriteLine(
                $"[blix] '{p.Name}' in {Path.GetFileName(path)} sits under a transform that will not "
                + "invert — its vertices stay in world space.");
        }

        var normalMatrix = invertible ? ComputeNormalMatrix(inverse) : Matrix4x4.Identity;
        var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(p.VertexBytes);
        var stride = p.Layout.Stride / sizeof(float);
        var positionAt = float3[0].Offset / sizeof(float);
        var normalAt = float3[1].Offset / sizeof(float);
        var uvAt = float2.Offset / sizeof(float);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var colourVerts = includeColour ? new VertexPosition3NormalTexture2Color[p.VertexCount] : null;
        var plainVerts = includeColour ? null : new VertexPosition3NormalTexture[p.VertexCount];

        for (var v = 0; v < p.VertexCount; v++)
        {
            var at = v * stride;
            var position = new Vector3(source[at + positionAt], source[at + positionAt + 1], source[at + positionAt + 2]);
            var normal = new Vector3(source[at + normalAt], source[at + normalAt + 1], source[at + normalAt + 2]);
            var uv = new Vector2(source[at + uvAt], source[at + uvAt + 1]);

            if (invertible)
            {
                position = Vector3.Transform(position, inverse);
                normal = Vector3.TransformNormal(normal, normalMatrix);
                if (normal.LengthSquared() > 1e-12f) normal = Vector3.Normalize(normal);
            }

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);

            var gp = new GraphicsVector3(position.X, position.Y, position.Z);
            var gn = new GraphicsVector3(normal.X, normal.Y, normal.Z);
            var gt = new GraphicsVector2(uv.X, uv.Y);

            if (colourVerts is not null)
            {
                // uv1 falls back to uv0 and colour to white — the same defaults the source path
                // applies to an asset declaring neither.
                colourVerts[v] = new VertexPosition3NormalTexture2Color(
                    gp, gn, gt, gt, VertexPosition3NormalTextureColor.White);
            }
            else
            {
                plainVerts![v] = new VertexPosition3NormalTexture(gp, gn, gt);
            }
        }

        var packed = colourVerts is not null
            ? VertexPosition3NormalTexture2Color.Pack(colourVerts)
            : VertexPosition3NormalTexture.Pack(plainVerts!);
        var layout = colourVerts is not null
            ? VertexPosition3NormalTexture2Color.Layout
            : VertexPosition3NormalTexture.Layout;

        return new MeshData(
            p.Name, packed, indices16, layout,
            p.VertexCount > 0 ? new Bounds3(min, max) : p.Bounds,
            Indices32: lod0.Indices32);
    }

    private GltfNodeModel ImportNodesCore(AssetImportContext context)
    {
        if (Path.GetExtension(context.SourcePath).Equals(".blixmesh", StringComparison.OrdinalIgnoreCase))
        {
            return ImportCookedNodes(context.SourcePath, context.IncludeColour);
        }

        var model = AssetImportException.Refusing(context.SourcePath, () => ModelRoot.Load(context.SourcePath));
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) ?? string.Empty;
        var textureCache = new Dictionary<int, GltfTexture>();
        var materialCache = new Dictionary<int, GltfMaterial>();
        GltfShared.PreDecodeImages(model, textureCache, gltfDir, context.SourcePath);

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
                    prims[j] = new GltfPrimitive(meshData, GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache, context.SourcePath));
                }
            }

            nodes[i] = new GltfNode(node.Name ?? $"node{i}", parent, node.LocalMatrix, prims);
        }

        return new GltfNodeModel(nodes, GltfShared.CollectIgnored(model, StaticFeatures(context)));
    }

    private static GltfShared.VertexFeatures StaticFeatures(AssetImportContext context) =>
        (context.IncludeTangents ? GltfShared.VertexFeatures.Tangents : GltfShared.VertexFeatures.None)
        | (context.IncludeColour ? GltfShared.VertexFeatures.Colour : GltfShared.VertexFeatures.None);

    /// <param name="includeColour">
    /// Read <c>COLOR_0</c> and <c>TEXCOORD_1</c> into the 44-byte
    /// <c>VertexPosition3NormalTexture2Color</c> layout. This is opt-in because the importer output
    /// layout must match the pipeline's declared vertex stride and attributes.
    /// </param>
    public static MeshData BuildStaticMeshData(string name, MeshPrimitive primitive, Matrix4x4 world, Matrix4x4 normalMatrix, bool flipTextureV = false, bool includeTangents = false, bool includeColour = false)
    {
        if (includeTangents && includeColour)
        {
            // No current vertex layout carries both tangent and colour/second-UV attributes.
            throw new NotSupportedException(
                $"'{name}': tangents and vertex colour cannot be imported together — no vertex layout carries both.");
        }

        // ── What glTF 2.0 defines, and what this reads ──────────────────────────────────────
        // Spec §3.7.2.1 "Meshes / Overview", the mesh.primitive attribute table:
        //   https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#meshes-overview
        //
        // This static path reads POSITION and optional NORMAL/TEXCOORD_0. Tangent mode adds
        // TANGENT; colour mode instead adds COLOR_0 and TEXCOORD_1. Higher colour/UV sets, morph
        // attributes, skin attributes, and application-specific semantics are not represented by
        // these static vertex layouts and are reported through CollectIgnored.
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
        // AsColorArray normalizes all legal component encodings and supplies alpha 1 for vec3.
        var colours = includeColour ? primitive.GetVertexAccessor("COLOR_0")?.AsColorArray() : null;

        // The colour layout also carries TEXCOORD_1; absent data falls back below to UV0.
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
    /// Public because project-owned recipes use the same normal transformation as runtime imports;
    /// recipe assemblies do not require privileged internal access.
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
