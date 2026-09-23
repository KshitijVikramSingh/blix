using Blix.Cooked;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Graphics.Images;

// Engine-native binary IBL probe container. One file per HDR sky source bundles the visible
// environment, diffuse irradiance, GGX and Charlie prefilter chains, their lookup tables, and the
// detected sun. Cooking performs all convolution; runtime loading only reads and uploads blobs.
//
// File layout (little-endian), v4:
//
//   The shared Blix cooked preamble first -- see Blix.Cooked/CookPreamble.cs.
//   Then this format's own header, at offsets relative to the end of it:
//
//   offset  size  field
//   ------------------------------------------
//   0       4     flags       (bit 0 = sun direction, bit 1 = sun irradiance)
//   4       4     envFaceSize
//   8       4     irrFaceSize
//   12      4     prefilterBaseSize
//   16      4     prefilterMipCount
//   20      4     brdfLutSize
//   24      12    sunDirection  (vec3 float32, valid only when flag bit 0)
//   36      12    sunIrradiance (vec3 float32, valid only when flag bit 1)
//   --------- 48 bytes (format header) ---------
//                 envCube      : 4 channels * 6 faces * envFaceSize^2     Halves (RGBA16F)
//                 irrCube      : 4 channels * 6 faces * irrFaceSize^2     Halves (RGBA16F)
//                 prefilter[k] : 4 * 6 * (prefilterBaseSize>>k)^2 Halves, k=0..prefilterMipCount-1
//                 brdfLut      : brdfLutSize * brdfLutSize * 4 bytes      (RGBA8)
//                 sheenFaceSize, sheenMipCount, sheenLutSize               (3 int32 values)
//                 sheen[k]     : 4 * 6 * (sheenFaceSize>>k)^2 Halves
//                 sheenLut     : sheenLutSize * sheenLutSize * 4 bytes     (RGBA8)
//
// Blobs are tightly concatenated. Their dimensions provide the lengths; there are no per-blob
// length fields. The reader accepts v4 only, so older probes must be recooked.
public static class BlixProbe
{
    public const uint Magic = 0x50584C42; // "BLXP" little-endian

    // Retained as the identifier of the previous layout; no current reader accepts it.
    public const uint Version3 = 3;

    /// <summary>The current probe layout, including sun irradiance and Charlie sheen data.</summary>
    public const uint Version4 = 4;
    public const int HeaderSize = 48;

    /// <summary>The recipe id the shipped probe cook stamps.</summary>
    public const string ShippedRecipe = "gpro";

    /// <summary>The probe cook's own version — see BlixMesh.MeshRecipeVersion for why.</summary>
    public const uint ShippedRecipeVersion = 1;

    [Flags]
    public enum Flags : uint
    {
        None = 0,
        HasSunDirection = 1 << 0,

        /// <summary>The disc was removed from the lighting integrals and its irradiance recorded.</summary>
        HasSunIrradiance = 1 << 1,
    }
}

public sealed record BlixProbeData(
    int EnvFaceSize,
    int IrradianceFaceSize,
    int PrefilterBaseSize,
    int PrefilterMipCount,
    int BrdfLutSize,
    Vector3? SunDirection,
    Vector3? SunIrradiance,
    Half[] EnvCube,
    Half[] IrradianceCube,
    Half[][] PrefilteredSpecular,
    byte[] BrdfLut,

    /// <summary>Face size of the Charlie-prefiltered cube, and how many roughness mips it has.</summary>
    int SheenFaceSize = 0,
    int SheenMipCount = 0,

    /// <summary>Side of the square sheen directional-albedo table, E(NdotV, roughness) in R.</summary>
    int SheenLutSize = 0,

    /// <summary>The environment convolved with Charlie, one mip per sheen roughness.</summary>
    Half[][]? PrefilteredSheen = null,

    /// <summary>E(NdotV, roughness): what fraction of arriving light the sheen lobe carries.</summary>
    byte[]? SheenLut = null);

public static class BlixProbeWriter
{
    /// <param name="stamp">See <c>BlixMeshWriter.Write</c> — required, for the same reason.</param>
    public static void Write(string path, BlixProbeData data, in CookStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(data);
        if (data.PrefilteredSpecular.Length != data.PrefilterMipCount)
        {
            throw new ArgumentException(
                $"PrefilterMipCount ({data.PrefilterMipCount}) does not match PrefilteredSpecular.Length ({data.PrefilteredSpecular.Length}).",
                nameof(data));
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        CookPreamble.Write(fs, BlixProbe.Magic, BlixProbe.Version4, stamp);
        using var bw = new BinaryWriter(fs);

        var flags = BlixProbe.Flags.None;
        if (data.SunDirection.HasValue) flags |= BlixProbe.Flags.HasSunDirection;
        if (data.SunIrradiance.HasValue) flags |= BlixProbe.Flags.HasSunIrradiance;

        bw.Write((uint)flags);
        bw.Write(data.EnvFaceSize);
        bw.Write(data.IrradianceFaceSize);
        bw.Write(data.PrefilterBaseSize);
        bw.Write(data.PrefilterMipCount);
        bw.Write(data.BrdfLutSize);
        var sun = data.SunDirection ?? Vector3.Zero;
        bw.Write(sun.X);
        bw.Write(sun.Y);
        bw.Write(sun.Z);
        var irradiance = data.SunIrradiance ?? Vector3.Zero;
        bw.Write(irradiance.X);
        bw.Write(irradiance.Y);
        bw.Write(irradiance.Z);

        WriteHalves(bw, data.EnvCube);
        WriteHalves(bw, data.IrradianceCube);
        foreach (var mip in data.PrefilteredSpecular)
        {
            WriteHalves(bw, mip);
        }
        bw.Write(data.BrdfLut);

        bw.Write(data.SheenFaceSize);
        bw.Write(data.SheenMipCount);
        bw.Write(data.SheenLutSize);
        foreach (var mip in data.PrefilteredSheen ?? Array.Empty<Half[]>()) WriteHalves(bw, mip);
        if (data.SheenLut is { } sheenLut) bw.Write(sheenLut);
    }

    private static void WriteHalves(BinaryWriter bw, Half[] data)
    {
        var bytes = MemoryMarshal.AsBytes(data.AsSpan());
        bw.Write(bytes);
    }
}

public static class BlixProbeReader
{
    public static BlixProbeData Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CookPreamble.Read(fs, path).Require(BlixProbe.Magic, BlixProbe.Version4, path, ".blixprobe");
        return AssetImportException.Refusing(path, () => ReadBody(fs, path), ".blixprobe");
    }

    private static BlixProbeData ReadBody(Stream fs, string path)
    {
        using var br = new BinaryReader(fs);

        var flags = (BlixProbe.Flags)br.ReadUInt32();
        var envFace = br.ReadInt32();
        var irrFace = br.ReadInt32();
        var prefilterBase = br.ReadInt32();
        var prefilterMipCount = br.ReadInt32();
        var brdfLutSize = br.ReadInt32();
        var sun = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        var sunIrradiance = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

        var envCube = ReadHalves(br, 4 * 6 * envFace * envFace);
        var irrCube = ReadHalves(br, 4 * 6 * irrFace * irrFace);
        var prefilter = new Half[prefilterMipCount][];
        for (var k = 0; k < prefilterMipCount; k++)
        {
            var size = Math.Max(1, prefilterBase >> k);
            prefilter[k] = ReadHalves(br, 4 * 6 * size * size);
        }
        var brdfLut = br.ReadBytes(brdfLutSize * brdfLutSize * 4);
        if (brdfLut.Length != brdfLutSize * brdfLutSize * 4)
        {
            throw new InvalidDataException(
                $"'{path}' truncated reading BRDF LUT: got {brdfLut.Length} bytes, expected {brdfLutSize * brdfLutSize * 4}.");
        }

        var sheenFace = br.ReadInt32();
        var sheenMips = br.ReadInt32();
        var sheenLutSize = br.ReadInt32();
        Half[][]? prefilteredSheen = null;
        if (sheenMips > 0)
        {
            prefilteredSheen = new Half[sheenMips][];
            for (var k = 0; k < sheenMips; k++)
            {
                var size = Math.Max(1, sheenFace >> k);
                prefilteredSheen[k] = ReadHalves(br, 4 * 6 * size * size);
            }
        }
        byte[]? sheenLut = null;
        if (sheenLutSize > 0)
        {
            sheenLut = br.ReadBytes(sheenLutSize * sheenLutSize * 4);
            if (sheenLut.Length != sheenLutSize * sheenLutSize * 4)
            {
                throw new InvalidDataException(
                    $"'{path}' truncated reading sheen LUT: got {sheenLut.Length} bytes, " +
                    $"expected {sheenLutSize * sheenLutSize * 4}.");
            }
        }

        return new BlixProbeData(
            EnvFaceSize: envFace,
            IrradianceFaceSize: irrFace,
            PrefilterBaseSize: prefilterBase,
            PrefilterMipCount: prefilterMipCount,
            BrdfLutSize: brdfLutSize,
            SunDirection: flags.HasFlag(BlixProbe.Flags.HasSunDirection) ? sun : null,
            SunIrradiance: flags.HasFlag(BlixProbe.Flags.HasSunIrradiance) ? sunIrradiance : null,
            EnvCube: envCube,
            IrradianceCube: irrCube,
            PrefilteredSpecular: prefilter,
            BrdfLut: brdfLut,
            SheenFaceSize: sheenFace,
            SheenMipCount: sheenMips,
            SheenLutSize: sheenLutSize,
            PrefilteredSheen: prefilteredSheen,
            SheenLut: sheenLut);
    }

    private static Half[] ReadHalves(BinaryReader br, int count)
    {
        var bytes = br.ReadBytes(count * 2);
        if (bytes.Length != count * 2)
        {
            throw new InvalidDataException(
                $"Truncated read: expected {count * 2} bytes, got {bytes.Length}.");
        }
        var halves = new Half[count];
        bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(halves.AsSpan()));
        return halves;
    }
}
