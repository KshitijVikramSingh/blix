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
    // v3: the sun's IRRADIANCE rides with its direction. Without it a consumer knows where the sun
    // is and not how bright, so it reaches for a hand-tuned intensity — and since v3 also removes
    // the sun's disc from the diffuse and specular integrals, a reader that ignores this number is
    // rendering a sky with the sun taken out of it.
    public const uint Version3 = 3;
    // v4: the sheen half of the lighting model — a second prefiltered cube convolved with the
    // CHARLIE distribution, and the Charlie lobe's directional-albedo table. A GGX cube blurred
    // differently is not sheen: GGX distributes microfacets about the normal and cloth is fibres
    // standing away from it, so using the specular cube deletes the grazing rim that is the whole
    // visual signature of fabric. No back-read; re-cook, as every bump here has.
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
