using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// CPU-simulated billboard particle system — the first NEW consumer of the
// per-frame transient vertex arena. Each frame it integrates particles on the CPU,
// expands every live one into a camera-facing quad, and uploads the lot as ONE
// transient-arena vertex slice (no owned/persistent vertex buffer — that's the
// arena's whole point). It records a single DrawIndexed against a caller-supplied
// pipeline, mirroring InstancedBatch's layering: the batch owns simulation +
// geometry expansion, the caller brings the shader/pipeline (and thus the blend
// mode — additive sparks vs alpha smoke are just two pipelines over one shader).
//
// Descriptor-less by design: vertices carry their own colour, the only binding is a
// view-projection push constant. That's why particles belong in the arena, not in
// MaterialBindings (see docs/renderer.md → "two substrates, one boundary").
public sealed class ParticleBatch : IDisposable
{
    // pos(3) + color(4) + uv(2) = 9 floats. The demo builds a matching pipeline
    // against this layout; exposed so callers stay in lockstep with the packing.
    public static VertexLayout VertexLayoutDescription { get; } = new(
        Stride: 9 * sizeof(float),
        Attributes: new[]
        {
            new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
            new VertexAttribute(1, VertexAttributeFormat.Float4, 3 * sizeof(float)),
            new VertexAttribute(2, VertexAttributeFormat.Float2, 7 * sizeof(float)),
        });

    private const int FloatsPerVertex = 9;
    private const int VertsPerParticle = 4;
    private const int IndicesPerParticle = 6;

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector4 Color;
        public float Age;
        public float Life;
        public float Size;
    }

    private readonly VulkanGraphicsDevice device;
    private readonly int maxParticles;
    private readonly IndexBufferHandle indexBuffer;   // static base-0 quad indices
    private readonly Particle[] particles;
    private readonly float[] scratch;                 // CPU vertex staging
    private readonly int[] order;                     // indices for optional depth sort
    private readonly byte[] pushConstants = new byte[64]; // view-projection
    private int count;
    private bool disposed;

    public ParticleBatch(VulkanGraphicsDevice device, int maxParticles, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (maxParticles <= 0) throw new ArgumentOutOfRangeException(nameof(maxParticles));
        this.device = device;
        this.maxParticles = maxParticles;
        particles = new Particle[maxParticles];
        scratch = new float[maxParticles * VertsPerParticle * FloatsPerVertex];
        order = new int[maxParticles];

        // Static quad index buffer: particle i -> indices [6i, 6i+6) over verts
        // [4i, 4i+4). Base-0, addressed via the arena slice's bind offset, exactly
        // like SpriteBatch. Built once.
        var indices = new ushort[maxParticles * IndicesPerParticle];
        for (var p = 0; p < maxParticles; p++)
        {
            var v = (ushort)(p * VertsPerParticle);
            var o = p * IndicesPerParticle;
            indices[o + 0] = (ushort)(v + 0);
            indices[o + 1] = (ushort)(v + 1);
            indices[o + 2] = (ushort)(v + 2);
            indices[o + 3] = (ushort)(v + 0);
            indices[o + 4] = (ushort)(v + 2);
            indices[o + 5] = (ushort)(v + 3);
        }
        indexBuffer = device.CreateIndexBuffer(indices, GraphicsBufferUsage.Static, name: $"{name ?? "particles"}.ib");
    }

    public int Count => count;

    // Spawn one particle (dropped silently if at capacity — emission shouldn't crash).
    public void Emit(Vector3 position, Vector3 velocity, Vector4 color, float life, float size)
    {
        if (count >= maxParticles) return;
        particles[count++] = new Particle
        {
            Position = position,
            Velocity = velocity,
            Color = color,
            Age = 0f,
            Life = life,
            Size = size,
        };
    }

    // Integrate + age. Dead particles are swap-removed so the live set stays packed
    // at the front. `acceleration` is gravity/wind applied to every particle.
    public void Update(float dt, Vector3 acceleration)
    {
        for (var i = 0; i < count; i++)
        {
            ref var p = ref particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                particles[i] = particles[--count];   // swap-remove
                i--;
                continue;
            }
            p.Velocity += acceleration * dt;
            p.Position += p.Velocity * dt;
        }
    }

    // Expand live particles into camera-facing billboards, upload as one transient
    // arena slice, and record one DrawIndexed against `pipeline` (whose blend state
    // chooses additive vs alpha). camRight/camUp orient the quads; when sortByDepth
    // is set (alpha blending), particles are drawn back-to-front from camPos.
    public void Draw(
        RenderPassBuilder pass,
        PipelineHandle pipeline,
        Matrix4x4 viewProjection,
        Vector3 camRight,
        Vector3 camUp,
        Vector3 camPos,
        bool sortByDepth)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (count == 0) return;

        for (var i = 0; i < count; i++) order[i] = i;
        if (sortByDepth)
        {
            // Back-to-front: farthest first so alpha blending composites correctly.
            var cam = camPos;
            Array.Sort(order, 0, count, Comparer<int>.Create((a, b) =>
                Vector3.DistanceSquared(particles[b].Position, cam)
                    .CompareTo(Vector3.DistanceSquared(particles[a].Position, cam))));
        }

        // Quad corners in billboard space + their UVs (UV drives the soft radial
        // falloff in the fragment shader).
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            new(-1, -1), new(1, -1), new(1, 1), new(-1, 1),
        };

        var f = 0;
        for (var oi = 0; oi < count; oi++)
        {
            ref var p = ref particles[order[oi]];
            // Fade alpha out over the particle's life so it dissolves rather than
            // popping when culled.
            var fade = 1f - (p.Age / p.Life);
            var c = p.Color;
            for (var k = 0; k < 4; k++)
            {
                var world = p.Position + (camRight * corners[k].X + camUp * corners[k].Y) * p.Size;
                scratch[f++] = world.X;
                scratch[f++] = world.Y;
                scratch[f++] = world.Z;
                scratch[f++] = c.X;
                scratch[f++] = c.Y;
                scratch[f++] = c.Z;
                scratch[f++] = c.W * fade;
                scratch[f++] = corners[k].X * 0.5f + 0.5f;   // uv
                scratch[f++] = corners[k].Y * 0.5f + 0.5f;
            }
        }

        var bytes = MemoryMarshal.AsBytes(scratch.AsSpan(0, count * VertsPerParticle * FloatsPerVertex));
        var slice = device.AllocVertices(bytes, VertexLayoutDescription.Stride, "particles.vertices");

        MemoryMarshal.Write(pushConstants.AsSpan(0, 64), in viewProjection);
        pass.DrawIndexed(
            slice.Buffer,
            indexBuffer,
            pipeline,
            indexCount: count * IndicesPerParticle,
            Array.Empty<ShaderUniform>(),
            Array.Empty<ShaderTextureBinding>(),
            pushConstants,
            indexOffset: 0,
            vertexOffset: 0,
            vertexBufferByteOffset: slice.ByteOffset);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyIndexBuffer(indexBuffer);
        // No vertex buffer to destroy — vertices ride the device-owned transient arena.
    }
}
