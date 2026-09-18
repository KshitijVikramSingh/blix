using System.Numerics;

namespace Blix.Graphics.Images;

/// <summary>
/// A baked grid of how much sky each point in a scene can see, as L1 spherical harmonics.
/// </summary>
/// <remarks>
/// <para>
/// Written by <c>blix cook sky</c>, read by a renderer that wants to know what a surface can
/// actually see. The contents are GEOMETRY — what escapes the building — so the same file stays
/// correct at dawn, at noon, and under a different sky entirely. That is the whole reason it is a
/// bake rather than a lightmap.
/// </para>
/// <para>
/// Four coefficients per cell, which is one Rgba16F texel, so the runtime uploads it as a 3D
/// texture and the shader evaluates it for whatever normal a fragment has. A scalar per cell would
/// have been smaller and would have answered the wrong question: a cell has no normal, and
/// hemisphere visibility depends entirely on which way a surface faces.
/// </para>
/// </remarks>
public sealed record BlixSkyVolume(
    Vector3 Min, Vector3 Max, int SizeX, int SizeY, int SizeZ, float[] Coefficients,
    int OccupancyX = 0, int OccupancyY = 0, int OccupancyZ = 0, byte[]? Occupancy = null,
    int AlbedoX = 0, int AlbedoY = 0, int AlbedoZ = 0, byte[]? Albedo = null)
{
    /// <summary>
    /// The occupancy grid the visibility was traced through, shipped alongside it.
    /// </summary>
    /// <remarks>
    /// <b>Because the bounce has to be solved at runtime, and it needs the same geometry.</b> What
    /// lights a courtyard is the sun off its walls, which no sun-independent bake can carry — so the
    /// probes' visibility is baked and the bounce is injected each frame by marching this grid with
    /// the current sun. Shipping the grid is what makes "bake geometry, solve lighting dynamically"
    /// an arrangement rather than a slogan: the expensive, sun-invariant half is precomputed and the
    /// cheap, sun-dependent half is not.
    ///
    /// One byte per voxel rather than one bit. A bitmask is eight times smaller and cannot be a
    /// sampled 3D texture, and the consumer is a shader.
    /// </remarks>
    public bool HasOccupancy => Occupancy is { Length: > 0 };

    /// <summary>
    /// What colour each cell's surface is, RGBA8 at half the occupancy resolution.
    /// </summary>
    /// <remarks>
    /// <b>Without it the injection has no idea what it is bouncing off.</b> The march finds a
    /// surface and knows only that one is there, so every bounce took a single scalar albedo for the
    /// whole scene — which meant the light could only ever be the colour of the sun, and Sponza's
    /// one famous effect, red and green bleeding off the curtains, was absent by construction
    /// rather than merely faint.
    ///
    /// Coarser than the occupancy it accompanies, because colour is low-frequency where occlusion
    /// is not: a curtain is one colour over its whole area, but its EDGE has to be sharp or it stops
    /// being a curtain. Half resolution keeps this at ~3 MB against ~25 MB at full.
    ///
    /// Gamma-2.0 encoded (the byte holds sqrt of linear). Eight linear bits leave almost no codes
    /// below 0.05, and that is precisely the range a saturated fabric occupies in the two channels
    /// it absorbs — curtain_02 is (0.031, 0.099, 0.014), so two of its three channels would quantise
    /// to a handful of steps. The shader squares it back on read.
    /// </remarks>
    public bool HasAlbedo => Albedo is { Length: > 0 };

    /// <summary>Four floats per cell: L0, then L1 x/y/z.</summary>
    public const int FloatsPerCell = 4;

    private const ulong Magic = 0x594B53_58494C42UL; // "BLIXSKY"
    // v2 added the albedo grid after the occupancy block. No back-read: the bake takes 1.6 s and
    // nothing ships older files, so re-cooking is cheaper than carrying a migration.
    private const int Version = 2;

    public static void Write(string path, BlixSkyVolume v)
    {
        ArgumentNullException.ThrowIfNull(v);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Magic);
        w.Write(Version);
        w.Write(v.SizeX); w.Write(v.SizeY); w.Write(v.SizeZ);
        w.Write(v.Min.X); w.Write(v.Min.Y); w.Write(v.Min.Z);
        w.Write(v.Max.X); w.Write(v.Max.Y); w.Write(v.Max.Z);
        foreach (var f in v.Coefficients) w.Write(f);
        w.Write(v.OccupancyX); w.Write(v.OccupancyY); w.Write(v.OccupancyZ);
        if (v.Occupancy is { } occ) w.Write(occ);
        w.Write(v.AlbedoX); w.Write(v.AlbedoY); w.Write(v.AlbedoZ);
        if (v.Albedo is { } alb) w.Write(alb);
    }

    public static BlixSkyVolume Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadUInt64() != Magic) throw new InvalidDataException($"{path} is not a .blixsky.");
        var version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"{path} is .blixsky v{version}; this build reads v{Version}.");
        int sx = r.ReadInt32(), sy = r.ReadInt32(), sz = r.ReadInt32();
        var min = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var max = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var coeffs = new float[sx * sy * sz * FloatsPerCell];
        for (var i = 0; i < coeffs.Length; i++) coeffs[i] = r.ReadSingle();
        int ox = r.ReadInt32(), oy = r.ReadInt32(), oz = r.ReadInt32();
        var occ = ox * oy * oz > 0 ? r.ReadBytes(ox * oy * oz) : null;
        int ax = r.ReadInt32(), ay = r.ReadInt32(), az = r.ReadInt32();
        var alb = ax * ay * az > 0 ? r.ReadBytes(ax * ay * az * 4) : null;
        return new BlixSkyVolume(min, max, sx, sy, sz, coeffs, ox, oy, oz, occ, ax, ay, az, alb);
    }

    /// <summary>The volume as Rgba16F bytes, in the order a 3D texture upload wants.</summary>
    public byte[] ToRgba16F()
    {
        var bytes = new byte[SizeX * SizeY * SizeZ * FloatsPerCell * 2];
        for (var i = 0; i < SizeX * SizeY * SizeZ * FloatsPerCell; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), (Half)Coefficients[i]);
        return bytes;
    }
}
