using Blix.Graphics;
using Blix.Graphics.Primitives;

namespace Blix.Render;

// Owns the resources every PBR post-process needs: the four pipelines + the
// off-screen surfaces they target + a shared fullscreen quad mesh. Deliberately
// thin -- exposes the Material/Surface/Pipeline handles as properties so the
// demo continues to drive pass orchestration. The point is to box up
// initialization (which has nine cousin shader/pipeline/material trios that
// follow the same pattern) so future demos don't reproduce the boilerplate,
// not to prescribe the post-process composition order. The composition order
// is going to vary (some demos want fog before SSR, some after; some skip
// SSR entirely) and a one-method "RunPostProcess" would foreclose those
// choices.
//
// When the post-process pipeline matures into a real subsystem (per-frame
// scheduler, dependency tracking between passes, render-graph), it lands
// here -- the resource layout is correct already.
public sealed class PostProcessStack
{
    public Material FogMaterial { get; }
    public Material SsrMaterial { get; }
    public Material BloomDownMaterial { get; }
    public Material BloomUpMaterial { get; }
    public Material CompositeMaterial { get; }

    // Output target for the SSR pass. Separate from hdrSceneSurface so SSR
    // can sample the HDR scene without a read-write hazard. Demo composites
    // it back in via CompositeMaterial.
    public RenderSurface SsrSurface { get; }

    // Dual-filter bloom chain. Indices 0..N-1 = half-res, quarter-res, etc.
    // Downsample reads mip[i] -> writes mip[i+1]; upsample reverses with
    // Additive blend so the upsample tent accumulates.
    public IReadOnlyList<RenderSurface> BloomMips { get; }

    // Shared fullscreen quad. All five post-process pipelines render with
    // this mesh; demos sometimes have their own (a skybox quad, etc.) and
    // can use this one too.
    public Mesh FullscreenQuad { get; }

    private PostProcessStack(
        Material fog, Material ssr, Material bloomDown, Material bloomUp, Material composite,
        RenderSurface ssrSurface, RenderSurface[] bloomMips, Mesh fullscreenQuad)
    {
        FogMaterial = fog;
        SsrMaterial = ssr;
        BloomDownMaterial = bloomDown;
        BloomUpMaterial = bloomUp;
        CompositeMaterial = composite;
        SsrSurface = ssrSurface;
        BloomMips = bloomMips;
        FullscreenQuad = fullscreenQuad;
    }

    // Builds every post-process resource from a shaders directory. The
    // directory is expected to contain composite.vert + bloom_down.frag +
    // bloom_up.frag + ssr.frag + fog.frag + composite.frag, all written
    // against the engine's standard uniform set.
    public static PostProcessStack Create(
        IGraphicsDevice device,
        string shadersDir,
        string namePrefix,
        int bloomMipCount = 4)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shadersDir);
        ArgumentNullException.ThrowIfNull(namePrefix);
        if (bloomMipCount < 1) throw new ArgumentOutOfRangeException(nameof(bloomMipCount));

        Material Build(string fragName, string passName, IReadOnlyList<BlendState> blends)
        {
            var sources = ShaderLoader.LoadVertexFragment(
                Path.Combine(shadersDir, "composite.vert"),
                Path.Combine(shadersDir, fragName));
            var shader = device.CreateShaderProgram(sources);
            var pipeline = device.CreatePipeline(
                new PipelineDescription(
                    shader,
                    VertexPositionTexture.Layout,
                    PrimitiveTopology.Triangles,
                    DepthState.LessEqualNoWrite,
                    RasterizerState.NoCulling,
                    blends),
                name: $"{namePrefix}.{passName}");
            return new Material($"{namePrefix}.{passName}", pipeline);
        }

        // Fog targets hdrSceneSurface, which has two color attachments
        // (HDR + material G-buffer). Per-attachment additive blends so the
        // god-ray inscatter adds onto the scene without trashing the
        // material-roughness output.
        var fog = Build("fog.frag", "fog",
            new[] { BlendState.Additive, BlendState.Additive });

        var ssr = Build("ssr.frag", "ssr", new[] { BlendState.Disabled });
        var bloomDown = Build("bloom_down.frag", "bloom.down", new[] { BlendState.Disabled });
        var bloomUp = Build("bloom_up.frag", "bloom.up", new[] { BlendState.Additive });
        var composite = Build("composite.frag", "composite", new[] { BlendState.Disabled });

        var ssrSurface = device.CreateRenderSurface(new RenderSurfaceDescription(
            Name: $"{namePrefix}.ssr",
            Size: new MatchDefaultRenderSurfaceSize(1.0f),
            ColorAttachments: new[]
            {
                new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
            },
            Depth: null));

        var bloomMips = new RenderSurface[bloomMipCount];
        for (var i = 0; i < bloomMipCount; i++)
        {
            var scale = 1.0f / MathF.Pow(2.0f, i + 1);
            bloomMips[i] = device.CreateRenderSurface(new RenderSurfaceDescription(
                Name: $"{namePrefix}.bloom.mip{i}",
                Size: new MatchDefaultRenderSurfaceSize(scale),
                ColorAttachments: new[]
                {
                    new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
                },
                Depth: null));
        }

        var vb = device.CreateVertexBuffer(
            VertexPositionTexture.CreateBufferData(Blix.Graphics.Primitives.FullscreenQuad.Vertices),
            name: $"{namePrefix}.post.quad.vb");
        var ib = device.CreateIndexBuffer(
            Blix.Graphics.Primitives.FullscreenQuad.Indices,
            name: $"{namePrefix}.post.quad.ib");
        var quad = new Mesh(
            $"{namePrefix}.post.quad", vb, ib,
            Blix.Graphics.Primitives.FullscreenQuad.Indices.Length,
            Blix.Geometry.Bounds3.Empty);

        return new PostProcessStack(fog, ssr, bloomDown, bloomUp, composite,
            ssrSurface, bloomMips, quad);
    }
}
