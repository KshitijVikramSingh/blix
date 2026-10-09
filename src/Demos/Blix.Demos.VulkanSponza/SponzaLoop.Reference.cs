using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --gi-reference (G toggles it at runtime): the reference view -- every pixel's indirect light path-traced on the GPU
// (Shaders/reference_trace.comp) and shown by the incident pass in place of the GI's answer, accumulating while the
// camera and the sun hold still. What the GI should look like, in the renderer itself; the probe reference's 24 x 24
// CPU points measure the same thing at points.
internal sealed partial class SponzaLoop
{
    private bool referenceView;
    private bool referenceAvailable;
    // --gi-reference-bounces N: path length (6: the 16-bounce CPU reference differs from 6 by well under its noise
    // indoors, where albedos under 0.6 shrink each bounce).
    private int referenceBounces = 6;
    private ShaderInterface referenceInterface = null!;
    private PassHandle referencePassHandle;
    private PipelineHandle referencePipeline;
    private GpuBufferHandle referenceAccum;
    private (int X, int Y) referenceSize;
    private Matrix4x4 referenceViewProj;
    private Vector3 referenceSun;
    private uint referenceFrame;
    private int referenceSamples;

    private void ReadReferenceArgs(AppArgs args)
    {
        referenceView = args.Flag("gi-reference");
        referenceAvailable = true;
        if (args.Int("gi-reference-bounces") is { } b) referenceBounces = Math.Clamp(b, 1, 32);
    }

    private void ToggleReference(IInputState input)
    {
        if (input[Key.G].Pressed)
        {
            referenceView = !referenceView;
            referenceSamples = 0;
            Console.WriteLine($"[VulkanSponza] reference view {(referenceView ? "on" : "off")} (G)");
        }
    }

    private void RecordReference(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height)
    {
        if (!referenceView || rayBlockBuffers.Length == 0 || !raySurfacesBaked) return;
        var size = ((width + 1) / 2, (height + 1) / 2);
        var restart = referenceSamples == 0 || viewProj != referenceViewProj || sunDirection != referenceSun;
        if (size != referenceSize)
        {
            referenceAccum = Own(device.CreateGpuBuffer(size.Item1 * size.Item2 * 16, name: "sponza.reference.accum"));
            referenceSize = size;
            restart = true;
        }
        if (restart) referenceSamples = 0;
        referenceViewProj = viewProj;
        referenceSun = sunDirection;
        var declared = referenceInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(referencePassHandle, new DispatchCommand(referencePipeline, (size.Item1 + 7) / 8, (size.Item2 + 7) / 8, 1,
            new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uTarget", new Vector4Uniform(new Vector4(width, height, size.Item1, size.Item2))),
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                new("uParams", new Vector4Uniform(new Vector4(referenceFrame, referenceBounces, restart ? 1f : 0f, referenceFrame % 4))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
            },
            Buffers: rayBlockBuffers.Append(new ShaderBufferBinding("ReferenceAccum", referenceAccum))
                .Where(b => declared.Contains(b.Name)).ToArray()));
        referenceFrame++;
        referenceSamples++;
    }

    // What the incident pass binds: the accumulation, or a placeholder.
    private (Vector4 Params, ShaderBufferBinding Buffer) ReferenceIncidentInputs()
    {
        if (referenceAccum.Equals(default(GpuBufferHandle)))
            referenceAccum = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.reference.none"));
        var on = referenceView && referenceSize.X > 0 && referenceSamples > 0;
        return (new Vector4(on ? 1f : 0f, referenceSize.X, referenceSize.Y, 0f), new ShaderBufferBinding("ReferenceAccum", referenceAccum));
    }

    private void ReportReference(DebugContext debug)
    {
        if (referenceView) debug.Values.Value("reference", string.Create(CultureInfo.InvariantCulture, $"{referenceSamples} frames (~{referenceSamples / 4} paths a pixel), {referenceBounces} bounces"));
    }
}
