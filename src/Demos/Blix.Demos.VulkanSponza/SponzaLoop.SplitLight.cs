using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --split-light: the texels' light in two parts, each solved live and cached on its own -- the sky's (solved once for
// the sky) and the sun's (solved for a sun's direction, stored per unit of its irradiance). The resting light is their
// sum (texel_compose.comp): the sky's times its scale, the sun's times the sun's irradiance now. So a sun change
// re-solves only the sun's part (or reads it), a stronger sun or a brighter sky is a compose and no solve, and the sky's
// part serves every sun. Each part is a column of the sun nodes' cook (SponzaLoop.SunNodes.cs): one trace, every texel's
// gather shaded for the parts asked, the patches' light solved for each part apart (SunNodesSolve.cs: components).
// Light is linear: Cornell's sky and sun parts summed to the two together to +0.0001% (2026-10-13). Under
// --texel-filter the composed light is filtered as the gather's is (texel_compose.comp, mode 1).
internal sealed partial class SponzaLoop
{
    private bool splitLight;
    // The sky the sky's part is for (the probe loaded, else the procedural sky): in its cache's key.
    private string skyIdentity = "procedural";
    // 2: refined until settled (SponzaLoop.SunNodesRefine.cs). 3: the cells' detail cooked beside each part (SplitDetail).
    // 4: a hit with no patch takes its texel's known bounce, not 0 (texel_nodes_shade.glsl).
    // 5: the light strategy and multiple importance sampling (texel_nodes_rays.comp).
    // 6: the far field (SponzaLoop.FarField.cs): past its reach a texel reads its patch's record.
    // 7: a hit's lookup skips buried texels (texel_nodes.glsl nodeSetAt): their patch was seen from inside the wall.
    // 8: records where a solid touches their face move off it (SponzaLoop.Lightmap.cs LiftOffContacts).
    private const int SplitLightVersion = 8;
    private ShaderInterface texelComposeInterface = null!;
    private PassHandle texelComposePass, texelComposeFilterPass;
    private PipelineHandle texelComposePipeline;
    private GpuBufferHandle splitSkyBuffer, splitSunBuffer, splitLiveBuffer, splitComposedBuffer;
    private bool splitSkyUp, splitSunUp, splitComposeDirty;
    private long splitComposeAgainFrame = -1;
    private Vector3 splitComposedSky = new(float.NaN), splitComposedSun = new(float.NaN);
    // The cook under way for the split (its columns' cache paths, by column), and a sun asked for while it ran.
    private string?[] splitCookPaths = Array.Empty<string?>();
    private bool splitCooking;
    private Vector3? splitSunWanted;
    private Vector3 splitSunSolved = new(float.NaN);

    // --split-refine-min-share S (0.1): the refining rounds stop after one that settles under this share of what it
    // traced (SponzaLoop.SunNodesRefine.cs).
    private float splitRefineMinShare = 0.1f;

    private void ReadSplitLightArgs(AppArgs args)
    {
        splitLight = args.Flag("split-light");
        if (args.Float("split-refine-min-share") is { } share) splitRefineMinShare = Math.Clamp(share, 0f, 1f);
    }

    private void DeclareSplitLightPass(Func<string[], ShaderInterface> reflect)
    {
        texelComposeInterface = reflect(new[] { "texel_compose.comp" });
        texelComposePass = graph.ComputePass("texel-compose").Shader(texelComposeInterface).Handle;
        // The filter over the composed light (mode 1), a pass of its own: it reads the neighbours mode 0 wrote.
        texelComposeFilterPass = graph.ComputePass("texel-compose-filter").Shader(texelComposeInterface).Handle;
        DeclareTexelVisiblePass(reflect);
    }

    private void CreateSplitLightPipeline(string shaderDir)
    {
        texelComposePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_compose.comp.spv")), texelComposeInterface, "texel_compose")), "texel_compose"));
        CreateTexelVisiblePipeline(shaderDir);
    }

    // A part's cache: the transport's key, which part (and for the sun its direction), and how it was solved.
    private string SplitPathFor(string transportKey, Vector3? toSun)
    {
        var part = toSun is { } d ? string.Create(CultureInfo.InvariantCulture, $"sun {d.X:F5},{d.Y:F5},{d.Z:F5} per unit irradiance") : "sky";
        // The sun's part through sheets that transmit (texel_nodes_solve.comp; --cloth-transmission): its factor, the
        // bounce light alone through them (the sun through them is the frame's).
        if (toSun is not null && clothTransmission >= 0f) part += string.Create(CultureInfo.InvariantCulture, $";cloth {clothTransmission:G3} x hue capped, bounce");
        var spec = string.Create(CultureInfo.InvariantCulture,
            $"{transportKey};{part};sky {skyIdentity};rays {sunNodeRays} {(sunNodeGuide ? "guided" : "cosine")}{(sunNodeLights ? " lights" : "")};lift {sunNodeLift:G4};lookup {sunNodePlane:G3} {sunNodeSigma:G3};discover {sunNodeDiscoverStride};unknown {sunNodeUnknownSun:G3};settled {texelNoise:G6} {texelMaxRays} stop {splitRefineMinShare:G3} dark {texelDarkFloor:G3};patches seen of {PatchCandidates};buried {LightmapBuriedRule}{FarKey};v{SplitLightVersion}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec)))[..16].ToLowerInvariant();
        return Path.ChangeExtension(LightCachePath(transportKey), $".{(toSun is null ? "sky" : "sun")}-{hash}");
    }

    // UploadTexels with no single light cached, under --split-light: each part read from its cache, or cooked (both
    // from one trace when both are missing). True: the split answers (no light bake).
    private bool BeginSplitLight()
    {
        if (!splitLight || lightBakeKey is null || texelLiveIds is null) return false;
        var live = texelLiveIds;
        splitLiveBuffer = Own(device.CreateGpuBuffer(live.Length * 4, MemoryMarshal.AsBytes(live.AsSpan()), "sponza.split.live"));
        if (texelResolvedBuffer.Equals(default(GpuBufferHandle)))
            texelResolvedBuffer = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.texels.resolved"));
        // Composed, then filtered into the resolved light (texel_compose.comp); without the filter, composed straight in.
        splitComposedBuffer = texelFilter ? Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.split.composed")) : texelResolvedBuffer;
        var toSun = transportRelightSun.ToSun;
        if (liveTexels)
        {
            // The live light (SponzaLoop.LivePatchLight.cs): both columns gathered for the lights as they are, read from
            // no cache and written to none -- the sky's part and the sun's, as the cook lands them.
            StartSplitCook(new List<(Vector3? ToSun, string Path)> { (null, ""), (toSun, "") });
            splitCookPaths = new string?[] { null, null };
            Console.WriteLine("[VulkanSponza] split light: live -- the sky's part and the sun's gathered from the live patch light, no light cached.");
            return true;
        }
        var skyPath = SplitPathFor(lightBakeKey, null);
        var sunPath = SplitPathFor(lightBakeKey, toSun);
        if (ReadResolved(skyPath, texelCount) is { } sky) { UploadSplitPart(sky, sunPart: false); ReadPartDetail(skyPath, sunPart: false); }
        if (ReadResolved(sunPath, texelCount) is { } sun) { UploadSplitPart(sun, sunPart: true); ReadPartDetail(sunPath, sunPart: true); splitSunSolved = toSun; }
        var columns = new List<(Vector3? ToSun, string Path)>();
        if (!splitSkyUp) columns.Add((null, skyPath));
        if (!splitSunUp) columns.Add((toSun, sunPath));
        if (columns.Count > 0) StartSplitCook(columns);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] split light: sky {(splitSkyUp ? "read" : "to cook")}, sun {(splitSunUp ? "read" : "to cook")}{(columns.Count > 0 ? "; the texels gather as they are seen until the cook lands" : "")} ({Path.GetFileName(skyPath)}, {Path.GetFileName(sunPath)})."));
        return true;
    }

    // The sun nodes' cook, its columns these parts.
    private void StartSplitCook(List<(Vector3? ToSun, string Path)> columns)
    {
        splitCooking = true;
        sunProvisionalDone = false;
        BeginSplitCookLanding();
        splitCookPaths = columns.Select(c => (string?)c.Path).ToArray();
        sunNodeSuns = columns.Select(_ => (0f, 0f)).ToArray();
        sunNodeKinds = columns.Select(c => c.ToSun is null ? 2 : 1).ToArray();
        sunNodeDirsAsked = columns.Select(c => c.ToSun ?? Vector3.UnitY).ToArray();
        sunNodeKeep = columns.Count;
        sunNodeBootPath = null;
        sunNodeStage = SunNodeStage.Waiting;
    }

    // FinishSunNodes under the split: each column saved to its cache (the sun's per unit of irradiance) and up.
    private void FinishSplitCook()
    {
        var irradiance = EffectiveSunIrradiance;
        // --live-texels: every slice landed in the parts' buffers as it came (LandSplitSliceLive) -- and a view's gather
        // traced only some texels, so the results arrays are not the parts: nothing to upload.
        for (var k = 0; k < splitCookPaths.Length && !liveTexels; k++)
        {
            var words = sunNodeResults[k];
            var sunPart = sunNodeKinds[k] == 1;
            if (sunPart)
            {
                var inverse = new Vector3(1f / MathF.Max(irradiance.X, 1e-6f), 1f / MathF.Max(irradiance.Y, 1e-6f), 1f / MathF.Max(irradiance.Z, 1e-6f));
                Parallel.For(0, texelCount, i =>
                {
                    if ((words[2 * i + 1] & 0x10000u) != 0u) words[2 * i] = PackRgb9e5(UnpackRgb9e5(words[2 * i]) * inverse);
                });
                splitSunSolved = new Vector3(sunNodeDirsAsked![k].X, sunNodeDirsAsked[k].Y, sunNodeDirsAsked[k].Z);
            }
            if (splitCookPaths[k] is { } path) WriteResolved(path, words);
            UploadSplitPart(words, sunPart);
        }
        FlushSplitDetail();
        if (!liveTexels) SaveSplitDetail();
        splitCooking = false;
        sunNodeStage = SunNodeStage.Off;
        // --live-texels keeps the cook's setup for the next sun (LiveRetarget): it is the geometry's.
        if (!liveTexels)
        {
            sunNodeResults = Array.Empty<uint[]>();
            sunNodeDirsAsked = null;
            ReleaseSunNodeCook();
        }
        Console.WriteLine("[VulkanSponza] split light: the cook landed; the texels rest on the sky's part and the sun's.");
        // The sky's part gathered whole: a sun change gathers the sun's alone from here (SponzaLoop.SliceResolve.cs).
        if (liveTexels) liveSkyGathered = true;
        ReleaseRayGeometry();
        ReleaseGatherScratch();
        // A sun asked for while the cook ran: its part next.
        if (splitSunWanted is { } wanted && Vector3.Distance(wanted, splitSunSolved) > 1e-5f) SplitSunChanged(wanted);
    }

    private void UploadSplitPart(uint[] words, bool sunPart)
    {
        var buffer = Own(device.CreateGpuBuffer(words.Length * 4, MemoryMarshal.AsBytes(words.AsSpan()), sunPart ? "sponza.split.sun" : "sponza.split.sky"));
        if (sunPart)
        {
            if (!splitSunBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(splitSunBuffer);
            splitSunBuffer = buffer;
            splitSunUp = true;
            splitSunWords = words;   // what landing writes a relight's slices into (SponzaLoop.SplitLanding.cs)
        }
        else
        {
            if (!splitSkyBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(splitSkyBuffer);
            splitSkyBuffer = buffer;
            splitSkyUp = true;
            splitSkyWords = words;
        }
        splitComposeDirty = true;
    }

    // FollowSun, a new sun's patches solved: under the split, its sun part read or cooked -- the sky's stays.
    private void SplitSunChanged(Vector3 toSun)
    {
        splitSunWanted = toSun;
        // A sun-only cook already tracing gives way to the newest sun (SponzaLoop.SunProvisional.cs); any other waits.
        if (splitCooking && !RestartSunCook()) return;
        if (lightBakeKey is null) return;
        var path = SplitPathFor(lightBakeKey, toSun);
        if (ReadResolved(path, texelCount) is { } sun)
        {
            UploadSplitPart(sun, sunPart: true);
            ReadPartDetail(path, sunPart: true);
            splitSunSolved = toSun;
            Console.WriteLine("[VulkanSponza] split light: the new sun's part read from its cache.");
            return;
        }
        StartSplitCook(new List<(Vector3?, string)> { (toSun, path) });
        Console.WriteLine("[VulkanSponza] split light: the new sun's part to cook (the sky's kept); the old sun's shows until it lands.");
    }

    // RecordTexels, each frame: the resting light composed again when a part landed or a scale changed.
    private void RecordSplitCompose()
    {
        if (!splitLight || !splitSkyUp) return;
        var sky = new Vector3(render.SkyStrength);
        var sun = EffectiveSunIrradiance;
        // A live write (a scatter, an in-place provisional) is a pass of this frame that may run after the compose: composed
        // again the next frame too.
        if (!splitComposeDirty && sky == splitComposedSky && sun == splitComposedSun && splitComposeAgainFrame < framesRendered) return;
        splitComposeDirty = false;
        MarkIncidentDirty();   // the texels' light changes: the incident pass recomputes every pixel (SponzaLoop.IncidentReuse.cs)
        splitComposedSky = sky;
        splitComposedSun = sun;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] split light: composed at frame {framesRendered} (post-load {postLoadFrames}): sky {sky.X:G4}, sun {sun.X:G4},{sun.Y:G4},{sun.Z:G4}{(splitSunUp ? "" : " (no sun part yet)")}."));
        var live = texelLiveIds!.Length;
        foreach (var mode in texelFilter ? new[] { 0, 1 } : new[] { 0 })
            graph.Dispatch(mode == 0 ? texelComposePass : texelComposeFilterPass, new DispatchCommand(texelComposePipeline, (live + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uCompose", new Vector4Uniform(new Vector4(live, mode, 0f, 0f))),
                    new("uSky", new Vector4Uniform(new Vector4(sky, 0f))),
                    new("uSun", new Vector4Uniform(new Vector4(sun, splitSunUp ? 1f : 0f))),
                    new("uTexelGrid", new Vector4Uniform(texelGrid)),
                    new("uTexelParams", new Vector4Uniform(new Vector4(texelCapacity, 0f, 0f, 0f))),
                    new("uLightmap", new Vector4Uniform(LightmapUniform)),
                    new("uEdge", new Vector4Uniform(new Vector4(texelFilterSymmetric ? 1f : 0f, texelFilterArea ? 1f : 0f, 0f, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("SunNodesLive", splitLiveBuffer),
                    new ShaderBufferBinding("TexelSky", splitSkyBuffer),
                    new ShaderBufferBinding("TexelSun", splitSunUp ? splitSunBuffer : splitSkyBuffer),
                    new ShaderBufferBinding("TexelResolved", texelResolvedBuffer),
                    new ShaderBufferBinding("TexelComposed", splitComposedBuffer),
                    new ShaderBufferBinding("TexelHash", texelHashBuffer),
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("LightmapIndexMap", LightmapIndexMap()),
                }.Concat(SeamBindings()).Concat(DetailBindings()).ToArray()));
        texelResting = true;
        texelResolveCurrent = true;
        texelsSettled = true;
        texelSettledEpoch = texelEpoch;
        if (!texelGatherFreed) texelResolvedFrame = texelFrame;   // RestTexels lets the gather's buffers go a few frames on
        pendingResolvedSave = null;
    }

    private static Vector3 UnpackRgb9e5(uint p)
    {
        var scale = MathF.Pow(2f, (int)(p >> 27) - 15 - 9);
        return new Vector3(p & 511u, (p >> 9) & 511u, (p >> 18) & 511u) * scale;
    }
}
