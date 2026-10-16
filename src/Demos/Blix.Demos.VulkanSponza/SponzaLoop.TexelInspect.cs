using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --texel-inspect [N | x,y;x,y...] (at --shot): what a pixel's lightmap texels are. For each pixel -- the N most
// spot-like (darkest in the incident field against its surroundings, with an atlas texel), or the ones named in frame
// fractions -- the surface under it (a CPU ray through the pixel: placement, distance, face) and the four atlas texels
// its bilinear lookup reads: their records' position against the pixel's point, normal against its normal, rays, light
// and filtered light, and whether buried. A spot whose texels sit far from the point (the atlas coordinate reaching
// another place: a coarse LOD's UVs), face another way (the other side of a fold), or are lit dark where the point is
// open, each reads differently here.
internal sealed partial class SponzaLoop
{
    private string? texelInspect;

    private static float HalfAt(byte[] bytes, int index) => (float)BitConverter.ToHalf(bytes, index * 2);

    // A resolved answer (texelResolved: rgb9e5, then the rays share and the lit flag): its light and rays share.
    private static string ResolvedText(ReadOnlySpan<uint> r)
    {
        var scale = MathF.Pow(2f, (int)(r[0] >> 27) - 15 - 9);
        var lum = ((r[0] & 511u) + ((r[0] >> 9) & 511u) + ((r[0] >> 18) & 511u)) * scale / 3f;
        return string.Create(CultureInfo.InvariantCulture, $"{lum:0.000} (rays {(r[1] & 0xFFFFu) / 65535f:0.00}{((r[1] >> 16) == 0u ? ", unlit" : "")})");
    }

    private void WriteTexelInspect()
    {
        if (texelInspect is null || !texelsUploaded || !lightmapTexels || rayQueries is not { } scene) return;
        const float Lum = 1f / 3f;
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var w, out var h, out _);
        var cells = device.ReadTexture(graph.GetColorTexture(lightmapTexelHandle), out var cw, out var ch, out _);
        var normals = device.ReadTexture(graph.GetColorTexture(SampleablePrepassNormal), out var nw, out _, out _);
        float Luma(int x, int y) { var i = (y * w + x) * 4; return (HalfAt(incident, i) + HalfAt(incident, i + 1) + HalfAt(incident, i + 2)) * Lum; }
        uint Cell(int x, int y) => BitConverter.ToUInt32(cells, (y * cw + x) * 4);

        var points = new List<(int X, int Y)>();
        if (texelInspect.Contains(','))
        {
            foreach (var part in texelInspect.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = part.Split(',');
                points.Add(((int)(float.Parse(xy[0], CultureInfo.InvariantCulture) * w), (int)(float.Parse(xy[1], CultureInfo.InvariantCulture) * h)));
            }
        }
        else
        {
            // Spot-likeness: a pixel's light over the mean of a 33 x 33 window around it (on a 4x grid), lowest first,
            // pixels with an atlas texel only, 40 px apart.
            var count = int.TryParse(texelInspect, out var n) ? n : 12;
            var candidates = new List<(float Ratio, int X, int Y)>();
            for (var y = 20; y < h - 20; y += 4)
            for (var x = 20; x < w - 20; x += 4)
            {
                if (Cell(x, y) == 0u) continue;
                var own = Luma(x, y);
                float sum = 0f; var k = 0;
                for (var dy = -16; dy <= 16; dy += 8)
                for (var dx = -16; dx <= 16; dx += 8) { sum += Luma(x + dx, y + dy); k++; }
                var mean = sum / k;
                if (mean > 1e-4f) candidates.Add((own / mean, x, y));
            }
            foreach (var c in candidates.OrderBy(c => c.Ratio))
            {
                if (points.Count >= count) break;
                if (points.Any(p => Math.Abs(p.X - c.X) < 40 && Math.Abs(p.Y - c.Y) < 40)) continue;
                points.Add((c.X, c.Y));
            }
        }

        Matrix4x4.Invert(viewProj, out var invViewProj);
        Console.WriteLine($"[VulkanSponza] texel inspect: {points.Count} pixel(s) of {w} x {h}; atlas {lightmapAtlasWidth} x {lightmapAtlasHeight}, records from {lightmapFirstRecord:N0}.");
        foreach (var (px, py) in points)
        {
            var ndc = new Vector2((px + 0.5f) / w * 2f - 1f, (py + 0.5f) / h * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition);
            var hit = scene.Closest(new Ray(cameraPosition, dir));
            var ni = (py * nw + px) * 4;
            var pixelN = Vector3.Normalize(new Vector3(HalfAt(normals, ni), HalfAt(normals, ni + 1), HalfAt(normals, ni + 2)));
            var packed = Cell(px, py);
            var atlas = new Vector2(packed & 0xFFFF, packed >> 16) / 8f;
            var point = hit is { } hh ? cameraPosition + dir * hh.T : Vector3.Zero;
            var name = hit is { } h2 && h2.Instance < rayPlacementNames.Count ? rayPlacementNames[h2.Instance] : "-";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  pixel {px},{py} ({(float)px / w:0.000},{(float)py / h:0.000}): incident {Luma(px, py):0.000} | ray {(hit is { } h3 ? $"t {h3.T:0.00} m, placement {h3.Instance} '{name}', tri {h3.Triangle}, {(h3.FrontFace ? "front" : "BACK")}" : "MISS")} | pre-pass N ({pixelN.X:0.00},{pixelN.Y:0.00},{pixelN.Z:0.00}) | atlas ({atlas.X:0.0},{atlas.Y:0.0})"));
            if (packed == 0u) continue;
            var f = atlas - new Vector2(0.5f);
            var bx = (int)MathF.Floor(f.X); var by = (int)MathF.Floor(f.Y);
            var t = f - new Vector2(bx, by);
            for (var i = 0; i < 4; i++)
            {
                int ox = i & 1, oy = i >> 1, ax = bx + ox, ay = by + oy;
                var weight = (ox == 1 ? t.X : 1f - t.X) * (oy == 1 ? t.Y : 1f - t.Y);
                if (ax < 0 || ay < 0 || ax >= lightmapAtlasWidth || ay >= lightmapAtlasHeight) { Console.WriteLine($"    [{i}] off the atlas"); continue; }
                var id = BitConverter.ToUInt32(device.ReadGpuBuffer(lightmapIndexMapBuffer, (ay * lightmapAtlasWidth + ax) * 4, 4), 0);
                if (id == 0xFFFFFFFFu) { Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    [{i}] cell ({ax},{ay}) w {weight:0.00}: EMPTY")); continue; }
                var rec = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, (int)id * 32, 32).AsSpan()).ToArray();
                // The side the pixel shows, as texel.glsl picks it.
                if (rec[0].W < 0f && Vector3.Dot(new Vector3(rec[1].X, rec[1].Y, rec[1].Z), pixelN) < 0f)
                {
                    id += 1;
                    rec = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, (int)id * 32, 32).AsSpan()).ToArray();
                }
                var pos = new Vector3(rec[0].X, rec[0].Y, rec[0].Z);
                var nrm = new Vector3(rec[1].X, rec[1].Y, rec[1].Z);
                var bits = BitConverter.SingleToUInt32Bits(rec[1].W);
                // At rest (split light) the working buffers are placeholders: the resolved answer is what the pixel reads.
                var working = !texelLightBuffer.Equals(texelRestPlaceholder);
                var light = working ? MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, (int)id * 16, 16).AsSpan())[0] : default;
                var filtered = working ? MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelFilteredBuffer, (int)id * 16, 16).AsSpan())[0] : default;
                var resolved = texelResolvedBuffer.Equals(default(GpuBufferHandle)) ? "-"
                    : ResolvedText(MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelResolvedBuffer, (int)id * 8, 8).AsSpan()));
                var buried = MathF.Abs(rec[0].W) < 2f;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    [{i}] cell ({ax},{ay}) w {weight:0.00} -> record {id:N0} at cell ({bits & 0xFFFF},{bits >> 16}){(buried ? " BURIED" : "")}: {(point == Vector3.Zero ? "" : $"{Vector3.Distance(pos, point) * 100f:0.0} cm from the point, ")}n.pixelN {Vector3.Dot(nrm, pixelN):0.00}, n ({nrm.X:0.00},{nrm.Y:0.00},{nrm.Z:0.00}), rays {light.W:0}, light {(light.X + light.Y + light.Z) * Lum:0.000}, filtered {(filtered.W > 0f ? ((filtered.X + filtered.Y + filtered.Z) * Lum).ToString("0.000", CultureInfo.InvariantCulture) : "-")}, resolved {resolved}"));
                if (!buried) InspectEscapes(scene, pos, nrm);
            }
        }
    }

    // The record's gather rays as the cook starts them (texel_nodes_rays.comp: 1 cm off along its normal), cosine-weighted,
    // on the CPU's watertight scene and on the Metal tracer: the share that escapes to the sky on each, and the rays Metal
    // let out that the CPU stopped (a crack at a joint the frame's tracer has and the triangles do not).
    private void InspectEscapes(RayQueryScene scene, Vector3 pos, Vector3 nrm)
    {
        const int N = 4096;
        var n = Vector3.Normalize(nrm);
        var t1 = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, n));
        var t2 = Vector3.Cross(n, t1);
        var origin = pos + n * 0.01f;
        var rays = new Vector4[2 * N];
        var dirs = new Vector3[N];
        for (var k = 0; k < N; k++)
        {
            float u1 = (k + 0.5f) / N, u2 = (k * 0.6180339887f) % 1f;
            float r = MathF.Sqrt(u1), phi = 2f * MathF.PI * u2;
            var d = Vector3.Normalize(t1 * (r * MathF.Cos(phi)) + t2 * (r * MathF.Sin(phi)) + n * MathF.Sqrt(MathF.Max(0f, 1f - u1)));
            dirs[k] = d;
            rays[2 * k] = new Vector4(origin, 0f);
            rays[2 * k + 1] = new Vector4(d, 1e30f);
        }
        int cpuMiss = 0, cpuBack = 0, metalMiss = 0, crack = 0;
        float nearestBack = float.PositiveInfinity;
        var nearestBackDir = Vector3.Zero;
        uint[]? hits = frameTracer is { } t ? t.TraceFromHost(rays, 0u, true, out _) : null;
        var crackDirs = new List<Vector3>();
        for (var k = 0; k < N; k++)
        {
            var hit = scene.Closest(new Ray(origin, dirs[k]));
            if (hit is null) cpuMiss++;
            else if (!hit.Value.FrontFace)
            {
                cpuBack++;
                if (hit.Value.T < nearestBack) { nearestBack = hit.Value.T; nearestBackDir = dirs[k]; }
            }
            if (hits is null) continue;
            var metalHit = (hits[k * 12 + 3] & 1u) != 0u;
            if (!metalHit) metalMiss++;
            if (!metalHit && hit is not null) { crack++; if (crackDirs.Count < 3) crackDirs.Add(dirs[k]); }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        at ({pos.X:0.0000},{pos.Y:0.0000},{pos.Z:0.0000}); nearest back face {(float.IsInfinity(nearestBack) ? "-" : string.Create(CultureInfo.InvariantCulture, $"{nearestBack * 100f:0.00} cm toward ({nearestBackDir.X:0.00},{nearestBackDir.Y:0.00},{nearestBackDir.Z:0.00})"))}; escapes from 1 cm off: CPU {100.0 * cpuMiss / N:0.00}% (back faces met {100.0 * cpuBack / N:0.00}%), Metal {(hits is null ? "-" : (100.0 * metalMiss / N).ToString("0.00", CultureInfo.InvariantCulture) + "%")}; Metal out where the CPU stopped: {crack}{string.Concat(crackDirs.Select(d => string.Create(CultureInfo.InvariantCulture, $" ({d.X:0.00},{d.Y:0.00},{d.Z:0.00})")))}"));
    }
}
