using System.Numerics;
using Blix.Graphics;

namespace Blix.Graphics.Images;

// Environment authoring + baking. One demo previously hand-rolled all of this
// inline (load HDR, equirect-to-cube, run PbrIblBaker three times, alias
// handles when HDR was missing, manage rebake-on-sun-change for the
// procedural sky). It's enough engine machinery to be a shared subsystem:
// every PBR demo wants the same probes, the same source dichotomy, and the
// same rebake story.
//
// The split:
//   - EnvironmentProfile (data): which source, sizes, sample clamp. Mutable
//     in the demo so sliders and presets can edit it; immutable from the
//     bake's perspective.
//   - EnvironmentProbe (baked GPU outputs): env cubemap + IBL probes + the
//     sun direction extracted from an HDR if available. Returned by Bake.
//   - EnvironmentBaker (the stateless function): given a device + profile +
//     name prefix, produces a Probe. Re-calling makes a fresh Probe (old
//     handles leak; matches existing demo's deliberate "the old cube is
//     left to leak" pattern for rebake-on-slider).
//
// The BRDF LUT is environment-independent and expensive to compute (1024
// samples per texel), so it lives outside the Probe -- bake once, reuse
// across every Probe re-bake.

public abstract record EnvironmentSource;

// HDR equirect source. The caller decodes the file (ImageLoader.LoadRgba32F)
// so the Profile stays data-only; nothing here touches the file system.
public sealed record HdrEnvironmentSource(HdrImageData Equirect) : EnvironmentSource;

// Procedural-sky source. SunDirection points FROM-sun-INTO-scene (matches
// Blix's directional-light convention).
public sealed record ProceduralEnvironmentSource(Vector3 SunDirection) : EnvironmentSource;

public sealed record EnvironmentProfile
{
    public required EnvironmentSource Source { get; init; }
    // Visible-sky cube face resolution. Auto-mipped on upload for the cheap
    // mip-LOD specular fallback (when the source is procedural or the demo
    // wants to skip the GGX prefilter cost). 256 is the demo's default.
    public int EnvCubeFaceSize { get; init; } = 256;
    // Diffuse irradiance cube size. The cosine-weighted convolution is very
    // smooth so high resolution is wasted. 32 is fine.
    public int IrradianceFaceSize { get; init; } = 32;
    // GGX-prefiltered specular: top mip resolution and total mip count.
    // Mip K stores the env convolved at roughness K / (mipCount - 1).
    public int SpecularPrefilterBaseSize { get; init; } = 128;
    public int SpecularPrefilterMipCount { get; init; } = 5;
    // Firefly clamp: single-pixel suns in Polyhaven HDRIs otherwise produce
    // visible speckle on normal-mapped surfaces. 50 is the empirically-
    // safe ceiling -- preserves visible sun in the sky AND clean IBL probes.
    public float SampleClampMagnitude { get; init; } = 50.0f;
}

public sealed class EnvironmentProbe
{
    // Visible-sky cubemap. Both the skybox pass and the lit shader's IBL
    // fallback (when PrefilteredSpecular isn't a real GGX bake) sample this.
    public required TextureHandle EnvCubemap { get; init; }
    public required int EnvCubeMipCount { get; init; }
    // Cos-weighted diffuse irradiance. Sampled by the lit shader for the
    // diffuse IBL term. Equals EnvCubemap when the source was procedural --
    // accept the directional smear, avoid a black diffuse.
    public required TextureHandle DiffuseIrradiance { get; init; }
    // GGX-prefiltered specular with explicit mip per roughness step.
    // Sampled via textureLod(R, roughness * (mipCount - 1)) in the lit
    // shader's split-sum specular path. Equals EnvCubemap (with auto-mips)
    // when the source was procedural.
    public required TextureHandle PrefilteredSpecular { get; init; }
    public required int PrefilteredSpecularMipCount { get; init; }
    // Set when the source was HDR and the brightest pixel cluster in the
    // upper hemisphere passed the threshold. Use to auto-align the demo's
    // directional light with the visible sun. Null for procedural sources
    // (the procedural sky already takes its sun direction from the profile).
    public Vector3? SunDirectionFromEquirect { get; init; }
}

// Full IBL bundle returned by the cooked-probe load path: the on-GPU
// EnvironmentProbe plus the BRDF LUT that the lit shader's split-sum
// integration needs. Convenient single-return for demos that previously
// called Bake + BakeBrdfLut sequentially.
public sealed record BakedEnvironment(
    EnvironmentProbe Probe,
    TextureHandle BrdfLut);

public static class EnvironmentBaker
{
    // Bakes the env cube + IBL probes from a profile. The BRDF LUT is
    // environment-independent; bake it once via BakeBrdfLut and reuse.
    public static EnvironmentProbe Bake(IGraphicsDevice device, EnvironmentProfile profile, string namePrefix)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(namePrefix);

        return profile.Source switch
        {
            HdrEnvironmentSource hdr => BakeFromHdr(device, profile, hdr, namePrefix),
            ProceduralEnvironmentSource proc => BakeFromProcedural(device, profile, proc, namePrefix),
            _ => throw new ArgumentException(
                $"Unknown EnvironmentSource type: {profile.Source.GetType().Name}",
                nameof(profile)),
        };
    }

    // Karis split-sum 2D BRDF LUT. R = scale, G = bias. Lit shader does
    // F * scale + bias to get the GGX-integrated specular term. Env-
    // independent, so call once and reuse across every probe rebake.
    public static TextureHandle BakeBrdfLut(IGraphicsDevice device, int size = 256, string name = "brdf_lut")
    {
        ArgumentNullException.ThrowIfNull(device);
        var bytes = PbrIblBaker.BakeBrdfLut(size);
        return device.CreateTexture2D(
            new TextureDescription(size, size, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            bytes, name);
    }

    // CPU-only bake. Runs the equirect-to-cube + diffuse irradiance +
    // GGX prefilter + BRDF LUT integrations and packs the results into a
    // BlixProbeData blob suitable for BlixProbeWriter. Only HDR sources
    // are supported -- procedural skies depend on a runtime sun direction
    // and are cheap enough to bake every frame anyway.
    public static BlixProbeData CookHdrProbeData(
        EnvironmentProfile profile,
        int brdfLutSize = 256)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Source is not HdrEnvironmentSource hdr)
        {
            throw new ArgumentException(
                "Only HDR environment sources can be cooked. Procedural skies depend on a runtime sun direction.",
                nameof(profile));
        }

        var envPixels = EquirectangularToCubemap.Convert(hdr.Equirect, profile.EnvCubeFaceSize);
        var sun = HdrSunFinder.FindSunDirection(hdr.Equirect);
        var irrPixels = PbrIblBaker.BakeDiffuseIrradiance(
            hdr.Equirect, profile.IrradianceFaceSize,
            sampleClampMagnitude: profile.SampleClampMagnitude);
        var prefilter = PbrIblBaker.BakeSpecularPrefilteredMips(
            hdr.Equirect, profile.SpecularPrefilterBaseSize,
            profile.SpecularPrefilterMipCount,
            sampleClampMagnitude: profile.SampleClampMagnitude);
        var brdfLut = PbrIblBaker.BakeBrdfLut(brdfLutSize);

        return new BlixProbeData(
            EnvFaceSize: profile.EnvCubeFaceSize,
            IrradianceFaceSize: profile.IrradianceFaceSize,
            PrefilterBaseSize: profile.SpecularPrefilterBaseSize,
            PrefilterMipCount: profile.SpecularPrefilterMipCount,
            BrdfLutSize: brdfLutSize,
            SunDirection: sun,
            EnvCube: envPixels,
            IrradianceCube: irrPixels,
            PrefilteredSpecular: prefilter,
            BrdfLut: brdfLut);
    }

    // Upload a previously-cooked BlixProbeData blob into a BakedEnvironment.
    // No HDR sampling, no integration -- pure memcpy from CPU arrays to GPU
    // texture storage.
    public static BakedEnvironment UploadCookedProbe(
        IGraphicsDevice device,
        BlixProbeData data,
        string namePrefix)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(namePrefix);

        var envCube = device.CreateTextureCubeHdr(
            data.EnvFaceSize, data.EnvCube,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_cube.hdr");
        var envMipCount = (int)MathF.Floor(MathF.Log2(data.EnvFaceSize)) + 1;

        var irrCube = device.CreateTextureCubeHdr(
            data.IrradianceFaceSize, data.IrradianceCube,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_irradiance");

        var prefilterCube = device.CreateTextureCubeHdrMipped(
            data.PrefilterBaseSize, data.PrefilteredSpecular,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_prefilter");

        var brdfLut = device.CreateTexture2D(
            new TextureDescription(data.BrdfLutSize, data.BrdfLutSize,
                TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            data.BrdfLut,
            name: $"{namePrefix}.brdf_lut");

        var probe = new EnvironmentProbe
        {
            EnvCubemap = envCube,
            EnvCubeMipCount = envMipCount,
            DiffuseIrradiance = irrCube,
            PrefilteredSpecular = prefilterCube,
            PrefilteredSpecularMipCount = data.PrefilterMipCount,
            SunDirectionFromEquirect = data.SunDirection,
        };
        return new BakedEnvironment(probe, brdfLut);
    }

    private static EnvironmentProbe BakeFromHdr(IGraphicsDevice device, EnvironmentProfile profile, HdrEnvironmentSource hdr, string namePrefix)
    {
        var cubePixels = EquirectangularToCubemap.Convert(hdr.Equirect, profile.EnvCubeFaceSize);
        var envCube = device.CreateTextureCubeHdr(
            profile.EnvCubeFaceSize, cubePixels,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_cube.hdr");

        var envMipCount = (int)MathF.Floor(MathF.Log2(profile.EnvCubeFaceSize)) + 1;

        var sunDir = HdrSunFinder.FindSunDirection(hdr.Equirect);

        var irradiancePixels = PbrIblBaker.BakeDiffuseIrradiance(
            hdr.Equirect, profile.IrradianceFaceSize,
            sampleClampMagnitude: profile.SampleClampMagnitude);
        var irradianceCube = device.CreateTextureCubeHdr(
            profile.IrradianceFaceSize, irradiancePixels,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_irradiance");

        var prefilteredMips = PbrIblBaker.BakeSpecularPrefilteredMips(
            hdr.Equirect, profile.SpecularPrefilterBaseSize,
            profile.SpecularPrefilterMipCount,
            sampleClampMagnitude: profile.SampleClampMagnitude);
        var prefilteredCube = device.CreateTextureCubeHdrMipped(
            profile.SpecularPrefilterBaseSize, prefilteredMips,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_prefilter");

        return new EnvironmentProbe
        {
            EnvCubemap = envCube,
            EnvCubeMipCount = envMipCount,
            DiffuseIrradiance = irradianceCube,
            PrefilteredSpecular = prefilteredCube,
            PrefilteredSpecularMipCount = profile.SpecularPrefilterMipCount,
            SunDirectionFromEquirect = sunDir,
        };
    }

    private static EnvironmentProbe BakeFromProcedural(IGraphicsDevice device, EnvironmentProfile profile, ProceduralEnvironmentSource proc, string namePrefix)
    {
        var pixels = CubemapBaker.BakeSky(profile.EnvCubeFaceSize, proc.SunDirection);
        var envCube = device.CreateTextureCubeHdr(
            profile.EnvCubeFaceSize, pixels,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_cube.procedural");
        var envMipCount = (int)MathF.Floor(MathF.Log2(profile.EnvCubeFaceSize)) + 1;

        // No real IBL bake for procedural -- alias the env cube into the
        // diffuse + prefiltered slots. The auto-generated mips give a cheap
        // roughness blur for the specular path. Loses directional diffuse
        // variation but avoids a black indirect term.
        return new EnvironmentProbe
        {
            EnvCubemap = envCube,
            EnvCubeMipCount = envMipCount,
            DiffuseIrradiance = envCube,
            PrefilteredSpecular = envCube,
            PrefilteredSpecularMipCount = envMipCount,
            SunDirectionFromEquirect = null,
        };
    }
}
