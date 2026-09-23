using Blix.Cooked;
using Blix.Graphics.Images;

namespace Blix.Recipes;

/// <summary>
/// An equirectangular HDR to a <c>.blixprobe</c>: cube, irradiance, GGX prefilter chain, BRDF LUT.
/// </summary>
/// <remarks>
/// <para>
/// Probe convolution and BRDF integration are intentionally cook-time work. Unlike source-decoding
/// fallbacks for meshes, textures, and fonts, the runtime probe path expects the baked product.
/// </para>
/// <para>
/// This is why load reports retain cost and recipe identity rather than reducing cooking to a
/// source/cooked boolean: different artifacts remove different kinds of runtime work.
/// </para>
/// </remarks>
public static class ProbeRecipe
{
    /// <summary>Bakes one HDR into one probe. No console output: the driver owns reporting.</summary>
    public static long CookOne(
        string hdrPath,
        string outPath,
        int envFace,
        int irrFace,
        int prefilterBase,
        int prefilterMips,
        int brdfSize,
        float clamp,
        float yawDegrees = 0f)
    {
        ArgumentNullException.ThrowIfNull(hdrPath);
        ArgumentNullException.ThrowIfNull(outPath);

        // Apply yaw before profiling or convolution so the sun direction, cube, irradiance, and
        // prefilter chain all describe the same rotated environment.
        var hdr = EquirectYaw.Rotate(ImageLoader.LoadRgba32F(hdrPath), yawDegrees);
        var profile = new EnvironmentProfile
        {
            Source = new HdrEnvironmentSource(hdr),
            EnvCubeFaceSize = envFace,
            IrradianceFaceSize = irrFace,
            SpecularPrefilterBaseSize = prefilterBase,
            SpecularPrefilterMipCount = prefilterMips,
            SampleClampMagnitude = clamp,
        };
        var data = EnvironmentBaker.CookHdrProbeData(profile, brdfSize);

        // Record every bake input in stable authored order.
        var stamp = CookStamp.Of(
            BlixProbe.ShippedRecipe, BlixProbe.ShippedRecipeVersion, hdrPath, outPath,
            $"env={envFace} irr={irrFace} prefilterBase={prefilterBase} prefilterMips={prefilterMips} " +
            $"brdf={brdfSize} clamp={clamp.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"yaw={yawDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        BlixProbeWriter.Write(outPath, data, stamp);
        return new FileInfo(outPath).Length;
    }

    /// <summary>The uniform entry point the index finds and <c>blix cook</c> calls.</summary>
    /// <remarks>
    /// The defaults here are the CLI's defaults, in one place rather than two. They are real
    /// choices — 256 env faces and a 5-mip prefilter chain are what Sponza was tuned against — and
    /// they go into the stamp either way, so a file cooked with defaults says so rather than
    /// staying silent about it.
    /// </remarks>
    [Recipe(BlixProbe.ShippedRecipe,
        Produces = ".blixprobe",
        Consumes = ".hdr",
        Version = BlixProbe.ShippedRecipeVersion,
        Summary = "an HDR sky to a baked IBL probe")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = CookOne(
            request.SourcePath,
            request.OutputPath,
            envFace: request.Number("env", 256),
            irrFace: request.Number("irr", 32),
            prefilterBase: request.Number("prefilterBase", 128),
            prefilterMips: request.Number("prefilterMips", 5),
            brdfSize: request.Number("brdf", 256),
            clamp: request.Real("clamp", 50f),
            yawDegrees: request.Real("yaw"));

        return CookOutcome.Written($"{bytes / 1024.0 / 1024.0:0.00} MB");
    }
}
