using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Render;

// Coherent Hierarchical Culling, 1-frame-stale variant.
//
// For each tracked entity:
//   - Maintain a `lastVisibility` flag (true on first sight; queries
//     update it asynchronously over the next 1–2 frames).
//   - Every frame, in RecordQuery: render the entity's world AABB as a
//     depth-tested proxy through a depth-no-write, color-no-write
//     pipeline, bracketed by glBeginQuery / glEndQuery. The result
//     ("did ANY fragment of the AABB pass depth?") becomes
//     lastVisibility for next frame.
//   - ShouldDraw consults lastVisibility — if false, the scene renderer
//     skips the real geometry draw.
//
// Why 1-frame-stale is OK: a submesh that becomes visible after being
// hidden takes 1–2 frames to "pop in." In practice, camera motion is
// continuous; static occluders mean visibility transitions happen at
// the edges of the camera frustum and are masked by the motion.
//
// Why this is conservative (no false negatives that matter): a fresh
// entity defaults to "visible". Only the query result can flip an
// entity to "occluded", and only after we've verified it. False
// positives (rendering things that turn out to be occluded) are the
// only error, and they just cost a draw we'd have done anyway.
//
// Proxy pipeline: color writes are suppressed via an Additive blend
// where the shader outputs vec4(0). dst = 0 + dst = dst (no change),
// but the fragment still ran depth-test → contributes a sample to the
// query if depth passed. Depth writes are off (LessEqualNoWrite) so
// the proxy doesn't pollute the depth buffer.
public sealed class GpuOcclusionCuller : IOccluder, IDisposable
{
    private readonly IGraphicsDevice device;
    private readonly IOcclusionQueryProvider queryProvider;
    private readonly Dictionary<string, EntityState> states = new(StringComparer.Ordinal);

    // Proxy unit cube resources. Vertex buffer = 8 corners of [-1,1]^3;
    // index buffer = 36 indices (12 triangles, 2 per face). One pipeline
    // + material shared across every query draw.
    private readonly VertexBufferHandle cubeVertexBuffer;
    private readonly IndexBufferHandle cubeIndexBuffer;
    private readonly Material proxyMaterial;
    private readonly Mesh proxyMesh;

    // Counter exposed for diagnostics — number of submeshes ShouldDraw
    // skipped this frame because of cached occlusion. Reset by callers
    // (Sponza) at the start of each render frame.
    public int OcclusionCulledThisFrame { get; private set; }

    // Camera view-projection captured at BeginFrame. Each query's proxy
    // AABB needs it as a per-draw uniform; capturing once per frame
    // avoids re-passing it through every RecordQuery call.
    private Matrix4x4 frameViewProjection = Matrix4x4.Identity;

    public GpuOcclusionCuller(IGraphicsDevice device, IOcclusionQueryProvider queryProvider)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(queryProvider);
        this.device = device;
        this.queryProvider = queryProvider;

        // No bare position-only vertex type in the engine yet; use
        // VertexPosition3Color with black colour. 32 bytes of "waste"
        // across 8 vertices is invisible at this scale, and it keeps
        // us from threading a one-off vertex layout through the engine.
        var packedVertices = new VertexPosition3Color[UnitCubeVertices.Length];
        for (var i = 0; i < UnitCubeVertices.Length; i++)
        {
            var p = UnitCubeVertices[i];
            packedVertices[i] = new VertexPosition3Color(
                new GraphicsVector3(p.X, p.Y, p.Z), new GraphicsColor(0, 0, 0, 0));
        }
        cubeVertexBuffer = device.CreateVertexBuffer(
            new VertexBufferData(
                new VertexBufferDescription(VertexPosition3Color.Layout, 8, GraphicsBufferUsage.Static),
                VertexPosition3Color.Pack(packedVertices)),
            name: "occlusion.proxy.vb");
        cubeIndexBuffer = device.CreateIndexBuffer(UnitCubeIndices, name: "occlusion.proxy.ib");

        var shader = device.CreateShaderProgram(new ShaderSources(
            VertexShader: ProxyVertexShader,
            FragmentShader: ProxyFragmentShader,
            VertexName: "occlusion-proxy.vert",
            FragmentName: "occlusion-proxy.frag"));

        var pipeline = device.CreatePipeline(
            new PipelineDescription(
                shader,
                VertexPosition3Color.Layout,
                PrimitiveTopology.Triangles,
                // Depth test on so we know if any fragment is in front
                // of existing geometry. Depth WRITE off so the proxy
                // box doesn't overwrite real geometry's depth with the
                // larger AABB extent.
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                // Additive blend + shader outputting vec4(0) acts as a
                // color-write mask: depth is tested, samples count
                // toward the query, but the color attachment stays
                // unchanged. We don't have a true ColorMask knob in
                // PipelineDescription; this is the equivalent.
                BlendState.Additive),
            name: "occlusion.proxy");

        proxyMaterial = new Material("occlusion.proxy", pipeline);
        proxyMesh = new Mesh("occlusion.proxy", cubeVertexBuffer, cubeIndexBuffer, UnitCubeIndices.Length, Bounds3.Empty);
    }

    // Resets the per-frame skipped counter and captures the camera's
    // world->clip matrix for use in proxy queries. Call at the start of
    // each render frame, before any RecordQuery. Visibility state itself
    // persists across frames; only counters / per-frame VP are reset.
    public void BeginFrame(Matrix4x4 viewProjection)
    {
        OcclusionCulledThisFrame = 0;
        frameViewProjection = viewProjection;
    }

    // Polls completed query results and updates visibility flags. Call
    // once per frame, after all queries have been submitted (e.g. after
    // device.Execute returns). Stale entries in `states` whose owners
    // are gone will keep their query handles until disposed.
    public void Update()
    {
        if (!queryProvider.OcclusionQuerySupported)
        {
            return;
        }
        foreach (var kv in states)
        {
            var s = kv.Value;
            if (s.PendingQueryId == 0)
            {
                continue;
            }
            if (queryProvider.TryGetOcclusionResult(s.PendingQueryId, out var anySamplesPassed))
            {
                if (anySamplesPassed)
                {
                    // Fast transition to visible — one positive result
                    // is enough. Newly-revealed geometry must not lag
                    // behind the camera by more than the unavoidable
                    // 1-frame query-issuance delay.
                    s.LastVisibility = true;
                    s.ConsecutiveOccludedResults = 0;
                }
                else
                {
                    // Slow transition to occluded — require N
                    // consecutive "occluded" results before actually
                    // culling. Suppresses the camera-settle pop-in
                    // where a transiently-occluded submesh would
                    // otherwise stay culled for an extra frame after
                    // the camera reveals it.
                    s.ConsecutiveOccludedResults++;
                    if (s.ConsecutiveOccludedResults >= OccludedHysteresisFrames)
                    {
                        s.LastVisibility = false;
                    }
                }
                queryProvider.ReleaseOcclusionQuery(s.PendingQueryId);
                s.PendingQueryId = 0;
            }
        }
    }

    public bool ShouldDraw(string entityPath)
    {
        if (!queryProvider.OcclusionQuerySupported)
        {
            return true; // backend can't measure; always draw
        }
        if (!states.TryGetValue(entityPath, out var s))
        {
            // First sighting — render it, query later.
            return true;
        }
        if (!s.LastVisibility)
        {
            OcclusionCulledThisFrame++;
            return false;
        }
        return true;
    }

    public void RecordQuery(RenderPassBuilder pass, string entityPath, Bounds3 worldBounds)
    {
        if (!queryProvider.OcclusionQuerySupported)
        {
            return;
        }
        var s = GetOrCreateState(entityPath);
        if (s.PendingQueryId != 0)
        {
            return;
        }
        var queryId = queryProvider.AllocateOcclusionQuery();
        if (queryId == 0)
        {
            return;
        }
        s.PendingQueryId = queryId;

        // Unit-cube -> world-space AABB transform. MUST use
        // GraphicsMatrices.CreateModel (column-vector convention) and
        // NOT compose with System.Numerics.CreateTranslation/CreateScale
        // (row-vector) — same convention footgun as CSM in 2bd2fc0.
        var center = (worldBounds.Min + worldBounds.Max) * 0.5f;
        var halfExtents = (worldBounds.Max - worldBounds.Min) * 0.5f;
        var transform = Blix.Graphics.GraphicsMatrices.CreateModel(
            position: center,
            rotation: Quaternion.Identity,
            scale: halfExtents);

        var perDrawUniforms = new ShaderUniform[]
        {
            new("uTransform", new Matrix4x4Uniform(transform)),
            new("uViewProjection", new Matrix4x4Uniform(frameViewProjection)),
        };

        pass.BeginOcclusionQuery(queryId);
        pass.DrawMesh(proxyMesh, proxyMaterial,
            perDrawUniforms: perDrawUniforms, perDrawTextures: null);
        pass.EndOcclusionQuery();
    }

    private EntityState GetOrCreateState(string entityPath)
    {
        if (!states.TryGetValue(entityPath, out var s))
        {
            s = new EntityState();
            states[entityPath] = s;
        }
        return s;
    }

    public void Dispose()
    {
        // Release any in-flight queries back to the pool so the device
        // can clean them up on its own Dispose.
        foreach (var s in states.Values)
        {
            if (s.PendingQueryId != 0)
            {
                queryProvider.ReleaseOcclusionQuery(s.PendingQueryId);
                s.PendingQueryId = 0;
            }
        }
        states.Clear();
        // Mesh / pipeline / shader handles are owned by the device's
        // resource registry and freed on its Dispose; we just drop the
        // references here.
    }

    // Hysteresis on visible -> occluded: require N consecutive
    // "occluded" query results before flipping ShouldDraw to false.
    // 2 is the smallest value that suppresses the typical camera-
    // settle pop-in (1-frame stale + 1-frame query-issuance latency)
    // without keeping known-truly-occluded submeshes drawn for long.
    private const int OccludedHysteresisFrames = 2;

    private sealed class EntityState
    {
        // Default true: a freshly-seen entity is drawn until the query
        // says otherwise. Avoids "pop in" of newly streamed geometry.
        public bool LastVisibility = true;
        // Count of consecutive "no samples passed" results since the
        // last positive (or since first sight). Cleared on any visible
        // result. LastVisibility flips to false only when this reaches
        // OccludedHysteresisFrames.
        public int ConsecutiveOccludedResults;
        public int PendingQueryId;
    }

    // 8 corners of the unit cube. Order doesn't matter for the index
    // buffer below; it just needs to map (vertex index) → (corner).
    private static readonly Vector3[] UnitCubeVertices =
    {
        new(-1, -1, -1), new( 1, -1, -1), new( 1,  1, -1), new(-1,  1, -1),
        new(-1, -1,  1), new( 1, -1,  1), new( 1,  1,  1), new(-1,  1,  1),
    };

    // 6 faces × 2 triangles × 3 verts = 36 indices. Counter-clockwise
    // winding when viewed from outside (matches GL's default front-face).
    private static readonly ushort[] UnitCubeIndices =
    {
        // -Z face
        0, 1, 2,  0, 2, 3,
        // +Z face
        5, 4, 7,  5, 7, 6,
        // -Y face
        0, 4, 5,  0, 5, 1,
        // +Y face
        2, 6, 7,  2, 7, 3,
        // -X face
        0, 3, 7,  0, 7, 4,
        // +X face
        1, 5, 6,  1, 6, 2,
    };

    private const string ProxyVertexShader = """
        #version 410 core
        layout(location = 0) in vec3 aPosition;
        uniform mat4 uTransform;
        uniform mat4 uViewProjection;
        void main()
        {
            gl_Position = uViewProjection * uTransform * vec4(aPosition, 1.0);
        }
        """;

    // ASCII-only inside the GLSL source: Apple GL 4.1's compiler
    // rejects non-ASCII even in comments (em-dashes, smart quotes,
    // etc). Keep this file pure ASCII when editing.
    private const string ProxyFragmentShader = """
        #version 410 core
        out vec4 outColor;
        void main()
        {
            // Additive blend (One, One) + this zero source = dst
            // unchanged. The fragment still runs through depth test
            // and contributes a sample to the active query if it
            // passed - that is what we want.
            outColor = vec4(0.0);
        }
        """;
}
