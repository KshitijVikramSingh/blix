using System.Numerics;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix.Recipes;

/// <summary>
/// Bakes how much sky each point in a scene can see, into a small 3D grid.
/// </summary>
/// <remarks>
/// <para>
/// Screen-space occlusion covers local contact, while this volume represents scene-scale access to
/// the sky for courtyards, arcades, and other enclosed regions.
/// </para>
/// <para>
/// The baked quantity is geometry, not lighting. It stores the
/// fraction of the sphere that escapes the building, and the average direction of escape. Neither
/// depends on where the sun is, what colour the sky is, or what time of day it is, so a bake stays
/// correct while the lighting moves freely over it. A baked lightmap would buy more and cost the
/// dynamic sun; this buys less and costs nothing.
/// </para>
/// <para>
/// Geometry is voxelised once, then each probe ray marches the occupancy density grid. This avoids
/// requiring a triangle acceleration structure and retains a coarse world representation that the
/// runtime can also use for sun injection.
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
    /// One cell: the visibility function projected onto L2 spherical harmonics — nine coefficients.
    /// </summary>
    /// <remarks>
    /// Visibility is directional because the surface normal is known only when shading. L2 can
    /// represent narrow openings and reject them for surfaces facing away. Nine coefficients fit
    /// three filterable 3D textures and are cosine-convolved during evaluation.
    /// </remarks>
    public readonly record struct SkyCell(
        float L0,
        Vector3 L1,
        float L2m2, float L2m1, float L20, float L2p1, float L2p2)
    {
        // Cosine-convolved evaluation (Ramamoorthi & Hanrahan 2001): the band coefficients are
        // pi, 2pi/3 and pi/4, and the trailing 1/pi turns irradiance into the fraction a visibility
        // term wants.
        public float Visibility(Vector3 n)
        {
            const float Y0 = 0.282095f, Y1 = 0.488603f, Y2 = 1.092548f, Y20c = 0.315392f, Y22c = 0.546274f;
            const float A0 = MathF.PI, A1 = 2f * MathF.PI / 3f, A2 = MathF.PI / 4f;
            var band2 =
                Y2 * L2m2 * n.X * n.Y +
                Y2 * L2m1 * n.Y * n.Z +
                Y20c * L20 * (3f * n.Z * n.Z - 1f) +
                Y2 * L2p1 * n.X * n.Z +
                Y22c * L2p2 * (n.X * n.X - n.Y * n.Y);
            var v = (A0 * Y0 * L0 + A1 * Y1 * Vector3.Dot(L1, n) + A2 * band2) / MathF.PI;
            return Math.Clamp(v, 0f, 1f);
        }
    }

    public sealed record Volume(
        Bounds3Lite Bounds, int SizeX, int SizeY, int SizeZ, SkyCell[] Cells,
        int OccupancyX = 0, int OccupancyY = 0, int OccupancyZ = 0, byte[]? Occupancy = null,
        // Coarser than occupancy on purpose — see the albedo parameter on Bake. RGBA8, one texel
        // per cell, gamma-2.0 encoded (sqrt of linear) so the dark saturated channels of a red
        // curtain survive eight bits. The shader squares it back.
        int AlbedoX = 0, int AlbedoY = 0, int AlbedoZ = 0, byte[]? Albedo = null)
    {
        public SkyCell At(int x, int y, int z) => Cells[(z * SizeY + y) * SizeX + x];
    }

    /// <summary>
    /// How opaque one voxel of alpha-tested geometry is to sky.
    /// </summary>
    /// <remarks>
    /// The one number here that is judged rather than derived. A leaf card is opaque where it has a
    /// leaf and clear where it does not, and the grid has no idea which — resolving that would mean
    /// sampling the albedo's alpha during voxelisation, which is where this should eventually go.
    /// A third per voxel means roughly three overlapping cards before a canopy reads as closed,
    /// which is about right for a cypress and errs toward letting light through.
    /// </remarks>
    private const float CutoutOpacity = 0.34f;


    /// <summary>One linear RGB per material: the mean of its base-colour texture, times its factor.</summary>
    /// <remarks>
    /// <para>
    /// The factor alone is insufficient when authored colour lives in the base-colour texture, so
    /// the result multiplies both.
    /// </para>
    /// <para>
    /// The mean comes from a small cooked mip, reusing the texture cook's filtering without decoding
    /// the full source image. It remains large enough to distinguish cutout surfaces from their
    /// transparent padding.
    /// </para>
    /// <para>
    /// Averaged in LINEAR space. Base colour is authored sRGB, and a mean taken over encoded values
    /// reads systematically bright — worst precisely on the dark saturated channels this is for.
    /// </para>
    /// </remarks>
    private static Vector3 MaterialAlbedo(
        BlixMeshFile file, int materialIndex, string meshPath,
        Dictionary<int, Vector3> cache, Action<string>? log)
    {
        if (cache.TryGetValue(materialIndex, out var hit)) return hit;

        var materials = file.MaterialTable;
        if (materialIndex < 0 || materialIndex >= materials.Count)
            return cache[materialIndex] = new Vector3(0.5f);

        var mat = materials[materialIndex];
        var factor = new Vector3(mat.BaseColorFactor.X, mat.BaseColorFactor.Y, mat.BaseColorFactor.Z);
        var images = file.ImageTable;
        var tint = Vector3.One;

        if (mat.BaseColorImage >= 0 && mat.BaseColorImage < images.Count)
        {
            var texPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(meshPath)) ?? string.Empty,
                images[mat.BaseColorImage].Resource);
            try
            {
                tint = AverageOfSmallestMip(texPath);
            }
            catch (Exception ex)
            {
                // Named rather than swallowed: a material silently falling back to its white factor
                // is invisible in the output and looks exactly like "colour bleeding does not work".
                log?.Invoke($"  albedo: {mat.Name} fell back to its factor ({Path.GetFileName(texPath)}: {ex.Message})");
            }
        }

        var result = factor * tint;
        log?.Invoke($"  albedo: {mat.Name,-30} ({result.X:0.000}, {result.Y:0.000}, {result.Z:0.000})");
        return cache[materialIndex] = result;
    }

    /// <summary>Mean linear colour of a cooked texture, read from a small representative mip.</summary>
    private static Vector3 AverageOfSmallestMip(string texPath)
    {
        var tex = BlixTexReader.Read(texPath);
        // Use a small mip rather than the chain tail. At roughly 32 texels per side, cutout surfaces
        // and gaps remain separable by alpha while the read is still tiny relative to the source.
        int W(int l) => Math.Max(1, tex.Width >> l);
        int H(int l) => Math.Max(1, tex.Height >> l);
        var level = tex.MipBytes.Count - 1;
        while (level > 0 && (W(level) < 32 || H(level) < 32)) level--;

        var bytes = tex.MipBytes[level];
        var (w, h) = (W(level), H(level));
        // Average only surviving cutout texels; transparent padding is not surface colour.
        var sum = Vector3.Zero;
        var weight = 0f;

        if (tex.Format is TextureFormat.Rgba8 or TextureFormat.Rgba8Srgb)
        {
            var srgb = tex.Format == TextureFormat.Rgba8Srgb;
            for (var i = 0; i + 3 < bytes.Length; i += 4)
            {
                if (bytes[i + 3] < 128) continue;   // a gap between leaves, not a surface
                sum += new Vector3(Decode(bytes[i], srgb), Decode(bytes[i + 1], srgb), Decode(bytes[i + 2], srgb));
                weight += 1f;
            }
        }
        else
        {
            var decoded = new BCnEncoder.Decoder.BcDecoder()
                .DecodeRaw(bytes, w, h, ToBcFormat(tex.Format));
            var srgb = tex.Format == TextureFormat.Bc7Srgb;
            foreach (var px in decoded)
            {
                if (px.a < 128) continue;          // a gap between leaves, not a surface
                sum += new Vector3(Decode(px.r, srgb), Decode(px.g, srgb), Decode(px.b, srgb));
                weight += 1f;
            }
        }
        // Fully transparent everywhere leaves nothing to average; the factor alone then stands.
        return weight < 1e-3f ? Vector3.One : sum / weight;

        static float Decode(byte v, bool srgb)
        {
            var c = v / 255f;
            if (!srgb) return c;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }

    private static BCnEncoder.Shared.CompressionFormat ToBcFormat(TextureFormat fmt) => fmt switch
    {
        TextureFormat.Bc7Srgb or TextureFormat.Bc7Unorm => BCnEncoder.Shared.CompressionFormat.Bc7,
        TextureFormat.Bc5Unorm => BCnEncoder.Shared.CompressionFormat.Bc5,
        _ => throw new NotSupportedException($"no CPU decode for {fmt}"),
    };


    /// <summary>A world-space box. Local so the baker does not drag Blix.Geometry into the cook.</summary>
    public readonly record struct Bounds3Lite(Vector3 Min, Vector3 Max);

    /// <summary>
    /// Voxelises every triangle in the given cooked meshes, then traces sky visibility per probe.
    /// </summary>
    /// <param name="occupancy">Occupancy resolution on the longest axis. Sets what counts as a wall.</param>
    /// <param name="probes">Probe resolution on the longest axis. Sets how finely visibility varies.</param>
    /// <param name="rays">Rays per probe. Variance falls as 1/sqrt(rays), so this buys smoothness.</param>
    /// <param name="albedo">
    /// Albedo resolution on the longest axis; 0 means half the occupancy resolution.
    /// </param>
    /// <remarks>
    /// Half, because surface colour is low-frequency but not arbitrarily so. At occupancy 256 over
    /// Sponza's ~30 m the cell is 12 cm and a curtain is about 50 cm wide; a quarter-resolution grid
    /// makes each curtain a single cell and loses the thing this is for. Half is 3 MB against the
    /// 25 MB a full-resolution RGB grid would cost — which is the figure the injection shader's
    /// header already weighed and rejected.
    /// </remarks>
    public static Volume Bake(
        IReadOnlyList<string> meshPaths, int occupancy = 256, int probes = 48, int rays = 64,
        Action<string>? log = null, int albedo = 0)
    {
        // Store opacity per triangle because cutout foliage attenuates rather than behaving like
        // masonry. The grid stores density and a ray
        // accumulates transmittance through it. Stone stays opaque; alpha-tested geometry
        // contributes a fraction, and enough overlapping leaf cards still add up to darkness.
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C, float Opacity, Vector3 Albedo)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var path in meshPaths)
        {
            var file = BlixMeshReader.Read(path);
            var materials = file.MaterialTable;
            var albedoCache = new Dictionary<int, Vector3>();
            foreach (var prim in file.Primitives)
            {
                // glTF alpha modes: 0 OPAQUE, 1 MASK, 2 BLEND. Anything not opaque is a surface the
                // renderer lets light through, so the grid should too.
                var mode = prim.MaterialIndex >= 0 && prim.MaterialIndex < materials.Count
                    ? materials[prim.MaterialIndex].AlphaMode : (byte)0;
                var opacity = mode == 0 ? 1f : CutoutOpacity;
                var albedoRgb = MaterialAlbedo(file, prim.MaterialIndex, path, albedoCache, log);
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
                        tris.Add((positions[i32[i]], positions[i32[i + 1]], positions[i32[i + 2]], opacity, albedoRgb));
                else if (lod.Indices16 is { } i16)
                    for (var i = 0; i + 2 < i16.Length; i += 3)
                        tris.Add((positions[i16[i]], positions[i16[i + 1]], positions[i16[i + 2]], opacity, albedoRgb));
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
        var density = new float[ox * oy * oz];
        var cell = new Vector3(span.X / ox, span.Y / oy, span.Z / oz);

        var albedoRes = albedo > 0 ? albedo : Math.Max(2, occupancy / 2);
        var (ax, ay, az) = (Dim(span.X, albedoRes), Dim(span.Y, albedoRes), Dim(span.Z, albedoRes));
        // Summed, then divided by the count — an AVERAGE, not a max. A cell straddling stone and
        // curtain should read as the mix; taking whichever triangle was sampled last would make the
        // colour of a boundary cell depend on mesh ordering.
        var albedoSum = new Vector3[ax * ay * az];
        var albedoCount = new int[ax * ay * az];

        // Voxelise by sampling each triangle's SURFACE, densely enough that no cell it crosses is
        // missed.
        //
        // Filling triangle AABBs would turn large floor and wall quads into solid volumes. Surface
        // sampling has the opposite failure, missing a cell a triangle only clips, and that
        // is the safe direction: a pinhole in a wall leaks a little light, where a filled courtyard
        // deletes all of it.
        var cellDiag = cell.Length();
        foreach (var (a, b, c, opacity, albedoRgb) in tris)
        {
            var area = Vector3.Cross(b - a, c - a).Length() * 0.5f;
            // Two samples per cell-width along each edge direction, so a triangle crossing a cell
            // puts at least one sample in it.
            // The cap must accommodate the largest scene triangles; too few samples leave holes in
            // broad floors and walls.
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
                var voxel = (z * oy + y) * ox + x;
                // Opaque saturates immediately; cutout accumulates, so overlapping leaf cards build
                // up density the way overlapping foliage builds up shade.
                density[voxel] = MathF.Min(1f, MathF.Max(density[voxel], opacity));

                // Same samples, coarser grid. Sample density already tracks triangle area, so a
                // plain count weights each cell's colour by how much surface actually sits in it.
                var avoxel = (Math.Clamp((int)((p.Z - min.Z) / span.Z * az), 0, az - 1) * ay
                            + Math.Clamp((int)((p.Y - min.Y) / span.Y * ay), 0, ay - 1)) * ax
                            + Math.Clamp((int)((p.X - min.X) / span.X * ax), 0, ax - 1);
                albedoSum[avoxel] += albedoRgb;
                albedoCount[avoxel]++;
            }
        }

        // Gamma-2.0 on the way out: eight linear bits put almost no codes below 0.05, which is
        // exactly where a saturated curtain's absorbing channels live. sqrt costs one instruction
        // here and one multiply in the shader, and it is format-independent — no 3D sRGB view needed.
        var albedoBytes = new byte[ax * ay * az * 4];
        var coloured = 0;
        for (var i = 0; i < albedoSum.Length; i++)
        {
            if (albedoCount[i] == 0) continue;
            coloured++;
            var c3 = albedoSum[i] / albedoCount[i];
            albedoBytes[i * 4 + 0] = (byte)Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Clamp(c3.X, 0f, 1f)) * 255f), 0, 255);
            albedoBytes[i * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Clamp(c3.Y, 0f, 1f)) * 255f), 0, 255);
            albedoBytes[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Clamp(c3.Z, 0f, 1f)) * 255f), 0, 255);
            albedoBytes[i * 4 + 3] = 255;
        }
        log?.Invoke($"  albedo grid {ax}x{ay}x{az} ({albedoBytes.Length / 1024.0 / 1024.0:0.00} MB), " +
                    $"{100.0 * coloured / albedoSum.Length:0.0}% of cells carry a surface");

        var opaqueCells = density.Count(d => d >= 0.99f);
        var partialCells = density.Count(d => d > 0.01f && d < 0.99f);
        log?.Invoke($"  voxelised {tris.Count:N0} triangles (coarsest LOD) into {ox}x{oy}x{oz}, " +
                    $"{100.0 * opaqueCells / density.Length:0.0}% opaque, " +
                    $"{100.0 * partialCells / density.Length:0.0}% partial (foliage)");

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

                // Project onto L0 + L1 + L2. The 4*pi/N is the Monte Carlo weight for a uniform
                // sphere; the basis constants are the real ones.
                const float Y0 = 0.282095f, Y1 = 0.488603f, Y2 = 1.092548f;
                const float Y20c = 0.315392f, Y22c = 0.546274f;
                var l0 = 0f;
                var l1 = Vector3.Zero;
                float l2m2 = 0f, l2m1 = 0f, l20 = 0f, l2p1 = 0f, l2p2 = 0f;
                foreach (var dir in directions)
                {
                    // Transmittance, not a yes/no. A ray through a canopy arrives carrying what got
                    // past the leaves, which is the difference between a tree and a chimney.
                    var t = Transmittance(origin, dir, min, cell, ox, oy, oz, density);
                    if (t <= 0.001f) continue;
                    l0 += Y0 * t;
                    l1 += Y1 * t * dir;
                    l2m2 += Y2 * t * dir.X * dir.Y;
                    l2m1 += Y2 * t * dir.Y * dir.Z;
                    l20 += Y20c * t * (3f * dir.Z * dir.Z - 1f);
                    l2p1 += Y2 * t * dir.X * dir.Z;
                    l2p2 += Y22c * t * (dir.X * dir.X - dir.Y * dir.Y);
                }
                var w = 4f * MathF.PI / directions.Length;

                // Window the coefficients because reconstructing a sharp function from a few
                // bands overshoots at the discontinuities — Gibbs — and visibility is about as sharp
                // as a function gets: one either sees the sky or does not. The overshoot lands ABOVE
                // the true value, so without a window it reads as bright patches on faces that
                // should be dark. L1 rings less visibly because it represents less sharp detail.
                //
                // sinc(l / (L + 1)) is Sloan's window: it costs some of the very sharpness the
                // second band was added for, which is the trade — a slightly soft cone beats a cone
                // with bright fringes around it.
                const float W1 = 0.8270f;   // sinc(1/3)
                const float W2 = 0.4135f;   // sinc(2/3)
                cells[(z * py + y) * px + x] = new SkyCell(
                    l0 * w,
                    l1 * (w * W1),
                    l2m2 * w * W2, l2m1 * w * W2, l20 * w * W2, l2p1 * w * W2, l2p2 * w * W2);
            }
        });

        log?.Invoke($"  traced {px}x{py}x{pz} probes x {rays} rays (direct sky)");

        // This file stores visibility only. Visibility multiplies incoming light; bounced radiance
        // adds to it and belongs in separate storage. Dynamic sun bounce is injected at runtime by
        // marching the occupancy/albedo grids emitted with this volume.

        // --- Probes buried in geometry ---------------------------------------
        // A probe centred inside solid geometry has no useful visibility but may still participate
        // in interpolation near a surface. Fill invalid cells from valid neighbours until the interior of
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
            var solidHere = 0.99f <= density[
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
                float a2m2 = 0f, a2m1 = 0f, a20 = 0f, a2p1 = 0f, a2p2 = 0f;
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
                    a2m2 += cells[j].L2m2; a2m1 += cells[j].L2m1; a20 += cells[j].L20;
                    a2p1 += cells[j].L2p1; a2p2 += cells[j].L2p2;
                    n++;
                }
                if (n == 0) continue;
                cells[i] = new SkyCell(acc0 / n, acc1 / n,
                    a2m2 / n, a2m1 / n, a20 / n, a2p1 / n, a2p2 / n);
                next[i] = true;
                filledThisPass++;
            }
            valid = next;
            if (filledThisPass == 0) break;
        }
        log?.Invoke($"  filled {buried} probes buried in geometry ({100.0 * buried / cells.Length:0.0}%)");

        // The grid goes out with the probes: the runtime marches it to inject the sun's bounce.
        var occBytes = new byte[density.Length];
        for (var i = 0; i < density.Length; i++)
            occBytes[i] = (byte)Math.Clamp((int)MathF.Round(density[i] * 255f), 0, 255);
        return new Volume(new Bounds3Lite(min, max), px, py, pz, cells, ox, oy, oz, occBytes,
                          ax, ay, az, albedoBytes);
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

    // Amanatides-Woo DDA accumulating transmittance. 1 = nothing in the way, 0 = fully blocked.
    private static float Transmittance(
        Vector3 origin, Vector3 dir, Vector3 min, Vector3 cell, int nx, int ny, int nz, float[] density)
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

        var transmittance = 1f;
        var first = true;
        while (true)
        {
            if (!first)
            {
                transmittance *= 1f - density[(z * ny + y) * nx + x];
                if (transmittance <= 0.001f) return 0f;
            }
            first = false;
            if (tMaxX < tMaxY && tMaxX < tMaxZ) { x += stepX; tMaxX += tDeltaX; if (x < 0 || x >= nx) return transmittance; }
            else if (tMaxY < tMaxZ)             { y += stepY; tMaxY += tDeltaY; if (y < 0 || y >= ny) return transmittance; }
            else                                { z += stepZ; tMaxZ += tDeltaZ; if (z < 0 || z >= nz) return transmittance; }
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
