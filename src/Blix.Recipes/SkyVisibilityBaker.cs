using System.Numerics;
using Blix.Assets;

namespace Blix.Recipes;

/// <summary>
/// Bakes how much sky each point in a scene can see, into a small 3D grid.
/// </summary>
/// <remarks>
/// <para>
/// <b>The renderer has no idea what a surface can see, and it shows.</b> Ambient light in the lit
/// pass is the probe's sky irradiance times a screen-space occlusion term with a sub-metre radius.
/// That term answers "is a leaf near this stone"; nothing answers "is this stone at the bottom of a
/// courtyard". So an enclosed floor receives very nearly full sky, and the measurement says so
/// plainly: pushing the screen-space radius from 0.8 m to 20 m cut visibility 13% and the floor's
/// luminance 7%. It is not under-occluded. It is receiving sky it cannot see.
/// </para>
/// <para>
/// <b>Geometry, not lighting — which is the whole reason to bake it.</b> What lands here is the
/// fraction of the sphere that escapes the building, and the average direction of escape. Neither
/// depends on where the sun is, what colour the sky is, or what time of day it is, so a bake stays
/// correct while the lighting moves freely over it. A baked lightmap would buy more and cost the
/// dynamic sun; this buys less and costs nothing.
/// </para>
/// <para>
/// <b>An occupancy grid, not a triangle hierarchy.</b> Tracing rays against 12.8M triangles needs an
/// acceleration structure this tree does not have, and the sums are hopeless anyway — tens of
/// thousands of probes times tens of rays against millions of triangles. Voxelising once is linear
/// in triangles, and a ray then becomes a few hundred integer steps through a bitmask. It is also
/// the coarse world representation that a radiance cache would later want to march, so it is
/// substrate rather than scaffolding.
/// </para>
/// <para>
/// The cost of the approximation is honest and worth stating: a voxel is solid or not, so a window
/// narrower than a voxel closes, and a wall thinner than one is as opaque as a cathedral. At the
/// scale this is sampled — metres — that is the right trade, and the alternative errs the other way
/// by letting light through walls.
/// </para>
/// </remarks>
public static class SkyVisibilityBaker
{
    /// <summary>
    /// One cell: the visibility function projected onto L1 spherical harmonics.
    /// </summary>
    /// <remarks>
    /// <b>A function, not a number, because a cell has no normal and a surface does.</b> The first
    /// version stored a single sphere-visibility scalar, and its profile gave itself away: a probe
    /// floating above the roofline reported 0.66, because two thirds of a SPHERE escapes when the
    /// building blocks everything below. That is the correct answer to the wrong question. Ambient
    /// light wants the cosine-weighted hemisphere around a surface normal, and the normal is not
    /// known until a fragment asks.
    ///
    /// Four coefficients answer for any direction, cost one Rgba16F texel, and are the same
    /// representation the irradiance probe already uses — so the convolution constants below are
    /// the standard ones rather than anything invented here.
    /// </remarks>
    public readonly record struct SkyCell(float L0, Vector3 L1)
    {
        // Cosine-convolved evaluation: what fraction of the hemisphere around `n` escapes.
        // A0 = pi and A1 = 2pi/3 are the Lambertian convolution coefficients; the 1/pi returns a
        // fraction rather than an irradiance.
        public float Visibility(Vector3 n)
        {
            const float Y0 = 0.282095f, Y1 = 0.488603f;
            var v = (MathF.PI * Y0 * L0 + (2f * MathF.PI / 3f) * Y1 * Vector3.Dot(L1, n)) / MathF.PI;
            return Math.Clamp(v, 0f, 1f);
        }
    }

    public sealed record Volume(
        Bounds3Lite Bounds, int SizeX, int SizeY, int SizeZ, SkyCell[] Cells,
        int OccupancyX = 0, int OccupancyY = 0, int OccupancyZ = 0, byte[]? Occupancy = null)
    {
        public SkyCell At(int x, int y, int z) => Cells[(z * SizeY + y) * SizeX + x];
    }

    /// <summary>A world-space box. Local so the baker does not drag Blix.Geometry into the cook.</summary>
    public readonly record struct Bounds3Lite(Vector3 Min, Vector3 Max);

    /// <summary>
    /// Voxelises every triangle in the given cooked meshes, then traces sky visibility per probe.
    /// </summary>
    /// <param name="occupancy">Occupancy resolution on the longest axis. Sets what counts as a wall.</param>
    /// <param name="probes">Probe resolution on the longest axis. Sets how finely visibility varies.</param>
    /// <param name="rays">Rays per probe. Variance falls as 1/sqrt(rays), so this buys smoothness.</param>
    public static Volume Bake(
        IReadOnlyList<string> meshPaths, int occupancy = 256, int probes = 48, int rays = 64,
        Action<string>? log = null)
    {
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var path in meshPaths)
        {
            var file = BlixMeshReader.Read(path);
            foreach (var prim in file.Primitives)
            {
                var stride = prim.Layout.Stride;
                var positions = new Vector3[prim.VertexCount];
                for (var v = 0; v < prim.VertexCount; v++)
                {
                    var o = v * stride;
                    positions[v] = new Vector3(
                        BitConverter.ToSingle(prim.VertexBytes, o),
                        BitConverter.ToSingle(prim.VertexBytes, o + 4),
                        BitConverter.ToSingle(prim.VertexBytes, o + 8));
                    min = Vector3.Min(min, positions[v]);
                    max = Vector3.Max(max, positions[v]);
                }

                // The COARSEST LOD, deliberately. Occlusion at metre scale does not want the
                // finest surface, and the cheapest chain level carries the same walls with a
                // fraction of the triangles — which is the difference between a bake that runs in
                // seconds and one that runs in minutes.
                var lod = prim.Lods[^1];
                if (lod.Indices32 is { } i32)
                    for (var i = 0; i + 2 < i32.Length; i += 3)
                        tris.Add((positions[i32[i]], positions[i32[i + 1]], positions[i32[i + 2]]));
                else if (lod.Indices16 is { } i16)
                    for (var i = 0; i + 2 < i16.Length; i += 3)
                        tris.Add((positions[i16[i]], positions[i16[i + 1]], positions[i16[i + 2]]));
            }
        }

        if (tris.Count == 0) throw new InvalidOperationException("No triangles to bake sky visibility from.");

        // Pad so probes on the boundary are not clipped by their own scene.
        var span = max - min;
        var pad = span * 0.02f;
        min -= pad;
        max += pad;
        span = max - min;

        var longest = MathF.Max(span.X, MathF.Max(span.Y, span.Z));
        int Dim(float extent, int res) => Math.Max(2, (int)MathF.Ceiling(res * extent / longest));
        var (ox, oy, oz) = (Dim(span.X, occupancy), Dim(span.Y, occupancy), Dim(span.Z, occupancy));
        var solid = new bool[ox * oy * oz];
        var cell = new Vector3(span.X / ox, span.Y / oy, span.Z / oz);

        // Voxelise by sampling each triangle's SURFACE, densely enough that no cell it crosses is
        // missed.
        //
        // <b>Filling each triangle's bounding box instead is what made the first bake useless, and
        // the comment here excused it: "at this cell size a triangle spans few cells".</b> That is
        // true of a leaf and false of a floor slab, and Sponza is built from large quads. Their
        // AABBs are big boxes, filling one marks the whole volume solid, and the courtyard — open to
        // the sky in life — baked as the inside of a brick. Open air two metres above the floor
        // reported sky visibility 0.006.
        //
        // Surface sampling has the opposite failure, missing a cell a triangle only clips, and that
        // is the safe direction: a pinhole in a wall leaks a little light, where a filled courtyard
        // deletes all of it.
        var cellDiag = cell.Length();
        foreach (var (a, b, c) in tris)
        {
            var area = Vector3.Cross(b - a, c - a).Length() * 0.5f;
            // Two samples per cell-width along each edge direction, so a triangle crossing a cell
            // puts at least one sample in it.
            // <b>The cap has to clear the biggest triangle in the scene, not a typical one.</b> At 64
            // a large floor quad got about 2,100 samples across roughly 4,600 cells, so the floor
            // voxelised with holes — and the bake reported more sky escaping DOWNWARD than upward
            // from inside the arcade, which is impossible for a building with a floor. That negative
            // L1.y is what a leaking surface looks like from the far end of the pipeline.
            var steps = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(area) * 2f / cellDiag), 1, 1024);
            for (var i = 0; i <= steps; i++)
            for (var j = 0; j <= steps - i; j++)
            {
                var u = i / (float)steps;
                var v = j / (float)steps;
                var p = a + (b - a) * u + (c - a) * v;
                var x = Math.Clamp((int)((p.X - min.X) / cell.X), 0, ox - 1);
                var y = Math.Clamp((int)((p.Y - min.Y) / cell.Y), 0, oy - 1);
                var z = Math.Clamp((int)((p.Z - min.Z) / cell.Z), 0, oz - 1);
                solid[(z * oy + y) * ox + x] = true;
            }
        }

        var filled = solid.Count(s => s);
        log?.Invoke($"  voxelised {tris.Count:N0} triangles (coarsest LOD) into {ox}x{oy}x{oz}, " +
                    $"{100.0 * filled / solid.Length:0.0}% solid");

        var (px, py, pz) = (Dim(span.X, probes), Dim(span.Y, probes), Dim(span.Z, probes));
        var cells = new SkyCell[px * py * pz];
        var directions = FibonacciSphere(rays);

        Parallel.For(0, pz, z =>
        {
            for (var y = 0; y < py; y++)
            for (var x = 0; x < px; x++)
            {
                var origin = min + new Vector3(
                    (x + 0.5f) * span.X / px, (y + 0.5f) * span.Y / py, (z + 0.5f) * span.Z / pz);

                // Project the binary visibility function onto L0 + L1. The 4*pi/N is the Monte
                // Carlo weight for a uniform sphere; the basis constants are the real ones.
                const float Y0 = 0.282095f, Y1 = 0.488603f;
                var l0 = 0f;
                var l1 = Vector3.Zero;
                foreach (var dir in directions)
                {
                    if (Occluded(origin, dir, min, cell, ox, oy, oz, solid)) continue;
                    l0 += Y0;
                    l1 += Y1 * dir;
                }
                var w = 4f * MathF.PI / directions.Length;
                cells[(z * py + y) * px + x] = new SkyCell(l0 * w, l1 * w);
            }
        });

        log?.Invoke($"  traced {px}x{py}x{pz} probes x {rays} rays (direct sky)");

        // --- Why there is no bounce pass here -------------------------------
        // <b>There was one, and it made this volume mean two things at once.</b> Rays that hit were
        // given the visibility of the surface they struck, times an albedo. The result is not
        // visibility any more — it is incoming radiance — and the shader multiplies sky irradiance
        // by this SH, which already carries the sky's own directional distribution. Multiplying two
        // directional fields double-counts direction, and it showed: inside the arcade the bounced
        // field pointed DOWNWARD, correctly, because the brightest nearby surface is the floor. An
        // up-facing floor then evaluated NEGATIVE and clamped to black.
        //
        // Visibility multiplies. Radiance adds. They are different quantities and they need
        // different storage, and the bounce that actually matters cannot live here anyway: it is the
        // SUN's, at irradiance 17, and a sun-independent bake cannot carry it. Sky-only bounce was
        // measured at this scene and lifts interior visibility 0.15 -> 0.19, which is not the
        // missing light.
        //
        // So this file stores visibility, the one thing that is purely geometry and stays true as
        // the sun moves. The bounce belongs to a runtime pass that injects the current sun into the
        // same voxel grid.

        // --- Probes buried in geometry ---------------------------------------
        // <b>A probe whose centre is inside a wall sees nothing, and every surface near that wall
        // samples it.</b> This is what made the first wired-up version black rather than dim: the
        // cell under Sponza's floor reported exactly zero, and the floor is what reads it. Pushing
        // the lookup along the normal does not rescue it — the offset would have to exceed a cell,
        // and a cell here is 0.77 m.
        //
        // So invalid cells are filled from their valid neighbours, repeatedly, until the interior of
        // solid geometry carries whatever the space around it carries. A buried probe has no right
        // answer; what it needs is to stop poisoning the surfaces that interpolate through it.
        var valid = new bool[cells.Length];
        var buried = 0;
        for (var z = 0; z < pz; z++)
        for (var y = 0; y < py; y++)
        for (var x = 0; x < px; x++)
        {
            var origin = min + new Vector3(
                (x + 0.5f) * span.X / px, (y + 0.5f) * span.Y / py, (z + 0.5f) * span.Z / pz);
            var v = (origin - min) / cell;
            var solidHere = solid[
                (Math.Clamp((int)v.Z, 0, oz - 1) * oy + Math.Clamp((int)v.Y, 0, oy - 1)) * ox
                + Math.Clamp((int)v.X, 0, ox - 1)];
            valid[(z * py + y) * px + x] = !solidHere;
            if (solidHere) buried++;
        }

        for (var pass = 0; pass < 8; pass++)
        {
            var filledThisPass = 0;
            var next = (bool[])valid.Clone();
            for (var z = 0; z < pz; z++)
            for (var y = 0; y < py; y++)
            for (var x = 0; x < px; x++)
            {
                var i = (z * py + y) * px + x;
                if (valid[i]) continue;
                var acc0 = 0f;
                var acc1 = Vector3.Zero;
                var n = 0;
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx2 = x + dx, ny2 = y + dy, nz2 = z + dz;
                    if (nx2 < 0 || ny2 < 0 || nz2 < 0 || nx2 >= px || ny2 >= py || nz2 >= pz) continue;
                    var j = (nz2 * py + ny2) * px + nx2;
                    if (!valid[j]) continue;
                    acc0 += cells[j].L0;
                    acc1 += cells[j].L1;
                    n++;
                }
                if (n == 0) continue;
                cells[i] = new SkyCell(acc0 / n, acc1 / n);
                next[i] = true;
                filledThisPass++;
            }
            valid = next;
            if (filledThisPass == 0) break;
        }
        log?.Invoke($"  filled {buried} probes buried in geometry ({100.0 * buried / cells.Length:0.0}%)");

        // The grid goes out with the probes: the runtime marches it to inject the sun's bounce.
        var occBytes = new byte[solid.Length];
        for (var i = 0; i < solid.Length; i++) occBytes[i] = solid[i] ? (byte)255 : (byte)0;
        return new Volume(new Bounds3Lite(min, max), px, py, pz, cells, ox, oy, oz, occBytes);
    }

    // As Occluded, but reports WHERE it stopped, so a second pass can ask what that surface sees.
    private static bool Trace(
        Vector3 origin, Vector3 dir, Vector3 min, Vector3 cell, int nx, int ny, int nz, bool[] solid,
        out Vector3 hit)
    {
        var p = (origin - min) / cell;
        var x = Math.Clamp((int)p.X, 0, nx - 1);
        var y = Math.Clamp((int)p.Y, 0, ny - 1);
        var z = Math.Clamp((int)p.Z, 0, nz - 1);
        var stepX = dir.X > 0 ? 1 : -1;
        var stepY = dir.Y > 0 ? 1 : -1;
        var stepZ = dir.Z > 0 ? 1 : -1;
        float Next(float pos, int step, float d) =>
            MathF.Abs(d) < 1e-9f ? float.MaxValue
            : (step > 0 ? (MathF.Floor(pos) + 1 - pos) : (pos - MathF.Floor(pos))) / MathF.Abs(d);
        var tMaxX = Next(p.X, stepX, dir.X);
        var tMaxY = Next(p.Y, stepY, dir.Y);
        var tMaxZ = Next(p.Z, stepZ, dir.Z);
        var tDeltaX = MathF.Abs(dir.X) < 1e-9f ? float.MaxValue : 1f / MathF.Abs(dir.X);
        var tDeltaY = MathF.Abs(dir.Y) < 1e-9f ? float.MaxValue : 1f / MathF.Abs(dir.Y);
        var tDeltaZ = MathF.Abs(dir.Z) < 1e-9f ? float.MaxValue : 1f / MathF.Abs(dir.Z);
        var first = true;
        while (true)
        {
            if (!first && solid[(z * ny + y) * nx + x])
            {
                hit = min + new Vector3((x + 0.5f) * cell.X, (y + 0.5f) * cell.Y, (z + 0.5f) * cell.Z);
                return true;
            }
            first = false;
            if (tMaxX < tMaxY && tMaxX < tMaxZ) { x += stepX; tMaxX += tDeltaX; if (x < 0 || x >= nx) break; }
            else if (tMaxY < tMaxZ)             { y += stepY; tMaxY += tDeltaY; if (y < 0 || y >= ny) break; }
            else                                { z += stepZ; tMaxZ += tDeltaZ; if (z < 0 || z >= nz) break; }
        }
        hit = default;
        return false;
    }

    // Amanatides-Woo DDA through the occupancy grid. Returns true if the ray hits before leaving.
    private static bool Occluded(
        Vector3 origin, Vector3 dir, Vector3 min, Vector3 cell, int nx, int ny, int nz, bool[] solid)
    {
        var p = (origin - min) / cell;
        var x = Math.Clamp((int)p.X, 0, nx - 1);
        var y = Math.Clamp((int)p.Y, 0, ny - 1);
        var z = Math.Clamp((int)p.Z, 0, nz - 1);

        var stepX = dir.X > 0 ? 1 : -1;
        var stepY = dir.Y > 0 ? 1 : -1;
        var stepZ = dir.Z > 0 ? 1 : -1;

        float Next(float pos, int step, float d) =>
            MathF.Abs(d) < 1e-9f ? float.MaxValue
            : (step > 0 ? (MathF.Floor(pos) + 1 - pos) : (pos - MathF.Floor(pos))) / MathF.Abs(d);

        var tMaxX = Next(p.X, stepX, dir.X);
        var tMaxY = Next(p.Y, stepY, dir.Y);
        var tMaxZ = Next(p.Z, stepZ, dir.Z);
        var tDeltaX = MathF.Abs(dir.X) < 1e-9f ? float.MaxValue : 1f / MathF.Abs(dir.X);
        var tDeltaY = MathF.Abs(dir.Y) < 1e-9f ? float.MaxValue : 1f / MathF.Abs(dir.Y);
        var tDeltaZ = MathF.Abs(dir.Z) < 1e-9f ? float.MaxValue : 1f / MathF.Abs(dir.Z);

        // Skip the cell the probe sits in: a probe inside geometry would otherwise report zero sky
        // and paint a black cell into the middle of a lit room.
        var first = true;
        while (true)
        {
            if (!first && solid[(z * ny + y) * nx + x]) return true;
            first = false;

            if (tMaxX < tMaxY && tMaxX < tMaxZ) { x += stepX; tMaxX += tDeltaX; if (x < 0 || x >= nx) return false; }
            else if (tMaxY < tMaxZ)             { y += stepY; tMaxY += tDeltaY; if (y < 0 || y >= ny) return false; }
            else                                { z += stepZ; tMaxZ += tDeltaZ; if (z < 0 || z >= nz) return false; }
        }
    }

    // Evenly spread directions over the sphere. The golden-angle spiral, for the same reason
    // shadow.glsl uses a Vogel disc: a uniform random set clumps, and clumps become bias.
    private static Vector3[] FibonacciSphere(int count)
    {
        var dirs = new Vector3[count];
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var i = 0; i < count; i++)
        {
            var y = 1f - 2f * (i + 0.5f) / count;
            var r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            var theta = golden * i;
            dirs[i] = new Vector3(MathF.Cos(theta) * r, y, MathF.Sin(theta) * r);
        }
        return dirs;
    }
}
