using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// 2D textured-quad batcher on the Vulkan backend. Shape mirrors VkImGuiRenderer:
// a single pre-sized dynamic vertex buffer re-uploaded once per Begin/End, a
// pre-baked static index buffer, one alpha-blended depth-disabled pipeline, and
// the view-projection fed through a vertex-stage push constant. Sprites of the
// same texture are partitioned into one DrawIndexed sub-range each (the texture
// binds at set 0, slot 0 — the inline ShaderTextureBinding path).
//
// Vulkan-coupled deliberately: it owns its shader (sprite.vert/frag, shipped as
// SPIR-V next to the assembly) and its pipeline so callers get a turnkey 2D
// renderer rather than per-demo pipeline boilerplate. The pipeline bakes against
// `renderTarget` (null = swapchain) for render-pass compatibility, so create one
// SpriteBatch per target surface you draw into.
public sealed class SpriteBatch : IDisposable
{
    private const int MaxSprites = 4096;
    private const int MaxVertexCount = MaxSprites * 4;
    private const int MaxIndexCount = MaxSprites * 6;

    private readonly VulkanGraphicsDevice device;
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private readonly ShaderProgramHandle shader;
    private readonly PipelineHandle pipeline;
    private readonly byte[] uploadBuffer;
    private readonly byte[] pushConstants = new byte[64]; // one mat4 (view-projection)
    private readonly Dictionary<int, (int Width, int Height)> textureDimensionsCache = [];
    private readonly List<SpriteEntry> entries = new(capacity: 64);

    private bool inBatch;
    private bool disposed;
    private SpriteSortMode currentSortMode;
    private Matrix4x4 currentViewProjection;

    // renderTarget: the surface this batch's pipeline is baked against (Vulkan
    // render-pass compatibility requires matching attachment formats). Null
    // targets the swapchain. For an offscreen pass, pass that surface's handle
    // (e.g. graph.GetPassSurface(...) or CreateRenderSurface(...).Handle).
    public SpriteBatch(VulkanGraphicsDevice device, RenderSurfaceHandle? renderTarget = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;

        var emptyVertices = new VertexPosition3TextureColor[MaxVertexCount];
        var initialData = new VertexBufferData(
            new VertexBufferDescription(VertexPosition3TextureColor.Layout, MaxVertexCount, GraphicsBufferUsage.Dynamic),
            VertexPosition3TextureColor.Pack(emptyVertices));
        vertexBuffer = device.CreateVertexBuffer(initialData, name: "sprite.vertices");

        var quadIndices = new ushort[MaxIndexCount];
        for (var sprite = 0; sprite < MaxSprites; sprite++)
        {
            var baseVertex = (ushort)(sprite * 4);
            var offset = sprite * 6;
            quadIndices[offset + 0] = (ushort)(baseVertex + 0);
            quadIndices[offset + 1] = (ushort)(baseVertex + 1);
            quadIndices[offset + 2] = (ushort)(baseVertex + 2);
            quadIndices[offset + 3] = (ushort)(baseVertex + 0);
            quadIndices[offset + 4] = (ushort)(baseVertex + 2);
            quadIndices[offset + 5] = (ushort)(baseVertex + 3);
        }
        indexBuffer = device.CreateIndexBuffer(quadIndices, GraphicsBufferUsage.Static, name: "sprite.indices");

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "sprite.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "sprite.frag.spv"));
        var spriteInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        shader = device.CreateShaderProgramFromSpv(vertSpv, fragSpv, spriteInterface, "sprite");

        pipeline = device.CreatePipeline(
            new PipelineDescription(
                shader,
                VertexPosition3TextureColor.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                ColorBlends: [BlendState.AlphaBlend],
                RenderTarget: renderTarget),
            name: "sprite");

        uploadBuffer = new byte[MaxVertexCount * VertexPosition3TextureColor.Layout.Stride];
    }

    public void Begin(
        Matrix4x4 viewProjection,
        SpriteSortMode sortMode = SpriteSortMode.Deferred)
    {
        // Takes a precomputed view-projection rather than a camera type so
        // SpriteBatch doesn't depend on the Blix layer above it. The matrix
        // must be in Vulkan NDC convention (see GraphicsMatrices
        // .CreateOrthographicOffCenterVulkan for screen-space 2D).
        if (inBatch)
        {
            throw new InvalidOperationException("SpriteBatch.Begin called while a batch is already active. Call End first.");
        }

        currentViewProjection = viewProjection;
        currentSortMode = sortMode;
        entries.Clear();
        inBatch = true;
    }

    public void Draw(
        TextureHandle texture,
        Vector2 position,
        Vector2 size,
        Rect? sourceRect = null,
        GraphicsColor? color = null,
        float depth = 0.0f,
        bool flipV = true)
    {
        if (!inBatch)
        {
            throw new InvalidOperationException("SpriteBatch.Draw called outside Begin/End.");
        }

        if (entries.Count >= MaxSprites)
        {
            throw new InvalidOperationException($"SpriteBatch exceeded {MaxSprites} sprites in a single Begin/End pair.");
        }

        entries.Add(new SpriteEntry(
            texture,
            position,
            size,
            sourceRect,
            color ?? new GraphicsColor(1.0f, 1.0f, 1.0f, 1.0f),
            depth,
            flipV));
    }

    public void End(RenderPassBuilder pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        if (!inBatch)
        {
            throw new InvalidOperationException("SpriteBatch.End called without a matching Begin.");
        }

        if (entries.Count == 0)
        {
            inBatch = false;
            return;
        }

        SortEntries();
        FlushEntries(pass);

        entries.Clear();
        inBatch = false;
    }

    private void SortEntries()
    {
        switch (currentSortMode)
        {
            case SpriteSortMode.Deferred:
                return;

            case SpriteSortMode.BackToFront:
                entries.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
                return;

            case SpriteSortMode.FrontToBack:
                entries.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));
                return;

            case SpriteSortMode.Texture:
                entries.Sort(static (a, b) => a.Texture.Id.CompareTo(b.Texture.Id));
                return;
        }
    }

    private void FlushEntries(RenderPassBuilder pass)
    {
        // Pack every entry into the upload buffer in one pass, ordered by texture
        // partition. Critical invariant (matches VkImGuiRenderer): the dynamic
        // vertex buffer is uploaded ONCE, then each partition issues a sub-range
        // DrawIndexed. Packing-then-uploading per partition would let a later
        // upload clobber an earlier partition's vertices before the GPU ran any
        // recorded draw.
        var floats = MemoryMarshal.Cast<byte, float>(uploadBuffer.AsSpan());
        var floatOffset = 0;

        var partitionStart = 0;
        var currentTexture = entries[0].Texture;
        var partitions = new List<(int Start, int Count, TextureHandle Texture)>(capacity: 4);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Texture.Id != currentTexture.Id)
            {
                partitions.Add((partitionStart, i - partitionStart, currentTexture));
                partitionStart = i;
                currentTexture = entries[i].Texture;
            }
            var (texW, texH) = GetTextureDimensions(entries[i].Texture);
            EmitQuad(floats, ref floatOffset, entries[i], texW, texH);
        }
        partitions.Add((partitionStart, entries.Count - partitionStart, currentTexture));

        var byteCount = entries.Count * 4 * VertexPosition3TextureColor.Layout.Stride;
        device.UpdateVertexBuffer(vertexBuffer, uploadBuffer.AsSpan(0, byteCount));

        // View-projection rides a vertex-stage push constant. The whole batch
        // shares one matrix, so a single shared byte[] is safe across the
        // partition draws (no per-draw push divergence). Written raw: GLSL reads
        // std430 push bytes column-major, so the System.Numerics row-vector
        // matrix lands as its transpose and `uVP * v_col` equals `v_row * uVP`.
        MemoryMarshal.Write(pushConstants.AsSpan(0, 64), in currentViewProjection);

        var emptyUniforms = Array.Empty<ShaderUniform>();
        foreach (var (start, count, texture) in partitions)
        {
            // Pre-baked index buffer maps sprite i -> indices [6i, 6i+6), so
            // partition `start..start+count` lives at index range [start*6, ...).
            pass.DrawIndexed(
                vertexBuffer,
                indexBuffer,
                pipeline,
                indexCount: count * 6,
                emptyUniforms,
                new ShaderTextureBinding[]
                {
                    new ShaderTextureBinding("uTexture", texture, Slot: 0)
                },
                pushConstants,
                indexOffset: start * 6);
        }
    }

    private static void EmitQuad(Span<float> floats, ref int floatOffset, SpriteEntry entry, int texW, int texH)
    {
        var (uLeft, vTop, uRight, vBottom) = ComputeUV(entry.SourceRect, texW, texH, entry.FlipV);

        var minX = entry.Position.X;
        var minY = entry.Position.Y;
        var maxX = minX + entry.Size.X;
        var maxY = minY + entry.Size.Y;
        var z = entry.Depth;
        var c = entry.Color;

        // Vertex order matches the pre-baked index pattern (0,1,2, 0,2,3):
        // BL (0), BR (1), TR (2), TL (3).
        WriteVertex(floats, ref floatOffset, minX, minY, z, uLeft, vBottom, c);
        WriteVertex(floats, ref floatOffset, maxX, minY, z, uRight, vBottom, c);
        WriteVertex(floats, ref floatOffset, maxX, maxY, z, uRight, vTop, c);
        WriteVertex(floats, ref floatOffset, minX, maxY, z, uLeft, vTop, c);
    }

    private static void WriteVertex(
        Span<float> floats,
        ref int offset,
        float x, float y, float z,
        float u, float v,
        GraphicsColor color)
    {
        floats[offset] = x;
        floats[offset + 1] = y;
        floats[offset + 2] = z;
        floats[offset + 3] = u;
        floats[offset + 4] = v;
        floats[offset + 5] = color.Red;
        floats[offset + 6] = color.Green;
        floats[offset + 7] = color.Blue;
        floats[offset + 8] = color.Alpha;
        offset += 9;
    }

    // Maps a pixel-space source rect (origin top-left of the source image) to UV
    // space. flipV=true accounts for stb_image's vertical pre-flip; flipV=false
    // is the natural convention for code-authored atlases (the font baker).
    private static (float uLeft, float vTop, float uRight, float vBottom) ComputeUV(Rect? sourceRect, int texW, int texH, bool flipV)
    {
        if (sourceRect is not { } r)
        {
            return flipV ? (0.0f, 1.0f, 1.0f, 0.0f) : (0.0f, 0.0f, 1.0f, 1.0f);
        }

        var fw = (float)texW;
        var fh = (float)texH;
        return flipV
            ? (
                uLeft: r.X / fw,
                vTop: 1.0f - r.Y / fh,
                uRight: (r.X + r.Width) / fw,
                vBottom: 1.0f - (r.Y + r.Height) / fh)
            : (
                uLeft: r.X / fw,
                vTop: (r.Y + r.Height) / fh,
                uRight: (r.X + r.Width) / fw,
                vBottom: r.Y / fh);
    }

    private (int Width, int Height) GetTextureDimensions(TextureHandle handle)
    {
        if (textureDimensionsCache.TryGetValue(handle.Id, out var cached))
        {
            return cached;
        }

        var snapshot = device.SnapshotResources();
        var entry = snapshot.FindTexture(handle)
            ?? throw new InvalidOperationException($"Texture handle {handle.Id} is not registered in the resource registry.");
        var dims = (entry.Width, entry.Height);
        textureDimensionsCache[handle.Id] = dims;
        return dims;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyPipeline(pipeline);
        device.DestroyShaderProgram(shader);
        device.DestroyIndexBuffer(indexBuffer);
        device.DestroyVertexBuffer(vertexBuffer);
    }

    private readonly struct SpriteEntry
    {
        public SpriteEntry(
            TextureHandle texture,
            Vector2 position,
            Vector2 size,
            Rect? sourceRect,
            GraphicsColor color,
            float depth,
            bool flipV)
        {
            Texture = texture;
            Position = position;
            Size = size;
            SourceRect = sourceRect;
            Color = color;
            Depth = depth;
            FlipV = flipV;
        }

        public TextureHandle Texture { get; }
        public Vector2 Position { get; }
        public Vector2 Size { get; }
        public Rect? SourceRect { get; }
        public GraphicsColor Color { get; }
        public float Depth { get; }
        public bool FlipV { get; }
    }
}
