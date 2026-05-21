using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Render;

public sealed class SpriteBatch
{
    private const int MaxSprites = 4096;
    private const int MaxVertexCount = MaxSprites * 4;
    private const int MaxIndexCount = MaxSprites * 6;

    private const string VertexShaderSource = """
        #version 410 core
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec2 aTexCoord;
        layout (location = 2) in vec4 aColor;
        out vec2 vertexTexCoord;
        out vec4 vertexColor;
        uniform mat4 uViewProjection;
        void main()
        {
            vertexTexCoord = aTexCoord;
            vertexColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 410 core
        in vec2 vertexTexCoord;
        in vec4 vertexColor;
        out vec4 fragColor;
        uniform sampler2D uTexture;
        void main()
        {
            fragColor = texture(uTexture, vertexTexCoord) * vertexColor;
        }
        """;

    private readonly IGraphicsDevice device;
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private readonly Material material;
    private readonly byte[] uploadBuffer;
    private readonly Dictionary<int, (int Width, int Height)> textureDimensionsCache = [];
    private readonly List<SpriteEntry> entries = new(capacity: 64);

    private bool inBatch;
    private SpriteSortMode currentSortMode;
    private Matrix4x4 currentViewProjection;

    public SpriteBatch(IGraphicsDevice device)
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

        var shader = device.CreateShaderProgram(new ShaderSources(
            VertexShaderSource,
            FragmentShaderSource,
            VertexName: "sprite.vert",
            FragmentName: "sprite.frag"));

        var pipeline = device.CreatePipeline(
            new PipelineDescription(
                shader,
                VertexPosition3TextureColor.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.AlphaBlend),
            name: "sprite");

        material = new Material("sprite", pipeline);
        uploadBuffer = new byte[MaxVertexCount * VertexPosition3TextureColor.Layout.Stride];
    }

    public void Begin(
        Matrix4x4 viewProjection,
        SpriteSortMode sortMode = SpriteSortMode.Deferred)
    {
        // Deliberately takes a precomputed view-projection matrix rather than a camera
        // type. SpriteBatch lives in Blix.Render and doesn't depend on the Blix layer
        // above it; the caller (which typically owns a Camera2D in Blix) resolves the
        // matrix and hands it in.
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
        // Texture loaders that pre-flip vertically (the stb_image default the engine uses)
        // produce textures where V=0 samples the source image's bottom row. The default
        // flipV=true matches that, so a SourceRect in pixel-top-left coords samples the
        // intended region. For atlases authored directly in code (e.g. the font baker)
        // the buffer is NOT vertically flipped — pass flipV=false to skip the inversion.
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
        // partition. This is critical: UpdateVertexBuffer is immediate while
        // DrawIndexed is deferred — packing-then-drawing per partition would let
        // the second upload overwrite the first partition's vertex data before
        // the GPU ran either draw, so the first partition would render the wrong
        // geometry with its own texture. One upload, one VB state for the whole
        // pass, per-partition draws referencing distinct index ranges.
        var floats = MemoryMarshal.Cast<byte, float>(uploadBuffer.AsSpan());
        var floatOffset = 0;

        // Walk entries to find each partition's (start, end, texture) tuple AND
        // emit its quads to the upload buffer in the same iteration.
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

        var uniforms = new ShaderUniform[]
        {
            new ShaderUniform("uViewProjection", new Matrix4x4Uniform(currentViewProjection))
        };

        foreach (var (start, count, texture) in partitions)
        {
            // Index buffer is pre-baked with (4i + 0..3) -> 6-index quad runs, so
            // partition `start..start+count` lives at index range [start*6, (start+count)*6).
            pass.DrawIndexed(
                vertexBuffer,
                indexBuffer,
                material.Pipeline,
                indexCount: count * 6,
                indexOffset: start * 6,
                uniforms,
                new ShaderTextureBinding[]
                {
                    new ShaderTextureBinding("uTexture", texture, Slot: 0)
                });
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

        // Quad vertex order matches the pre-baked index buffer pattern (4i, 4i+1, 4i+2, 4i+3)
        // triangulated as (0, 1, 2) and (0, 2, 3). Verts must be wound CCW:
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

    // Maps a pixel-space source rect (origin top-left of the source image) to UV space.
    // When flipV=true the math accounts for stb_image's vertical pre-flip — V=0 samples
    // the source image's bottom row, so a "top" pixel row maps to a large V. When
    // flipV=false the V axis runs the same way as the pixel rect (top→0, bottom→1),
    // which is the natural convention for atlases authored directly in code.
    //
    // The returned tuple names (vTop, vBottom) describe which vertex they sit on in
    // the SpriteBatch's Y-up world convention — vTop is the V at the maxY vertices
    // (TR/TL in world). In Y-down screen-space those are the screen-bottom vertices.
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
