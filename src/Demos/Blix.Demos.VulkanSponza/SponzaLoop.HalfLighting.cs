using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// RenderSettings.HalfResLighting (the debug overlay's Render group): the frame's lighting at half resolution.
//
//   rt-shadow-rays        one sun ray per half-res pixel (SponzaLoop.RtShadows), a quarter of the rays
//   incident-light-half   the incident field per half-res pixel (incident_clipmap.frag), a quarter of its texel lookups
//   lighting-upsample     both back to full resolution along the depth and normals (lighting_upsample.frag), into the
//                         targets the lit pass reads, the sun's visibility in the gradient's w
//
// Each half-res pixel stands for ONE full-res pixel of its 2x2, read exactly (the pre-pass's depth and normal there),
// never an average of four: the sample is a real point on a real surface, and the upsample can ask whether a
// full-res pixel is on it. Which of the four turns each frame (LightingSubpixel), so over four frames every full-res
// pixel has had its own sample and TAA, accumulating, holds full-resolution lighting where the camera is still; in
// motion, where history is refused, it is the half-res answer, upsampled.
//
// The texel mark follows (SponzaLoop.Texels.cs): it queues the texels of pixels the incident pass reads this frame.
internal sealed partial class SponzaLoop
{
    // The 2x2's pixels in an order that alternates diagonals, so two consecutive frames never share a row or column.
    private static readonly (int X, int Y)[] LightingSubpixels = { (0, 0), (1, 1), (1, 0), (0, 1) };

    private (int X, int Y) LightingSubpixel => LightingSubpixels[lightingFrame & 3u];

    // The half-res grid, as the graph sizes MatchSwapchainGraphSize(0.5f) targets (rounded down, as the AO's).
    private static (int Width, int Height) HalfLightingSize(int width, int height) =>
        (Math.Max(1, width / 2), Math.Max(1, height / 2));

    private void RecordLightingUpsample(Matrix4x4 invProjection, Matrix4x4 invView)
    {
        var sub = LightingSubpixel;
        // The mask the lit pass would bind: this frame's half-res one, the pending one (the sun off), or the placeholder
        // (no traced shadows: the lit pass asks the cascades and never reads the w this writes).
        var mask = RtShadowMask;
        graph.Pass(lightingUpsamplePassHandle, scope => fullscreen.Draw(
            scope, lightingUpsamplePipeline,
            new[]
            {
                new ShaderTextureBinding("uIncidentHalf", graph.GetColorTexture(incidentHalfHandle)),
                new ShaderTextureBinding("uIncidentGradientHalf", graph.GetColorTexture(incidentGradientHalfHandle)),
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uHalf", new Vector4Uniform(new Vector4(1f, sub.X, sub.Y, 0f))),
            },
            buffers: new[] { new ShaderBufferBinding("RtShadowMask", mask) }));
    }
}
