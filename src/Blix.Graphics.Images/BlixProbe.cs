using Blix.Cooked;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Graphics.Images;

// Engine-native binary IBL probe container. One file per HDR sky source --
// bundles the equirect-to-cube conversion, the GGX-prefiltered specular
// mip pyramid, the cosine-weighted diffuse irradiance cube, the BRDF
// LUT, and the auto-detected sun direction. Cooking is offline (the same
// Blix.Tools.Cook tool that produces .blixtex); loading at runtime is a
// header read + four blob copies into GPU textures, replacing the ~2s
// of equirect convolution + ~1.6s BRDF LUT integration the runtime
// EnvironmentBaker would otherwise burn at startup.
//
// File layout (little-endian), v2:
//
//   The shared Blix cooked preamble first -- see Blix.Cooked/CookPreamble.cs.
//   Then this format's own header, at offsets relative to the end of it:
//
//   offset  size  field
//   ---------------------------------
//   0       4     flags       (bit 0 = has sun direction)
//   4       4     envFaceSize
//   8       4     irrFaceSize
//   12      4     prefilterBaseSize
//   16      4     prefilterMipCount
//   20      4     brdfLutSize
//   24      12    sunDirection (vec3 float32, valid only when flag bit 0)
//   --------- 36 bytes (format header) ---------
//                 envCube      : 4 channels * 6 faces * envFaceSize^2     Halves (RGBA16F)
//                 irrCube      : 4 channels * 6 faces * irrFaceSize^2     Halves (RGBA16F)
//                 prefilter[k] : 4 * 6 * (prefilterBaseSize>>k)^2 Halves, k=0..prefilterMipCount-1
//                 brdfLut      : brdfLutSize * brdfLutSize * 4 bytes      (RGBA8)
//
// All blobs are concatenated tightly in the order above. No per-blob
// length fields -- sizes are recoverable from the header dimensions.
public static class BlixProbe
{
    public const uint Magic = 0x50584C42; // "BLXP" little-endian

    // v2: the shared cooked preamble replaces the private magic+version pair.
    // This format already recorded five of its six cook parameters in its own
    // header -- the one of the three that had worked out it should be
    // reproducible -- and the preamble generalises that to all of them.
    public const uint Version2 = 2;
    public const int HeaderSize = 36;

    /// <summary>The recipe id the shipped probe cook stamps.</summary>
    public const string ShippedRecipe = "gpro";

    /// <summary>The probe cook's own version — see BlixMesh.MeshRecipeVersion for why.</summary>
    public const uint ShippedRecipeVersion = 1;

    [Flags]
    public enum Flags : uint
    {
        None = 0,
        HasSunDirection = 1 << 0,
    }
}

public sealed record BlixProbeData(
    int EnvFaceSize,
    int IrradianceFaceSize,
    int PrefilterBaseSize,
    int PrefilterMipCount,
    int BrdfLutSize,
    Vector3? SunDirection,
    Half[] EnvCube,
    Half[] IrradianceCube,
    Half[][] PrefilteredSpecular,
    byte[] BrdfLut);

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
        CookPreamble.Write(fs, BlixProbe.Magic, BlixProbe.Version2, stamp);
        using var bw = new BinaryWriter(fs);

        var flags = BlixProbe.Flags.None;
        if (data.SunDirection.HasValue) flags |= BlixProbe.Flags.HasSunDirection;

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

        WriteHalves(bw, data.EnvCube);
        WriteHalves(bw, data.IrradianceCube);
        foreach (var mip in data.PrefilteredSpecular)
        {
            WriteHalves(bw, mip);
        }
        bw.Write(data.BrdfLut);
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
        CookPreamble.Read(fs, path).Require(BlixProbe.Magic, BlixProbe.Version2, path, ".blixprobe");
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

        return new BlixProbeData(
            EnvFaceSize: envFace,
            IrradianceFaceSize: irrFace,
            PrefilterBaseSize: prefilterBase,
            PrefilterMipCount: prefilterMipCount,
            BrdfLutSize: brdfLutSize,
            SunDirection: flags.HasFlag(BlixProbe.Flags.HasSunDirection) ? sun : null,
            EnvCube: envCube,
            IrradianceCube: irrCube,
            PrefilteredSpecular: prefilter,
            BrdfLut: brdfLut);
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
