using System.Numerics;

namespace Blix.Graphics.Images;

/// <summary>
/// A baked grid of how much sky each point in a scene can see, as L2 spherical harmonics.
/// </summary>
/// <remarks>
/// <para>
/// Written by <c>blix cook sky</c>, read by a renderer that wants to know what a surface can
/// actually see. The contents describe geometry rather than a particular sun or sky, so one bake
/// remains valid as lighting changes.
/// </para>
/// <para>
/// Nine coefficients per cell are padded to twelve and uploaded as three RGBA16F 3D textures. The
/// shader evaluates directional visibility for the surface normal; a scalar per cell cannot express
/// which part of the sky is open.
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
    /// Runtime bounce injection marches the same geometry with the current sun. One byte per voxel
    /// preserves partial foliage density and is directly sampleable as a 3D texture.
    /// </remarks>
    public bool HasOccupancy => Occupancy is { Length: > 0 };

    /// <summary>
    /// What colour each cell's surface is, RGBA8 at half the occupancy resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Surface colour is lower-frequency than occupancy, so the default baker uses half resolution.
    /// RGB stores the square root of linear colour to preserve precision in dark saturated
    /// channels; the shader squares values on read.
    /// </para>
    /// <para>
    /// <b>ALPHA is the cell's mean <c>KHR_materials_diffuse_transmission</c> factor, stored
    /// linearly</b> — how much light the surface in this cell scatters through rather than
    /// stopping. It is not a colour and is not gamma-encoded. Cells with no surface hold zero in
    /// all four channels; a reader distinguishes "no surface" by the grid's own presence and by
    /// RGB, never by alpha, because a fully opaque surface legitimately stores alpha zero.
    /// </para>
    /// </remarks>
    public bool HasAlbedo => Albedo is { Length: > 0 };

    /// <summary>Nine floats per cell: L0, L1 x/y/z, then the five L2 coefficients.</summary>
    /// <remarks>
    /// Twelve are stored so the coefficients divide into three RGBA texels and retain hardware
    /// trilinear filtering. The final three slots are padding.
    /// </remarks>
    public const int FloatsPerCell = 12;

    private const ulong Magic = 0x594B53_58494C42UL; // "BLIXSKY"
    // Current layout: L2 coefficients, followed by occupancy and albedo grids. No older layout is
    // accepted; regenerate the scene-level bake instead.
    //
    // v4: the albedo grid's alpha carries per-material diffuse transmission. The BYTES did not
    // move — v3 wrote a constant 255 into that channel and nothing read it — so this bump buys
    // nothing structural and is made anyway, because a v3 file loaded by a v4 build would be
    // silently interpreted as "every surface in the scene scatters 100% of the light through it".
    // A format whose meaning changes under a fixed layout is exactly the case a version exists
    // for; refusing the file is the only outcome that cannot be mistaken for a look.
    private const int Version = 4;

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

    /// <summary>One of the three RGBA16F volumes the nine coefficients are split across.</summary>
    /// <remarks>
    /// Split rather than interleaved because a 3D texture holds four channels, and the shader wants
    /// three filtered fetches rather than one fetch and a lot of address arithmetic.
    /// Texture 0 carries L0 and L1; texture 1 the first four L2 terms; texture 2 the last, with
    /// three slots spare.
    /// </remarks>
    public byte[] ToRgba16F(int texture)
    {
        var cells = SizeX * SizeY * SizeZ;
        var bytes = new byte[cells * 4 * 2];
        for (var c = 0; c < cells; c++)
        for (var k = 0; k < 4; k++)
        {
            var src = c * FloatsPerCell + texture * 4 + k;
            var value = src < (c + 1) * FloatsPerCell ? Coefficients[src] : 0f;
            BitConverter.TryWriteBytes(bytes.AsSpan((c * 4 + k) * 2, 2), (Half)value);
        }
        return bytes;
    }
}
