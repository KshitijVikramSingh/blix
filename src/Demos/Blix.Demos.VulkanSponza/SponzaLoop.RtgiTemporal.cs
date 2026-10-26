using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --rtgi-temporal: the indirect light accumulated over frames, the light alone (rtgi_temporal.frag) -- never the image
// (TAA stays off). With it the half-res lighting's upsample writes raw targets, rtgi-temporal reprojects last frame's
// accumulated light onto this frame's pixels (kept where last frame saw the same point), blends this frame's in at one
// over the frames held (at most --rtgi-temporal-frames, 16), and writes the targets the lit pass reads; rtgi-history then
// copies it, and each pixel's distance and frames held, for the next frame (a graph history read is of the target as the
// last frame left it, so the pass that reads it cannot write it). The half-res sample's turn through its 2x2 is gathered
// over the held frames. A sun turn starts it over, as it cuts the cache's history (SponzaLoop.Rtgi.cs).
internal sealed partial class SponzaLoop
{
    // On by default (--rtgi-no-temporal: off). Its slow history clamped into a fast one's box (NRD's anti-lag):
    // --rtgi-temporal-fast frames (6), --rtgi-temporal-clamp standard deviations (2; 0: none).
    private bool rtgiTemporal = true;
    private float rtgiTemporalFrames = 16f, rtgiTemporalFast = 6f, rtgiTemporalClamp = 2f;
    private GraphResourceHandle rtgiRawIncidentHandle, rtgiRawGradientHandle, rtgiStateHandle, rtgiHistoryHandle, rtgiHistoryStateHandle,
        rtgiFastHandle, rtgiFastHistoryHandle;
    private ShaderInterface rtgiTemporalInterface = null!, rtgiHistoryInterface = null!;
    private PassHandle lightingUpsampleRawPassHandle, rtgiTemporalPassHandle, rtgiHistoryPassHandle;
    private PipelineHandle lightingUpsampleRawPipeline, rtgiTemporalPipeline, rtgiHistoryPipeline;
    private Matrix4x4 rtgiTemporalPrevViewProj;
    private Vector3 rtgiTemporalPrevCamera, rtgiTemporalSun;
    private bool rtgiTemporalHistory;

    private bool RtgiTemporalNow => rtgiTemporal && RtgiReadoutReady;

    private void ParseRtgiTemporal(Blix.Core.AppArgs args)
    {
        // Off by default: Cornell's two-seed noise and error no better with it (screen-space smoothing alone, 2026-10-26).
        rtgiTemporal = false;
        if (args.Flag("rtgi-no-temporal")) rtgiTemporal = false;
        if (args.Flag("rtgi-temporal")) rtgiTemporal = true;
        if (args.Float("rtgi-temporal-frames") is { } f) rtgiTemporalFrames = MathF.Max(1f, f);
        if (args.Float("rtgi-temporal-fast") is { } ff) rtgiTemporalFast = MathF.Max(1f, ff);
        if (args.Float("rtgi-temporal-clamp") is { } tc) rtgiTemporalClamp = MathF.Max(0f, tc);
    }

    // Setup, after lighting-upsample: the raw upsample, the accumulation and the copy.
    private void DeclareRtgiTemporalPasses(Func<string, string, ShaderInterface> reflect, MatchSwapchainGraphSize fullSize)
    {
        rtgiRawIncidentHandle = graph.ColorTarget("rtgi-raw-incident", TextureFormat.Rgba16F, fullSize);
        rtgiRawGradientHandle = graph.ColorTarget("rtgi-raw-gradient", TextureFormat.Rgba16F, fullSize);
        rtgiStateHandle = graph.ColorTarget("rtgi-temporal-state", TextureFormat.Rgba16F, fullSize);
        rtgiHistoryHandle = graph.ColorTarget("rtgi-history", TextureFormat.Rgba16F, fullSize);
        rtgiHistoryStateHandle = graph.ColorTarget("rtgi-history-state", TextureFormat.Rgba16F, fullSize);
        rtgiFastHandle = graph.ColorTarget("rtgi-fast", TextureFormat.Rgba16F, fullSize);
        rtgiFastHistoryHandle = graph.ColorTarget("rtgi-fast-history", TextureFormat.Rgba16F, fullSize);
        lightingUpsampleRawPassHandle = graph.GraphicsPass("lighting-upsample-raw")
            .Target(rtgiRawIncidentHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiRawGradientHandle, LoadOp.Clear, StoreOp.Store)
            .Read(incidentHalfHandle).Read(incidentGradientHalfHandle).Read(SampleableSceneDepth).Read(SampleablePrepassNormal)
            .Shader(lightingUpsampleInterface).Handle;
        rtgiTemporalInterface = reflect("present.vert", "rtgi_temporal.frag");
        rtgiTemporalPassHandle = graph.GraphicsPass("rtgi-temporal")
            .Target(incidentHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiStateHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiFastHandle, LoadOp.Clear, StoreOp.Store)
            .Read(rtgiRawIncidentHandle).Read(rtgiRawGradientHandle).Read(SampleableSceneDepth)
            .ReadHistory(rtgiHistoryHandle).ReadHistory(rtgiHistoryStateHandle).ReadHistory(rtgiFastHistoryHandle)
            .Shader(rtgiTemporalInterface).Handle;
        rtgiHistoryInterface = reflect("present.vert", "rtgi_history.frag");
        rtgiHistoryPassHandle = graph.GraphicsPass("rtgi-history")
            .Target(rtgiHistoryHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiHistoryStateHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiFastHistoryHandle, LoadOp.Clear, StoreOp.Store)
            .Read(incidentHandle).Read(rtgiStateHandle).Read(rtgiFastHandle)
            .Shader(rtgiHistoryInterface).Handle;
    }

    private void CreateRtgiTemporalPipelines(string shaderDir, byte[] presentVertSpv)
    {
        lightingUpsampleRawPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "lighting_upsample.frag.spv")), lightingUpsampleInterface, "lighting_upsample_raw")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, lightingUpsampleRawPassHandle, "lighting_upsample_raw");
        rtgiTemporalPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_temporal.frag.spv")), rtgiTemporalInterface, "rtgi_temporal")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled, BlendState.Disabled, BlendState.Disabled }, rtgiTemporalPassHandle, "rtgi_temporal");
        rtgiHistoryPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_history.frag.spv")), rtgiHistoryInterface, "rtgi_history")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled, BlendState.Disabled }, rtgiHistoryPassHandle, "rtgi_history");
    }

    // After the raw upsample: accumulate into the lit pass's targets, then copy for the next frame.
    private void RecordRtgiTemporal()
    {
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        var towardSun = -Vector3.Normalize(sunDirection);
        // A sun turn starts it over (the cache cuts its history then too).
        var historyOk = rtgiTemporalHistory && Vector3.Dot(towardSun, rtgiTemporalSun) > MathF.Cos(0.02f * MathF.PI / 180f);
        var prevViewProj = rtgiTemporalPrevViewProj;
        var prevCamera = rtgiTemporalPrevCamera;
        graph.Pass(rtgiTemporalPassHandle, scope => fullscreen.Draw(
            scope, rtgiTemporalPipeline,
            new[]
            {
                new ShaderTextureBinding("uRaw", graph.GetColorTexture(rtgiRawIncidentHandle)),
                new ShaderTextureBinding("uRawGradient", graph.GetColorTexture(rtgiRawGradientHandle)),
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uHistory", graph.GetColorTexture(rtgiHistoryHandle)),
                new ShaderTextureBinding("uHistoryState", graph.GetColorTexture(rtgiHistoryStateHandle)),
                new ShaderTextureBinding("uFastHistory", graph.GetColorTexture(rtgiFastHistoryHandle)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uPrevViewProj", new Matrix4x4Uniform(prevViewProj)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uPrevCamera", new Vector4Uniform(new Vector4(prevCamera, 0f))),
                new("uParams", new Vector4Uniform(new Vector4(rtgiTemporalFrames, historyOk ? 1f : 0f, rtgiTemporalFast, rtgiTemporalClamp))),
            }));
        graph.Pass(rtgiHistoryPassHandle, scope => fullscreen.Draw(
            scope, rtgiHistoryPipeline,
            new[]
            {
                new ShaderTextureBinding("uIncident", graph.GetColorTexture(incidentHandle)),
                new ShaderTextureBinding("uState", graph.GetColorTexture(rtgiStateHandle)),
                new ShaderTextureBinding("uFast", graph.GetColorTexture(rtgiFastHandle)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[] { new("uUnused", new Vector4Uniform(Vector4.Zero)) }));
        rtgiTemporalPrevViewProj = viewProjJittered;
        rtgiTemporalPrevCamera = cameraPosition;
        rtgiTemporalSun = towardSun;
        rtgiTemporalHistory = true;
    }
}
