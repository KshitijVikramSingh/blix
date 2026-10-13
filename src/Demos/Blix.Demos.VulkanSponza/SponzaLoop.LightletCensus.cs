using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics.Images;

namespace Blix.Demos.VulkanSponza;

// --lightlet-census (plan.md, LIGHTLETS phase 0): before any bake, how many pixels a lightlet record could NOT answer,
// over suns across the plausible band, at this camera. Run at the shot, with --rt-shadows (the frame tracer, and the
// traced shadows' origins: one surface point a half-res pixel, at the full-res pixel it stood for).
//
// A cell's record is certain for a sun when every point of the cell's footprint agrees, and the sun is further from the
// visibility boundary (over directions) than the record's error. So per facing pixel and sun: 4 points over a 4 cm
// footprint in its tangent plane (the atlas texel's size), toward the sun and toward rings around it (Rings, 8
// directions each). Its MARGIN is the smallest ring on which any point disagrees with the sun's answer (0 when the
// footprint itself is mixed). A record with error bound e (plus the disc's 0.27 deg radius) needs a ray exactly where
// the margin is under it, so the census reports the margin's distribution: any grid's needs-a-ray share reads off it.
// Pixels facing away from the sun are certain (in shadow by facing) and count as answered.
internal sealed partial class SponzaLoop
{
    private bool lightletCensus;

    // The band (plan.md): Sponza at 35-45 deg N, the census at 40. North is set so the authored sun is a midsummer
    // morning sun at 37 deg N (the authored 74.5 deg elevation is reachable only south of ~39 deg).
    private const float BandLatitude = 40f, AuthoredLatitude = 37f, Obliquity = 23.44f, BandMinElevation = 5f;
    private const float CensusCell = 0.04f;
    // The rings around each sun the census tests (degrees).
    private static readonly float[] Rings = { 0.25f, 0.5f, 1f, 2f, 4f };

    // The scene's own sun, before --sun or a sweep turned it (null until the scene is loaded).
    private Vector3? authoredSunDirection;

    private static Vector3 SunLocal(float latitudeDeg, float declinationDeg, float hourDeg)
    {
        float r(float d) => d * MathF.PI / 180f;
        var (phi, dec, h) = (r(latitudeDeg), r(declinationDeg), r(hourDeg));
        var up = MathF.Sin(phi) * MathF.Sin(dec) + MathF.Cos(phi) * MathF.Cos(dec) * MathF.Cos(h);
        var east = -MathF.Cos(dec) * MathF.Sin(h);
        var north = MathF.Cos(phi) * MathF.Sin(dec) - MathF.Sin(phi) * MathF.Cos(dec) * MathF.Cos(h);
        return new Vector3(east, north, up);   // (east, north, up)
    }

    // The hour angle (degrees) at which a sun of this declination sets below the band's least elevation, at its latitude.
    private static float DayHalfSpan(float decDeg)
    {
        float r(float d) => d * MathF.PI / 180f;
        var c = (MathF.Sin(r(BandMinElevation)) - MathF.Sin(r(BandLatitude)) * MathF.Sin(r(decDeg))) / (MathF.Cos(r(BandLatitude)) * MathF.Cos(r(decDeg)));
        return MathF.Acos(Math.Clamp(c, -1f, 1f)) * 180f / MathF.PI;
    }

    // Scene direction (y up) of a local (east, north, up), north at angle theta in the x-z plane.
    private static Vector3 ToScene(Vector3 enu, float theta) =>
        Vector3.Normalize(new Vector3(
            enu.Y * MathF.Cos(theta) - enu.X * MathF.Sin(theta),
            enu.Z,
            enu.X * MathF.Cos(theta) + enu.Y * MathF.Sin(theta)));

    // North from the authored sun: the hour angle that gives its elevation at midsummer, 37 deg N (morning), and north
    // turned so that sun's azimuth lands on the authored one.
    private float BandNorth(out float authoredHour)
    {
        var authored = -Vector3.Normalize(authoredSunDirection ?? sunDirection);
        var elev = MathF.Asin(Math.Clamp(authored.Y, -1f, 1f));
        float rad(float d) => d * MathF.PI / 180f;
        var cosH = (MathF.Sin(elev) - MathF.Sin(rad(AuthoredLatitude)) * MathF.Sin(rad(Obliquity)))
                   / (MathF.Cos(rad(AuthoredLatitude)) * MathF.Cos(rad(Obliquity)));
        authoredHour = -MathF.Acos(Math.Clamp(cosH, -1f, 1f)) * 180f / MathF.PI;
        var local = SunLocal(AuthoredLatitude, Obliquity, authoredHour);
        return MathF.Atan2(authored.Z, authored.X) - MathF.Atan2(local.X, local.Y);
    }

    private void WriteLightletCensus(string basePath)
    {
        if (!lightletCensus) return;
        if (frameTracer is null || !rtShadows || rtShadowOrigins.Length == 0)
        {
            Console.WriteLine("[VulkanSponza] lightlet census: needs --rt-shadows and the frame tracer; skipped.");
            return;
        }
        device.WaitIdle();
        var slot = device.CurrentFrameSlot;
        var (w, h) = rtShadowSize;
        var origins = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtShadowOrigins[slot], 0, w * h * 16).AsSpan()).ToArray();
        var normalBytes = device.ReadTexture(graph.GetColorTexture(SampleablePrepassNormal), out var fw, out var fh, out _);
        var (half, sx, sy) = rtShadowSlotPixel[slot];

        var theta = BandNorth(out var authoredHour);
        float rad(float d) => d * MathF.PI / 180f;

        // Suns across the band: winter, equinox, summer x hour angles, above the minimum elevation; and the authored one.
        var suns = new List<(string Name, float Lat, float Dec, float Hour)> { ("authored", AuthoredLatitude, Obliquity, authoredHour) };
        foreach (var dec in new[] { -Obliquity, 0f, Obliquity })
        foreach (var hour in new[] { -60f, -30f, 0f, 30f, 60f })
            if (SunLocal(BandLatitude, dec, hour).Z > MathF.Sin(rad(BandMinElevation)))
                suns.Add((string.Create(CultureInfo.InvariantCulture, $"dec {dec:+0;-0} h {hour:+0;-0}"), BandLatitude, dec, hour));

        // The pixels: surface point (the traced shadows' origin), normal, tangent frame; 4 footprint points each.
        var pixels = new List<(int Index, Vector3 P, Vector3 N, Vector3 T, Vector3 B)>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = origins[y * w + x];
            if (o.W <= 0f) continue;
            var fx = Math.Min(half ? 2 * x + sx : x, fw - 1);
            var fy = Math.Min(half ? 2 * y + sy : y, fh - 1);
            var b = (fy * fw + fx) * 8;
            var n = new Vector3((float)BitConverter.ToHalf(normalBytes, b), (float)BitConverter.ToHalf(normalBytes, b + 2), (float)BitConverter.ToHalf(normalBytes, b + 4));
            if (n.LengthSquared() < 1e-6f) continue;
            n = Vector3.Normalize(n);
            var t = Vector3.Normalize(MathF.Abs(n.Y) < 0.999f ? Vector3.Cross(n, Vector3.UnitY) : Vector3.Cross(n, Vector3.UnitX));
            pixels.Add((y * w + x, new Vector3(o.X, o.Y, o.Z), n, t, Vector3.Cross(n, t)));
        }
        var reach = origins.Max(o => o.W);
        Vector2[] footprint = { new(-0.25f, -0.25f), new(0.25f, -0.25f), new(-0.25f, 0.25f), new(0.25f, 0.25f) };
        var rays = new Vector4[pixels.Count * footprint.Length];
        for (var i = 0; i < pixels.Count; i++)
        {
            var (_, p, _, t, bt) = pixels[i];
            for (var k = 0; k < footprint.Length; k++)
                rays[i * footprint.Length + k] = new Vector4(p + (t * footprint[k].X + bt * footprint[k].Y) * CensusCell, reach);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightlet census: {pixels.Count:N0} surface pixels of {w * h:N0} (half res), {footprint.Length} footprint points ({CensusCell * 100:0} cm), {suns.Count} suns, rings {string.Join('/', Rings)} deg; north {theta * 180f / MathF.PI:0.0} deg."));
        var sums = new double[Rings.Length + 1];   // [0] footprint mixed; [r + 1] margin under Rings[r]
        var facingSum = 0.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        byte[]? authoredMask = null;
        var seed = 0x1234u;
        foreach (var sun in suns)
        {
            var dir = ToScene(SunLocal(sun.Lat, sun.Dec, sun.Hour), theta);
            bool[] Trace(Vector3 d) => frameTracer.OccludedFromHost(rays, d, seed++, out _);
            var atSun = Trace(dir);
            // Per pixel the first ring that disagrees (0: the footprint is mixed; Rings.Length + 1: none; 255: faces away).
            var margin = new byte[pixels.Count];
            var facing = 0;
            for (var i = 0; i < pixels.Count; i++)
            {
                if (Vector3.Dot(pixels[i].N, dir) <= 0f) { margin[i] = 255; continue; }
                facing++;
                margin[i] = (byte)(Rings.Length + 1);
                var first = atSun[i * footprint.Length];
                for (var k = 1; k < footprint.Length; k++) if (atSun[i * footprint.Length + k] != first) { margin[i] = 0; break; }
            }
            var axisU = Vector3.Normalize(MathF.Abs(dir.Y) < 0.999f ? Vector3.Cross(dir, Vector3.UnitY) : Vector3.Cross(dir, Vector3.UnitX));
            var axisV = Vector3.Cross(dir, axisU);
            for (var ring = 0; ring < Rings.Length; ring++)
            {
                var rTan = MathF.Tan(Rings[ring] * MathF.PI / 180f);
                for (var a = 0; a < 8; a++)
                {
                    var ang = a * MathF.PI / 4f + ring * 0.39f;
                    var hit = Trace(Vector3.Normalize(dir + (axisU * MathF.Cos(ang) + axisV * MathF.Sin(ang)) * rTan));
                    for (var i = 0; i < pixels.Count; i++)
                    {
                        if (margin[i] <= ring + 1 || margin[i] == 255) continue;
                        var first = atSun[i * footprint.Length];
                        for (var k = 0; k < footprint.Length; k++)
                            if (hit[i * footprint.Length + k] != first) { margin[i] = (byte)(ring + 1); break; }
                    }
                }
            }
            var under = new int[Rings.Length + 1];
            for (var i = 0; i < pixels.Count; i++)
                if (margin[i] != 255) for (var r = (int)margin[i]; r <= Rings.Length; r++) under[r]++;
            var all = (double)(w * h);
            for (var r = 0; r <= Rings.Length; r++) sums[r] += under[r] / all;
            facingSum += facing / all;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {sun.Name,-14} elev {MathF.Asin(dir.Y) * 180f / MathF.PI,5:0.0}: facing {100.0 * facing / all,5:0.0}%; footprint mixed {100.0 * under[0] / all,5:0.00}%; margin under ") +
                string.Join(", ", Rings.Select((ringDeg, r) => string.Create(CultureInfo.InvariantCulture, $"{ringDeg:0.##} deg {100.0 * under[r + 1] / all:0.00}%"))));
            if (sun.Name == "authored")
            {
                authoredMask = new byte[w * h * 4];
                for (var i = 3; i < authoredMask.Length; i += 4) authoredMask[i] = 255;
                for (var i = 0; i < pixels.Count; i++)
                {
                    var m = margin[i];
                    var (r, gr, bl) = m == 255 ? (40, 40, 40) : m == 0 ? (255, 60, 60) : m <= 2 ? (255, 200, 0) : m <= Rings.Length ? (0, 160, 255) : (255, 255, 255);
                    var o = pixels[i].Index * 4;
                    authoredMask[o] = (byte)r; authoredMask[o + 1] = (byte)gr; authoredMask[o + 2] = (byte)bl;
                }
            }
        }
        var sunCount = suns.Count;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightlet census, mean over {sunCount} suns ({clock.Elapsed.TotalSeconds:0.0} s): facing {100 * facingSum / sunCount:0.0}%; footprint mixed {100 * sums[0] / sunCount:0.00}%; margin under ") +
            string.Join(", ", Rings.Select((ringDeg, r) => string.Create(CultureInfo.InvariantCulture, $"{ringDeg:0.##} deg {100 * sums[r + 1] / sunCount:0.00}%"))) + " of half-res pixels");
        if (authoredMask is not null)
        {
            PngWriter.WriteRgba8(basePath + ".lightlets.png", authoredMask, w, h);
            Console.WriteLine($"[VulkanSponza]   {basePath}.lightlets.png  (authored sun: white margin over 4 deg, blue under 4, yellow under 0.5, red footprint mixed, grey faces away)");
        }
    }
}
