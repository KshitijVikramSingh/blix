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
    Vector3 Min, Vector3 Max, int SizeX, int SizeY, int SizeZ, float[] Coefficients)
{
    /// <summary>Four floats per cell: L0, then L1 x/y/z.</summary>
    public const int FloatsPerCell = 4;

    private const ulong Magic = 0x594B53_58494C42UL; // "BLIXSKY"
    private const int Version = 1;

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
        return new BlixSkyVolume(min, max, sx, sy, sz, coeffs);
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
