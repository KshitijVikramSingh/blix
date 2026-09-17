using System.Numerics;

namespace Blix.Graphics.Images;

/// <summary>
/// The CPU twin of <c>Blix.Shaders/tonemap.glsl</c>: HDR-linear in, [0,1] LDR-linear out.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because a capture is taken before the present pass runs.</b> The capture tool reads
/// back the HDR scene target rather than the swapchain, which is what lets a capture hold real
/// radiance and makes the curve an offline choice — but it also means the curve has to be applied
/// here, in C#, by something. One copy of that already existed, hardcoded to ACES, and the result was
/// that <c>--tonemap-mode</c> moved the screen and not the picture.
/// </para>
/// <para>
/// <b>This is a second implementation of a thing that lives in GLSL, and that is a real hazard.</b>
/// The honest response is not a comment saying so — there was already one of those, reading "if a
/// capture ever disagrees with the screen, this pair is where to look first". It is
/// <c>Blix.Test.Graphics</c> section BD, which reads <c>tonemap.glsl</c> and fails if the constants
/// below have stopped appearing in it. A text check is crude next to reflecting an interface out of
/// SPIR-V, but it is the same idea: make the second copy answerable to the first rather than trusting
/// two files to stay in step.
/// </para>
/// </remarks>
public static class Tonemap
{
    // Narkowicz 2015 fit of the ACES filmic curve. Named rather than inlined so the drift check has
    // something to look for in the GLSL.
    public const float AcesA = 2.51f;
    public const float AcesB = 0.03f;
    public const float AcesC = 2.43f;
    public const float AcesD = 0.59f;
    public const float AcesE = 0.14f;

    /// <summary>AgX's log-space window, in stops.</summary>
    public const float AgxMinEv = -12.47393f;

    /// <inheritdoc cref="AgxMinEv"/>
    public const float AgxMaxEv = 4.026069f;

    /// <summary>ACES filmic. Saturated shadows, smooth highlight rolloff, a warm bias.</summary>
    public static Vector3 Aces(Vector3 x) => new(
        Aces(x.X), Aces(x.Y), Aces(x.Z));

    private static float Aces(float x) =>
        Math.Clamp((x * (AcesA * x + AcesB)) / (x * (AcesC * x + AcesD) + AcesE), 0f, 1f);

    /// <summary>Classic Reinhard. Flatter than ACES, gentler rolloff.</summary>
    public static Vector3 Reinhard(Vector3 x) => new(
        x.X / (1f + x.X), x.Y / (1f + x.Y), x.Z / (1f + x.Z));

    /// <summary>No curve, just a clamp. The reference that shows where values are clipping.</summary>
    public static Vector3 Neutral(Vector3 x) => new(
        Math.Clamp(x.X, 0f, 1f), Math.Clamp(x.Y, 0f, 1f), Math.Clamp(x.Z, 0f, 1f));

    /// <summary>Approximated AgX. Filmic but more neutral than ACES; keeps hue in bright colour.</summary>
    public static Vector3 Agx(Vector3 x) => new(
        Agx(x.X), Agx(x.Y), Agx(x.Z));

    private static float Agx(float x)
    {
        var logX = MathF.Log2(MathF.Max(MathF.Max(x, 0f), 1e-6f));
        var t = Math.Clamp((logX - AgxMinEv) / (AgxMaxEv - AgxMinEv), 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    /// <summary>
    /// The mode selector, with the same thresholds the shader's <c>blix_tonemap</c> uses.
    /// </summary>
    /// <remarks>
    /// A float rather than an enum because that is what the uniform carries, and the thresholds are
    /// the convention the engine already had: &lt;0.5 ACES, &lt;1.5 AgX, &lt;2.5 Reinhard, else
    /// neutral. Restating them here is exactly what the drift check watches.
    /// </remarks>
    public static Vector3 Apply(Vector3 hdr, float mode) => mode switch
    {
        < 0.5f => Aces(hdr),
        < 1.5f => Agx(hdr),
        < 2.5f => Reinhard(hdr),
        _ => Neutral(hdr),
    };
}
