using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// CPU-simulated billboard particle system — the first NEW consumer of the
// per-frame transient vertex arena. Each frame it integrates particles on the CPU,
// expands every live one into a camera-facing quad, and uploads the lot as ONE
// transient-arena vertex slice (no owned/persistent vertex buffer — that's the
// arena's whole point), then records a single DrawIndexed.
//
// The batch owns ONLY geometry: simulation, billboard expansion, optional depth sort,
// arena upload. The CALLER brings the pipeline, the push constants, and any texture
// bindings — mirroring InstancedBatch's layering. So the look is the caller's: blend
// mode (additive sparks vs alpha smoke), a plain viewProj push, or a soft-particle
// shader that samples scene depth and reads a fade push — none of that lives here. The
// batch stays a descriptor-less arena consumer; whatever bindings the caller hands it
// just ride through to the draw (see docs/renderer.md → "two substrates, one boundary").
public sealed class ParticleBatch : IDisposable
{
    // pos(3) + color(4) + uv(2) = 9 floats. The caller builds a matching pipeline
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
        // Colour and size interpolate from Start→End across the particle's life, so
        // sparks can cool (white-hot → ember → transparent) and smoke can swell.
        // End-colour alpha 0 makes a particle dissolve rather than pop on cull.
        public Vector4 StartColor;
        public Vector4 EndColor;
        public float StartSize;
        public float EndSize;
        public float Age;
        public float Life;
    }

    private readonly VulkanGraphicsDevice device;
    private readonly int maxParticles;
    private readonly IndexBufferHandle indexBuffer;   // static base-0 quad indices
    private readonly Particle[] particles;
    private readonly float[] scratch;                 // CPU vertex staging
    private readonly int[] order;                     // indices for optional depth sort
    private readonly float[] depthKey;                // sort keys, parallel to order
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
        depthKey = new float[maxParticles];

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

    // Spawn one particle with colour/size ramps (dropped silently if at capacity —
    // emission shouldn't crash). startColor→endColor and startSize→endSize lerp
    // across `life`.
    public void Emit(
        Vector3 position, Vector3 velocity,
        Vector4 startColor, Vector4 endColor,
        float startSize, float endSize, float life)
    {
        if (count >= maxParticles) return;
        particles[count++] = new Particle
        {
            Position = position,
            Velocity = velocity,
            StartColor = startColor,
            EndColor = endColor,
            StartSize = startSize,
            EndSize = endSize,
            Age = 0f,
            Life = life,
        };
    }

    // Integrate + age. Dead particles are swap-removed so the live set stays packed
    // at the front. `acceleration` is gravity/wind applied to every particle; `drag`
    // is a per-second velocity damping (0 = none) that makes sparks/vortices curl
    // and settle instead of flying straight forever.
    public void Update(float dt, Vector3 acceleration, float drag = 0f)
    {
        var damp = Math.Clamp(1f - drag * dt, 0f, 1f);
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
            p.Velocity = (p.Velocity + acceleration * dt) * damp;
            p.Position += p.Velocity * dt;
        }
    }

    // Expand the live set into camera-facing billboards (optionally depth-sorted
    // back-to-front for alpha blending), upload them as one transient-arena slice, and
    // record one DrawIndexed against the caller's `pipeline` with the caller's
    // `pushConstants` and `textures`. camRight/camUp orient the quads. What the draw
    // *means* is entirely the caller's: a plain viewProj push with no textures, or a
    // soft-particle pipeline whose push carries a fade and whose textures include the
    // scene depth — the batch is agnostic and just forwards them.
    public void Draw(
        RenderPassBuilder pass,
        PipelineHandle pipeline,
        Vector3 camRight,
        Vector3 camUp,
        Vector3 camPos,
        bool sortByDepth,
        byte[] pushConstants,
        IReadOnlyList<ShaderTextureBinding> textures)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (count == 0) return;

        var slice = ExpandBillboards(camRight, camUp, camPos, sortByDepth);
        pass.DrawIndexed(
            slice.Buffer,
            indexBuffer,
            pipeline,
            indexCount: count * IndicesPerParticle,
            Array.Empty<ShaderUniform>(),
            textures,
            pushConstants,
            indexOffset: 0,
            vertexOffset: 0,
            vertexBufferByteOffset: slice.ByteOffset);
    }

    // Sort (optional, back-to-front) + expand the live set into camera-facing quads and
    // upload them as one transient-arena slice.
    private TransientVertexSlice ExpandBillboards(Vector3 camRight, Vector3 camUp, Vector3 camPos, bool sortByDepth)
    {
        for (var i = 0; i < count; i++) order[i] = i;
        if (sortByDepth)
        {
            // Back-to-front so alpha blending composites correctly. Sort the index
            // array against a parallel key of NEGATED squared distance — ascending
            // sort then puts the farthest particle first, with no per-frame
            // comparer/closure allocation.
            for (var i = 0; i < count; i++)
            {
                depthKey[i] = -Vector3.DistanceSquared(particles[i].Position, camPos);
            }
            Array.Sort(depthKey, order, 0, count);
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
            // Interpolate colour + size across the particle's life.
            var life = MathF.Min(p.Age / p.Life, 1f);
            var c = Vector4.Lerp(p.StartColor, p.EndColor, life);
            var size = float.Lerp(p.StartSize, p.EndSize, life);
            for (var k = 0; k < 4; k++)
            {
                var world = p.Position + (camRight * corners[k].X + camUp * corners[k].Y) * size;
                scratch[f++] = world.X;
                scratch[f++] = world.Y;
                scratch[f++] = world.Z;
                scratch[f++] = c.X;
                scratch[f++] = c.Y;
                scratch[f++] = c.Z;
                scratch[f++] = c.W;
                scratch[f++] = corners[k].X * 0.5f + 0.5f;   // uv
                scratch[f++] = corners[k].Y * 0.5f + 0.5f;
            }
        }

        var bytes = MemoryMarshal.AsBytes(scratch.AsSpan(0, count * VertsPerParticle * FloatsPerVertex));
        return device.AllocVertices(bytes, VertexLayoutDescription.Stride, "particles.vertices");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyIndexBuffer(indexBuffer);
        // No vertex buffer to destroy — vertices ride the device-owned transient arena.
    }
}
