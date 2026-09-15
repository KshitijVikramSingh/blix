using Blix.Cooked;
using Blix.Graphics.Images;

namespace Blix.Recipes;

/// <summary>
/// An equirectangular HDR to a <c>.blixprobe</c>: cube, irradiance, GGX prefilter chain, BRDF LUT.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one recipe whose output cannot be produced at load time.</b> The other two are
/// optimisations — a <c>.blixmesh</c> saves a parse and a <c>.blixtex</c> saves a decode, and
/// without either the engine still runs. Convolving an environment map and integrating a BRDF LUT
/// cost seconds at startup, every startup, so this one is the reason cooking exists rather than a
/// way of making it faster.
/// </para>
/// <para>
/// That difference is why a single Source/Cooked flag is not enough to report with: three recipes,
/// three economics — load time, GPU memory, and work that has nowhere else to happen.
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
        float clamp)
    {
        ArgumentNullException.ThrowIfNull(hdrPath);
        ArgumentNullException.ThrowIfNull(outPath);

        var hdr = ImageLoader.LoadRgba32F(hdrPath);
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

        // Every knob that changes the bake, recorded verbatim — including clamp, which was the one
        // probe parameter the old header did NOT carry. Authored order, so the string is stable and
        // a byte-compare between two cooks means something.
        var stamp = CookStamp.Of(
            BlixProbe.ShippedRecipe, BlixProbe.ShippedRecipeVersion, hdrPath, outPath,
            $"env={envFace} irr={irrFace} prefilterBase={prefilterBase} prefilterMips={prefilterMips} " +
            $"brdf={brdfSize} clamp={clamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

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
            clamp: request.Real("clamp", 50f));

        return CookOutcome.Written($"{bytes / 1024.0 / 1024.0:0.00} MB");
    }
}
