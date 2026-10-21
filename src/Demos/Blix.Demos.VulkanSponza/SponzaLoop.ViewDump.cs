using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The view dump (the texel picker's "Dump view", --view-dump): everything a frame's GI is made of at the scale it is seen,
// written raw for tools/gi-model/view_dump.py -- per pixel the incident field, the pre-pass normal, the atlas texel it
// reads and the per-pixel path trace (--gi-reference, G) with its moments; per texel its record, its parts, its resolved
// light, who wrote it, its rays, its patch and its cooked visibilities; meta.json the camera, the sun, the seed and the
// sizes. Patterns a pixel's picker cannot show at once -- texels that settled at different counts side by side -- are
// read off the whole view.
internal sealed partial class SponzaLoop
{
    private bool viewDumpPending, viewDumpAtLanding, viewDumpDone;
    // Frames left before the dump reads back, the reference view's display held off meanwhile: with it on the incident
    // field is the path trace (incident_clipmap.frag), and the dump wants the GI's own.
    private int viewDumpWaiting;
    private int viewDumpAtFrame = -1;
    private string? viewDumpLast;
    private bool viewDumpReady;
    private static readonly string ViewDumpRoot = Environment.GetEnvironmentVariable("BLIX_DUMP_DIR") ?? "/private/tmp/blix-dbg/dumps";

    // RenderFrame's end: a dump asked for (the button), or --view-dump's once the light has landed, as --texel-pick logs.
    private void DumpViewIfDue()
    {
        if (viewDumpWaiting > 0 && --viewDumpWaiting > 0) return;
        if (viewDumpWaiting == 0 && !viewDumpReady)
        {
            var landed = viewDumpAtLanding && !viewDumpDone && texelResting && landedFrame >= 0 && framesRendered >= landedFrame + 60
                && (viewDumpAtFrame < 0 || postLoadFrames >= viewDumpAtFrame);
            if (!viewDumpPending && !landed) return;
            viewDumpPending = false;
            if (landed) viewDumpDone = true;
            // The reference view's display and any texel view held off a few frames: the field read is then the GI's.
            if (referenceView || texelView != 0) { viewDumpWaiting = 3; viewDumpReady = true; return; }
        }
        viewDumpReady = false;
        try { viewDumpLast = DumpView(); Console.WriteLine($"[VulkanSponza] view dumped: {viewDumpLast}"); }
        catch (Exception e) { viewDumpLast = "failed: " + e.Message; Console.WriteLine($"[VulkanSponza] view dump failed: {e.Message}"); }
    }

    private string DumpView()
    {
        var dir = Path.Combine(ViewDumpRoot, string.Create(CultureInfo.InvariantCulture, $"view-{DateTime.Now:yyyyMMdd-HHmmss}-{framesRendered}"));
        Directory.CreateDirectory(dir);
        device.WaitIdle();
        var files = new Dictionary<string, object>();
        void Texture(string name, TextureHandle handle)
        {
            var bytes = device.ReadTexture(handle, out var w, out var h, out var format);
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            files[name] = new { width = w, height = h, format = format.ToString(), bytes = bytes.Length };
        }
        void Buffer(string name, GpuBufferHandle buffer, long bytes)
        {
            if (buffer.Equals(default(GpuBufferHandle)) || bytes <= 0) return;
            var data = device.ReadGpuBuffer(buffer, 0, (int)bytes);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            files[name] = new { bytes = data.Length };
        }
        Texture("incident.rgba16f", graph.GetColorTexture(incidentHandle));
        Texture("normal.rgba16f", graph.GetColorTexture(SampleablePrepassNormal));
        if (lightmapEnabled) Texture("lightmap_texel.r32u", graph.GetColorTexture(lightmapTexelHandle));
        if (!lightmapIndexTexture.Equals(default(TextureHandle))) Texture("lightmap_index.r32u", lightmapIndexTexture);
        Buffer("texels.f32x8", texelBuffer, (long)texelCount * 32);
        Buffer("sky.u32x2", splitSkyBuffer, (long)texelCount * 8);
        Buffer("sun.u32x2", splitSunBuffer, (long)texelCount * 8);
        Buffer("resolved.u32x2", texelResolvedBuffer, (long)texelCount * 8);
        Buffer("provenance.u8", texelProvenanceBuffer, Math.Min(texelProvenanceBytes, (texelCount + 3) / 4 * 4));
        Buffer("rays.u16", texelRaysBuffer, Math.Min(texelRaysBytes, (texelCount + 1) / 2 * 4));
        Buffer("patch.i32", liveTexelPatch, (long)texelCount * 4);
        // The cooked visibilities (a byte a texel, 0 none, else 1 + 254 v): contact (the fine flag's ratio), its short
        // reach, the sky's (by its square root).
        var visBytes = (texelCount + 3) / 4 * 4;
        Buffer("contact.u8", texelAoBuffer, visBytes);
        Buffer("contact_short.u8", texelAoShortBuffer, visBytes);
        Buffer("skyvis.u8", texelSkyVisBuffer, visBytes);
        // --ray-capture's rays (SponzaLoop.RayCapture.cs): the count's 16 bytes, then 32 a ray.
        var (captured, capturedBytes) = RayCaptureForDump();
        Buffer("rays_captured.u32x8", captured, capturedBytes);
        // The light strategy's inputs (texel_nodes_rays.comp): each texel's guide patch (+ 2; 1 none), the patches'
        // light lists (LIGHT_K (index bits, cumulative share) a patch) and the patches (pos, normal, indirect).
        Buffer("guide_patch.u32", sunNodeGuidePatch, (long)texelCount * 4);
        if (cookedPacked is { } packed) Buffer("light_lists.f32x2", liveLights, (long)(packed.Length / 3) * LightListSize * 8);
        Buffer("patches.f32x12", cookedPatchBuffer, cookedPacked is { } cp ? (long)cp.Length * 16 : 0);
        if (referenceView || referenceSamples > 0)
        {
            Buffer("reference.f32x4", referenceAccum, (long)referenceSize.X * referenceSize.Y * 16);
            Buffer("reference_moments.f32", referenceMoments, (long)referenceSize.X * referenceSize.Y * 4);
        }
        var irr = EffectiveSunIrradiance;
        var meta = new
        {
            frame = framesRendered,
            camera = new[] { cameraPosition.X, cameraPosition.Y, cameraPosition.Z },
            viewProj = new[] { viewProj.M11, viewProj.M12, viewProj.M13, viewProj.M14, viewProj.M21, viewProj.M22, viewProj.M23, viewProj.M24,
                               viewProj.M31, viewProj.M32, viewProj.M33, viewProj.M34, viewProj.M41, viewProj.M42, viewProj.M43, viewProj.M44 },
            sunToward = new[] { -sunDirection.X, -sunDirection.Y, -sunDirection.Z },
            sunIrradiance = new[] { irr.X, irr.Y, irr.Z },
            texelCount,
            lightmapFirstRecord,
            lightmapAtlas = new[] { lightmapAtlasWidth, lightmapAtlasHeight },
            reference = new { width = referenceSize.X, height = referenceSize.Y, samples = referenceSamples, on = referenceView },
            pick = new[] { texelPickFraction.X, texelPickFraction.Y },
            seed = Environment.GetEnvironmentVariable("BLIX_GATHER_SEED") ?? "0",
            texelView = TexelViewNames[Math.Clamp(texelView, 0, TexelViewNames.Length - 1)],
            fieldIsGi = true,
            files,
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        return dir;
    }
}
