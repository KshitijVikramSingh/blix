using System.IO;
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

    /// <summary>
    /// The sun's irradiance in the source's own units, when it was measured and removed.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes a directional light and this probe commensurable.</b> Both come out of
    /// one capture, so a light built from this number needs no scale factor against the IBL — and a
    /// scale factor nobody can derive is precisely the hand-tuned knob it replaces.
    /// <para>
    /// Non-null implies the disc was removed from the diffuse and specular integrals, so a consumer
    /// that ignores this value is not merely missing the sun — it is rendering a sky that has had
    /// the sun taken out of it.
    /// </para>
    /// </remarks>
    public Vector3? SunIrradiance { get; init; }
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
    /// <param name="cacheDirectory">
    /// Where to keep the computed table between runs, or null to compute it every time.
    /// </param>
    /// <remarks>
    /// <b>The table is a pure function of its size and sample count, so caching it is always safe —
    /// and recomputing it per process is simply wrong.</b> It is the Karis split-sum integration over
    /// (NdotV, roughness) and depends on nothing else: not the environment, not the sun, not the
    /// model. Measured on one laptop it is O(n²) and not cheap — 32 → 25 ms, 64 → 84 ms, 128 → 407 ms,
    /// 256 → 1451 ms — which at the default made it 96% of a procedural environment bake.
    /// <para>
    /// <b>The caller decides WHERE, or whether at all.</b> An engine that picks a directory has
    /// picked it for a game, a tool and a test alike; the mechanism is "compute this once and keep
    /// it", and where a process is allowed to write is the caller's business.
    /// </para>
    /// <para>
    /// Every cache failure falls back to computing. A missing directory, a read-only volume, a
    /// truncated file: none of them is a reason for a renderer not to start, and a cache that can
    /// break the thing it accelerates is worse than no cache.
    /// </para>
    /// </remarks>
    public static TextureHandle BakeBrdfLut(
        IGraphicsDevice device, int size = 256, string name = "brdf_lut", string? cacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var bytes = BrdfLutBytes(size, cacheDirectory);
        return device.CreateTexture2D(
            new TextureDescription(size, size, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            bytes, name);
    }

    private static byte[] BrdfLutBytes(int size, string? cacheDirectory)
    {
        // The sample count is the other half of what the table is a function of. It is not a
        // parameter here today, but naming it in the file keeps a future change from silently
        // reading a table baked at a different quality.
        const int sampleCount = 1024;
        var expected = size * size * 4;
        var path = cacheDirectory is null
            ? null
            : Path.Combine(cacheDirectory, $"brdf_lut_{size}_{sampleCount}.bin");

        if (path is not null)
        {
            try
            {
                // Length is the whole validation, and it is enough: the name carries every input,
                // so a file of the right length under the right name cannot be the wrong table
                // unless someone has written garbage into it deliberately.
                var cached = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (cached is not null && cached.Length == expected) return cached;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var bytes = PbrIblBaker.BakeBrdfLut(size, sampleCount);

        if (path is not null)
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory!);
                // Written beside and moved into place, so a process killed mid-write leaves no
                // half-file for the next run to read as a table.
                var temp = path + ".partial";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return bytes;
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

        // The visible sky keeps its sun; the lighting integrals below do not. See
        // HdrSunFinder.WithoutSun — a renderer that adds a directional light for this same sun
        // would otherwise deliver its energy twice, which no intensity can balance.
        var envPixels = EquirectangularToCubemap.Convert(hdr.Equirect, profile.EnvCubeFaceSize);
        var measured = HdrSunFinder.FindSun(hdr.Equirect);
        var sun = measured?.Direction;
        var lighting = measured is { } found ? HdrSunFinder.WithoutSun(hdr.Equirect, found) : hdr.Equirect;

        var irrPixels = PbrIblBaker.BakeDiffuseIrradiance(
            lighting, profile.IrradianceFaceSize,
            sampleClampMagnitude: profile.SampleClampMagnitude);
        var prefilter = PbrIblBaker.BakeSpecularPrefilteredMips(
            lighting, profile.SpecularPrefilterBaseSize,
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
            SunIrradiance: measured?.Irradiance,
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
            SunIrradiance = data.SunIrradiance,
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

        // <b>The sun is measured, then taken out of the LIGHTING integrals only.</b> The env cube
        // above keeps it — that is the sky people see and mirrors reflect. What follows must not,
        // or its energy arrives twice: once through the irradiance and prefilter, and again through
        // whatever directional light the renderer aims along SunDirectionFromEquirect.
        var sun = HdrSunFinder.FindSun(hdr.Equirect);
        var sunDir = sun?.Direction;
        var lighting = sun is { } found ? HdrSunFinder.WithoutSun(hdr.Equirect, found) : hdr.Equirect;

        var irradiancePixels = PbrIblBaker.BakeDiffuseIrradiance(
            lighting, profile.IrradianceFaceSize,
            sampleClampMagnitude: profile.SampleClampMagnitude);
        var irradianceCube = device.CreateTextureCubeHdr(
            profile.IrradianceFaceSize, irradiancePixels,
            SamplerDescription.LinearClampMipmap,
            name: $"{namePrefix}.env_irradiance");

        var prefilteredMips = PbrIblBaker.BakeSpecularPrefilteredMips(
            lighting, profile.SpecularPrefilterBaseSize,
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
            SunIrradiance = sun?.Irradiance,
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
