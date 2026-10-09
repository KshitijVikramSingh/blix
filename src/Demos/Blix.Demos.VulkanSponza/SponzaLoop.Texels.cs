using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --transport-texels (stage 5a receivers; implies --transport-gpu): what a pixel reads (Shaders/texel.glsl says why).
// The cook places the texels; texel_mark.comp queues those the image reads and that still need rays; texel_gather.comp
// traces them against the cooked patches' light; the incident pass reads them in place of the clipmap where they have
// rays. A texel stops being traced at its target, so a still sun settles once.
internal sealed partial class SponzaLoop
{
    private bool transportTexels;
    // --transport-texel S: the texels' cell, metres. CPU, Sponza's hall: 2.5 cm 10.8% (the gather's own floor at 1024
    // rays), 5 cm 13.5%, 10 cm 14.5%, 20 cm 22.4%.
    private float texelSpacing = 0.05f;
    // --texel-budget N: texels gathered a frame (one pass of 32 rays each); --texel-rays N: the rays a texel stops at;
    // --texel-trust N: the rays at which its answer replaces the clipmap's whole (fewer: blended by the fraction).
    private int texelBudget = 4096;
    private int texelTarget = 1024;
    private float texelTrust = 64f;
    // One pixel in each 4 x 4 block queues the texels it reads, a different one each frame (all sixteen every 16
    // frames): a texel is 5 cm, many pixels share one up close, and a converged view queues nothing anyway. At every
    // second pixel each frame the mark cost ~3 ms on Sponza's hall.
    private const int TexelMarkStride = 4;
    private bool texelsUploaded;
    private float lodPixelsOverride = -1f;
    private int texelCount, texelCapacity;
    private Vector4 texelGrid;
    private uint texelFrame;
    private GpuBufferHandle texelHashBuffer, texelBuffer, texelLightBuffer, texelStampBuffer, texelQueueBuffer, texelProbeBuffer, texelEpochBuffer, texelPriorBuffer;
    // Following the sun: the cook's solve for another sun (SponzaLoop.Transport.cs), the sun it last solved for, the
    // solve in flight, and the texels' epoch -- bumped when new patch light lands; a texel from an older epoch is
    // queued again and keeps its old estimate as one pass's worth.
    private Func<Vector3, Vector3, Vector3[]>? transportRelight;
    private (Vector3 ToSun, Vector3 Irradiance) transportRelightSun;
    private Task<Vector3[]>? relightTask;
    private (Vector3 ToSun, Vector3 Irradiance) relightTaskSun;
    private Stopwatch? relightClock;
    private Vector4[]? cookedPacked;
    private uint texelEpoch;
    private readonly List<(GpuBufferHandle Buffer, uint Frame)> retiredBuffers = new();
    private (int Frame, Vector2 Degrees)? sunChange;
    private ShaderInterface texelMarkInterface = null!, texelGatherInterface = null!;
    private PassHandle texelMarkPassHandle, texelGatherPassHandle;
    private PipelineHandle texelMarkPipeline, texelGatherPipeline;

    private void ReadTexelArgs(AppArgs args)
    {
        transportTexels = args.Flag("transport-texels");
        if (args.Float("transport-texel") is { } s) texelSpacing = Math.Clamp(s, 0.01f, 1f);
        if (args.Int("texel-budget") is { } b) texelBudget = Math.Clamp(b, 64, 1 << 20);
        if (args.Int("texel-rays") is { } r) texelTarget = Math.Clamp(r, 32, 1 << 16);
        if (args.Float("texel-trust") is { } t) texelTrust = Math.Max(1f, t);
        // --lod-pixels E: the LOD error budget (0: full detail everywhere) -- the [Tune] field is not reachable from the
        // command line. The texels are cooked from the full-detail ray scene; LOD'd raster puts faces elsewhere.
        if (args.Float("lod-pixels") is { } lod) lodPixelsOverride = Math.Max(0f, lod);
    }

    // The texels' 64-bit key, as texel.glsl packs it: x | y << 16 in the low word, z | bin << 16 in the high.
    private static (uint Lo, uint Hi) TexelKey(int x, int y, int z, int bin) => ((uint)x | ((uint)y << 16), (uint)z | ((uint)bin << 16));

    // texel_bin.glsl's texelBin: each component x 1.5 rounded to -1/0/1 (26 directions, axis and 45-degree normals at
    // bins' centres).
    private static int TexelBin(Vector3 n)
    {
        static int Q(float c) => Math.Clamp((int)MathF.Floor(c * 1.5f + 0.5f), -1, 1) + 1;
        return Q(n.X) + 3 * Q(n.Y) + 9 * Q(n.Z);
    }

    // texel.glsl's texelHash.
    private static uint TexelHash(uint lo, uint hi)
    {
        unchecked
        {
            var h = lo * 0x9E3779B1u;
            h ^= hi * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }
    }

    // One texel per (cell x normal bin) that architecture passes through: the surface is sampled at half a cell
    // (stratified per triangle, at least one sample on any triangle), both sides; a texel sits at the centroid of its
    // samples with their mean normal. A side is dropped as inside a solid only when 15 of 16 rays meet back faces: at
    // "most of 8" (the patches' rule), 34 of the 576 hall reference points met a culled texel the camera sees --
    // Sponza's one-sided geometry shows its back to many visible surfaces. A texel never seen is never traced (only
    // what the image reads is queued), so a kept buried one costs memory alone.
    private void CookTexels(RayQueryScene scene, Func<int, int, (Vector3 A, Vector3 B, Vector3 C)> tri, Func<int, int, bool> cutout)
    {
        if (!transportTexels) return;
        var clock = Stopwatch.StartNew();
        var s = texelSpacing;
        var min = sceneBoundsMin - new Vector3(2f * s);
        var sample = 0.5f * s;
        var index = new Dictionary<ulong, int>();
        var sumPos = new List<Vector3>();
        var sumNrm = new List<Vector3>();
        var hits = new List<int>();
        var keys = new List<(uint Lo, uint Hi)>();
        var rng = new Random(11);
        long samples = 0;
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var triangles = scene.Instances[i].Mesh.Indices.Length / 3;
            for (var t = 0; t < triangles; t++)
            {
                if (cutout(i, t)) continue;
                var (a, b, c) = tri(i, t);
                var cross = Vector3.Cross(b - a, c - a);
                var area = cross.Length() * 0.5f;
                if (area <= 0f) continue;
                var front = Vector3.Normalize(cross);
                var count = Math.Max(1, (int)MathF.Ceiling(area / (sample * sample)));
                var u0 = (float)rng.NextDouble();
                var v0 = (float)rng.NextDouble();
                for (var k = 0; k < count; k++)
                {
                    var u = (u0 + k * 0.7548777f) % 1f;
                    var v = (v0 + k * 0.5698403f) % 1f;
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    var at = a + (b - a) * u + (c - a) * v;
                    var cell = (at - min) / s;
                    int cx = (int)MathF.Floor(cell.X), cy = (int)MathF.Floor(cell.Y), cz = (int)MathF.Floor(cell.Z);
                    for (var side = 0; side < 2; side++)
                    {
                        var n = side == 0 ? front : -front;
                        var bin = TexelBin(n);
                        var packed = (ulong)(uint)cx | ((ulong)(uint)cy << 16) | ((ulong)(uint)cz << 32) | ((ulong)(uint)bin << 48);
                        if (!index.TryGetValue(packed, out var id))
                        {
                            index[packed] = id = sumPos.Count;
                            sumPos.Add(Vector3.Zero); sumNrm.Add(Vector3.Zero); hits.Add(0);
                            keys.Add(TexelKey(cx, cy, cz, bin));
                        }
                        sumPos[id] += at; sumNrm[id] += n; hits[id]++;
                    }
                    samples++;
                }
            }
        }
        var placed = sumPos.Count;
        var pos = new Vector3[placed];
        var nrm = new Vector3[placed];
        for (var i = 0; i < placed; i++) { pos[i] = sumPos[i] / hits[i]; nrm[i] = Vector3.Normalize(sumNrm[i]); }
        var keep = new bool[placed];
        Parallel.For(0, placed, i =>
        {
            var r = new Random(91 + i);
            var back = 0;
            const int tests = 16;
            for (var k = 0; k < tests; k++)
            {
                var d = CosineHemisphereCpu(nrm[i], r);
                if (scene.Closest(new Ray(pos[i] + nrm[i] * 1e-3f, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h && !h.FrontFace) back++;
            }
            keep[i] = back < tests - 1;
        });
        var kept = new List<int>(placed);
        for (var i = 0; i < placed; i++) if (keep[i]) kept.Add(i);
        texelCount = kept.Count;
        texelCapacity = 1;
        while (texelCapacity < 2 * texelCount) texelCapacity <<= 1;
        var table = new uint[texelCapacity * 4];
        Array.Fill(table, 0xFFFFFFFFu);
        var texels = new Vector4[texelCount * 2];
        long longestProbe = 0;
        for (var j = 0; j < texelCount; j++)
        {
            var i = kept[j];
            texels[2 * j] = new Vector4(pos[i], 0f);
            texels[2 * j + 1] = new Vector4(nrm[i], 0f);
            var (lo, hi) = keys[i];
            var slot = TexelHash(lo, hi) & (uint)(texelCapacity - 1);
            var probe = 0;
            while (table[slot * 4] != 0xFFFFFFFFu) { slot = (slot + 1) & (uint)(texelCapacity - 1); probe++; }
            longestProbe = Math.Max(longestProbe, probe);
            table[slot * 4] = lo; table[slot * 4 + 1] = hi; table[slot * 4 + 2] = (uint)j; table[slot * 4 + 3] = 0;
        }
        var stamps = new uint[texelCount];
        Array.Fill(stamps, 0xFFFFFFFFu);
        var queueWords = 8 + 3 * texelBudget;
        texelHashBuffer = Own(device.CreateGpuBuffer(table.Length * 4, MemoryMarshal.AsBytes(table.AsSpan()), "sponza.texels.hash"));
        texelBuffer = Own(device.CreateGpuBuffer(texels.Length * 16, MemoryMarshal.AsBytes(texels.AsSpan()), "sponza.texels"));
        texelLightBuffer = Own(device.CreateGpuBuffer(texelCount * 16, new byte[texelCount * 16], "sponza.texels.light"));
        texelStampBuffer = Own(device.CreateGpuBuffer(texelCount * 4, MemoryMarshal.AsBytes(stamps.AsSpan()), "sponza.texels.stamp"));
        texelQueueBuffer = Own(device.CreateGpuBuffer(queueWords * 4, new byte[queueWords * 4], "sponza.texels.queue"));
        texelPriorBuffer = Own(device.CreateGpuBuffer(texelCount * 16, new byte[texelCount * 16], "sponza.texels.prior"));
        texelEpochBuffer = Own(device.CreateGpuBuffer(texelCount * 4, new byte[texelCount * 4], "sponza.texels.epoch"));
        texelProbeBuffer = Own(device.CreateGpuBuffer(24 * 24 * 48, new byte[24 * 24 * 48], "sponza.texels.probe"));
        texelGrid = new Vector4(min, s);
        texelsUploaded = true;
        var mb = (table.Length * 4L + texels.Length * 16L + texelCount * 40L) / 1048576.0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels: {samples:N0} surface samples at {sample * 100:0.#} cm -> {placed:N0} texels at {s * 100:0.#} cm, {texelCount:N0} kept (the rest inside a solid); hash {texelCapacity:N0} slots (longest probe {longestProbe}); {mb:0.0} MB; cooked in {clock.Elapsed.TotalSeconds:0.0} s."));
        if (longestProbe >= 64) Console.WriteLine("[VulkanSponza] texels: WARNING a hash probe run exceeds the shaders' 64 (TEXEL_PROBES): some texels cannot be found.");
    }

    // Each frame (with the cooked patches on the GPU): when the sun has turned more than a quarter degree (or changed
    // strength) from the one last solved, solve for it in the background; when that lands, upload the patches' new
    // light and bump the texels' epoch. A sun that keeps moving is chased one solve at a time.
    private void FollowSun()
    {
        if (sunChange is { } change && postLoadFrames == change.Frame)
        {
            sunYaw = change.Degrees.X * MathF.PI / 180f;
            sunPitch = change.Degrees.Y * MathF.PI / 180f;
            UpdateSunDirection();
            Console.WriteLine($"[VulkanSponza] sun turned by --sun-change at frame {postLoadFrames}: {sunDirection}");
        }
        for (var i = retiredBuffers.Count - 1; i >= 0; i--)
        {
            if (texelFrame - retiredBuffers[i].Frame < 4u) continue;
            device.DestroyGpuBuffer(retiredBuffers[i].Buffer);
            ownedGpuBuffers.Remove(retiredBuffers[i].Buffer);
            retiredBuffers.RemoveAt(i);
        }
        if (transportRelight is null || cookedPacked is null) return;
        if (relightTask is { IsCompleted: true } done)
        {
            var incident = done.Result;
            for (var i = 0; i < incident.Length; i++) cookedPacked[3 * i + 2] = new Vector4(incident[i], 0f);
            retiredBuffers.Add((cookedPatchBuffer, texelFrame));
            cookedPatchBuffer = Own(device.CreateGpuBuffer(cookedPacked.Length * 16, MemoryMarshal.AsBytes(cookedPacked.AsSpan()), "sponza.cooked.patches"));
            transportRelightSun = relightTaskSun;
            texelEpoch++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport: patch light re-solved for the sun in {relightClock!.Elapsed.TotalSeconds:0.0} s; texels re-gather (epoch {texelEpoch})."));
            relightTask = null;
        }
        if (relightTask is not null) return;
        var toSun = -Vector3.Normalize(sunDirection);
        var irradiance = EffectiveSunIrradiance;
        var turned = MathF.Acos(Math.Clamp(Vector3.Dot(toSun, transportRelightSun.ToSun), -1f, 1f)) * 180f / MathF.PI;
        var changed = Vector3.Distance(irradiance, transportRelightSun.Irradiance) > 1e-3f * MathF.Max(1f, transportRelightSun.Irradiance.Length());
        if (turned < 0.25f && !changed) return;
        relightTaskSun = (toSun, irradiance);
        relightClock = Stopwatch.StartNew();
        var relight = transportRelight;
        relightTask = Task.Run(() => relight(toSun, irradiance));
    }

    private void EnsureTexelPlaceholders()
    {
        if (!texelHashBuffer.Equals(default(GpuBufferHandle))) return;
        texelHashBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.hash.none"));
        texelBuffer = Own(device.CreateGpuBuffer(32, new byte[32], "sponza.texels.none"));
        texelLightBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.light.none"));
        texelPriorBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.prior.none"));
    }

    // What the incident pass binds and reads: the texels, or placeholders with the switch off.
    private (Vector4 Grid, Vector4 Params, ShaderBufferBinding[] Buffers) TexelIncidentInputs()
    {
        EnsureTexelPlaceholders();
        return (texelGrid, new Vector4(texelsUploaded ? texelCapacity : 1, texelsUploaded ? 1f : 0f, texelTrust, 0f), new[]
        {
            new ShaderBufferBinding("TexelHash", texelHashBuffer),
            new ShaderBufferBinding("Texels", texelBuffer),
            new ShaderBufferBinding("TexelLight", texelLightBuffer),
            new ShaderBufferBinding("TexelPrior", texelPriorBuffer),
        });
    }

    private void RecordTexels(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height)
    {
        if (!texelsUploaded || rayBlockBuffers.Length == 0) return;
        FollowSun();
        var texelParams = new Vector4(texelCapacity, texelBudget, texelTarget, texelFrame);
        var groups = (((width + TexelMarkStride - 1) / TexelMarkStride + 7) / 8, ((height + TexelMarkStride - 1) / TexelMarkStride + 7) / 8);
        graph.Dispatch(texelMarkPassHandle, new DispatchCommand(texelMarkPipeline, groups.Item1, groups.Item2, 1,
            new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uTarget", new Vector4Uniform(new Vector4(width, height, TexelMarkStride, 0f))),
                new("uOffset", new Vector4Uniform(new Vector4(texelFrame % 4, (texelFrame / 4) % 4, texelEpoch, 0f))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uTexelParams", new Vector4Uniform(texelParams)),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            Buffers: new[]
            {
                new ShaderBufferBinding("TexelHash", texelHashBuffer),
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("TexelLight", texelLightBuffer),
                new ShaderBufferBinding("TexelStamp", texelStampBuffer),
                new ShaderBufferBinding("TexelQueue", texelQueueBuffer),
                new ShaderBufferBinding("TexelProbe", texelProbeBuffer),
                new ShaderBufferBinding("TexelEpoch", texelEpochBuffer),
            }));
        // Probe mode every frame (576 threads): the census reads the latest.
        {
            graph.Dispatch(texelMarkPassHandle, new DispatchCommand(texelMarkPipeline, 3, 3, 1,
                new ShaderUniform[]
                {
                    new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                    new("uInvView", new Matrix4x4Uniform(invView)),
                    new("uTarget", new Vector4Uniform(new Vector4(width, height, 1f, 1f))),
                    new("uOffset", new Vector4Uniform(new Vector4(0f, 0f, texelEpoch, 0f))),
                    new("uTexelGrid", new Vector4Uniform(texelGrid)),
                    new("uTexelParams", new Vector4Uniform(texelParams)),
                },
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                },
                Buffers: new[]
                {
                    new ShaderBufferBinding("TexelHash", texelHashBuffer),
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("TexelLight", texelLightBuffer),
                    new ShaderBufferBinding("TexelStamp", texelStampBuffer),
                    new ShaderBufferBinding("TexelQueue", texelQueueBuffer),
                    new ShaderBufferBinding("TexelProbe", texelProbeBuffer),
                    new ShaderBufferBinding("TexelEpoch", texelEpochBuffer),
                }));
        }
        var gatherBuffers = rayBlockBuffers
            .Append(new ShaderBufferBinding("Texels", texelBuffer))
            .Append(new ShaderBufferBinding("TexelLight", texelLightBuffer))
            .Append(new ShaderBufferBinding("TexelQueue", texelQueueBuffer))
            .Append(new ShaderBufferBinding("TexelEpoch", texelEpochBuffer))
            .Append(new ShaderBufferBinding("TexelPrior", texelPriorBuffer))
            .Concat(CookedBuffers()).ToArray();
        var declared = texelGatherInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(texelGatherPassHandle, new DispatchCommand(texelGatherPipeline, texelBudget, 1, 1,
            new ShaderUniform[]
            {
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uTexelParams", new Vector4Uniform(texelParams)),
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                // A ray's sky as the screen probes take it: strength 1, a little blurred (one mip).
                new("uSky", new Vector4Uniform(new Vector4(1f, 1f, texelEpoch, 0f))),
                new("uCascadeVP", new Matrix4x4ArrayUniform(cascadeViewProj)),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
            },
            new[]
            {
                new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
                new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
                new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
                new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
            },
            Buffers: gatherBuffers.Where(b => declared.Contains(b.Name)).ToArray()));
        texelFrame++;
    }

    // The lookup as the incident pass makes it, at the reference's 24 x 24 pixels (texel_mark.comp's probe mode, run
    // every frame): how many points find a texel, where they do not and with what bins, and each point's irradiance
    // (joined with the path trace by pixel). A CPU replay of the lookup was tried first and misled: its camera ray and
    // the pre-pass's pixel land on different surfaces wherever the geometry is fine.
    private void WriteTexelProbe()
    {
        if (!texelsUploaded) return;
        var gpu = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelProbeBuffer, 0, 24 * 24 * 48).AsSpan()).ToArray();
        int gpuPoints = 0, gpuFound = 0;
        var gpuLost = new List<string>();
        for (var k = 0; k < 24 * 24; k++)
        {
            if (gpu[3 * k + 1].W < 0.5f) continue;
            gpuPoints++;
            if (gpu[3 * k + 1].X > 0f) { gpuFound++; continue; }
            if (gpuLost.Count < 40) gpuLost.Add(string.Create(CultureInfo.InvariantCulture, $"{(k % 24 + 0.5f) / 24:0.0000},{(k / 24 + 0.5f) / 24:0.0000}(w {gpu[3 * k].W:0} bins {((int)gpu[3 * k].W - 1) % 32}/{((int)gpu[3 * k].W - 1) / 32} shading {gpu[3 * k + 1].Z:0} at {gpu[3 * k].X:0.00},{gpu[3 * k].Y:0.00},{gpu[3 * k].Z:0.00})"));
        }
        for (var k = 0; k < 24 * 24; k++)
        {
            if (gpu[3 * k + 1].W < 0.5f) continue;
            var e = gpu[3 * k + 2];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    texel probe at {(k % 24 + 0.5f) / 24:0.0000},{(k / 24 + 0.5f) / 24:0.0000} irradiance {0.2126f * e.X + 0.7152f * e.Y + 0.0722f * e.Z:0.00000} lit {e.W:0.000} found {gpu[3 * k + 1].X:0} weight {gpu[3 * k + 1].Y:0.000}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] texel lookup on the GPU (probe mode): {gpuFound} of {gpuPoints} points find a texel."));
        Console.WriteLine($"    gpu lost: {string.Join(' ', gpuLost)}");
    }

    // At the shot: of the texels this frame's image read, how many have rays and how many reached the target; and how
    // many the last frame queued (by list, before the budget clamps them).
    private void WriteTexelCensus()
    {
        if (!texelsUploaded) return;
        WriteTexelProbe();
        var light = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, 0, texelCount * 16).AsSpan()).ToArray();
        var stamps = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelStampBuffer, 0, texelCount * 4).AsSpan()).ToArray();
        var queue = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelQueueBuffer, 0, 32).AsSpan()).ToArray();
        var last = texelFrame - 1;
        int seen = 0, none = 0, partial = 0, done = 0, everTraced = 0;
        for (var i = 0; i < texelCount; i++)
        {
            if (light[i].W > 0f) everTraced++;
            // Stamped in the last 16 frames: the mark visits each pixel once in 16, and the readback may land early.
            if (stamps[i] == 0xFFFFFFFFu || last - stamps[i] > 15u) continue;
            seen++;
            if (light[i].W <= 0f) none++;
            else if (light[i].W < texelTarget) partial++;
            else done++;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels after {texelFrame} frames: the image read {seen:N0} (in its last 16 frames: the mark rotates over a 4 x 4 block) -- {done:N0} at the target ({texelTarget} rays), {partial:N0} under it, {none:N0} with none; {everTraced:N0} of {texelCount:N0} ever traced. Queued (both parities) {queue[0]:N0}/{queue[1]:N0}/{queue[2]:N0} and {queue[4]:N0}/{queue[5]:N0}/{queue[6]:N0} (none / few / rest; budget {texelBudget:N0})."));
    }
}
