using System.Numerics;
using Blix.Diagnostics;

namespace Blix.Tools.Studio;

/// <summary>
/// Every dial that decides what the stage looks like — Blix's house style, written down.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are Blix's optional reference look. Core engine layers express no visual opinion;
/// applications may take, modify, or ignore this authored composition.
/// </para>
/// <para>
/// Members are grouped by the pass that reads them while this type owns the authored decision across
/// passes. Ordinary members are live; <see cref="TuneAttribute.Structural"/> members are consumed when
/// targets and pipelines are built and must be set before the renderer loads.
/// </para>
/// </remarks>
public sealed class StudioLook : ITunable
{
    /// <summary>Degrees around Y, from +Z toward +X.</summary>
    [Tune(0, 360, Group = "sun")] public float SunAzimuth { get; set; } = 52.125f;

    /// <summary>Degrees above the horizon. Not 90: straight down has no stable up vector.</summary>
    [Tune(0, 89, Group = "sun")] public float SunElevation { get; set; } = 54.526f;

    /// <summary>Scales the sun's tint. One is the light this stage was authored under.</summary>
    [Tune(0, 3, Group = "sun")] public float SunIntensity { get; set; } = 1f;

    /// <summary>How much of the environment reaches what the sun does not.</summary>
    /// <remarks>
    /// Scales directional IBL when enabled and the flat fill otherwise. The 0.30 default keeps the
    /// Rogue's shaded side legible without weakening the key light; zero remains a useful inspection
    /// mode for isolating direct-light contribution.
    /// </remarks>
    [Tune(0, 0.5f, Group = "sun")] public float AmbientStrength { get; set; } = 0.30f;

    /// <summary>Metres around the origin the SHARPEST cascade covers — the subject's own box.</summary>
    /// <remarks>
    /// This is the innermost cascade radius. Studio normalises a model to roughly three units, so
    /// 2.7 m contains the subject and typical attachments. Raising it trades contact-shadow detail
    /// for reach.
    /// </remarks>
    [Tune(1, 12, Group = "shadow")] public float ShadowSubjectRadius { get; set; } = 2.7f;

    /// <summary>How far from the camera shadows are cast, in metres — the last cascade's far bound.</summary>
    /// <remarks>The 30 m default covers the 12 m stage from the far side of the camera orbit.</remarks>
    [Tune(5, 120, Group = "shadow")] public float ShadowDistance { get; set; } = 30f;

    /// <summary>How the three cascades divide the range: 0 splits evenly, 1 logarithmically.</summary>
    /// <remarks>0.85 concentrates resolution around Studio's small, close subject.</remarks>
    [Tune(0, 1, Group = "shadow")] public float CascadeSplitLambda { get; set; } = 0.85f;

    /// <summary>Paint each cascade a flat colour instead of shading. Magenta = past the last one.</summary>
    /// <remarks>Use this to verify that all three caster passes cover distinct useful regions.</remarks>
    [Tune(Group = "shadow")] public bool ShowCascades { get; set; }

    /// <summary>Whether the floor is drawn. Off is how you look at a thing against nothing.</summary>
    /// <remarks>The floor is lit geometry that receives shadows; the overlaid grid is a gizmo.</remarks>
    [Tune(Group = "stage")] public bool Ground { get; set; } = true;

    // ── structural: read when the graph is built, never again ────────────────────────────────

    /// <summary>Whether the environment lights the scene, or a flat fill stands in for it.</summary>
    /// <remarks>Structural because the environment is baked once during renderer loading.</remarks>
    [Tune(Group = "environment", Structural = true)]
    public bool ImageBasedLighting
    {
        get => imageBasedLighting;
        set { Seal(nameof(ImageBasedLighting), imageBasedLighting != value); imageBasedLighting = value; }
    }

    private bool imageBasedLighting = true;

    /// <summary>Faces of the prefiltered specular cube, at mip 0.</summary>
    /// <remarks>
    /// 128 is the engine profile's own default and it is plenty for a stage whose job is to make one
    /// object legible: the specular cube is read at a roughness-selected mip, so all but the
    /// smoothest materials are reading a blurred level of it anyway.
    /// </remarks>
    [Tune(32, 512, Group = "environment", Structural = true)]
    public int EnvFaceSize
    {
        get => envFaceSize;
        set { Seal(nameof(EnvFaceSize), envFaceSize != value); envFaceSize = value; }
    }

    private int envFaceSize = 128;

    /// <summary>Mip levels of the prefiltered specular cube, each a rougher GGX convolution.</summary>
    /// <remarks>The shader derives its LOD ceiling from the baked result; GLSL must not restate it.</remarks>
    [Tune(2, 8, Group = "environment", Structural = true)]
    public int EnvMipCount
    {
        get => envMipCount;
        set { Seal(nameof(EnvMipCount), envMipCount != value); envMipCount = value; }
    }

    private int envMipCount = 5;

    /// <summary>Samples per pixel in the scene pass. 1 is off; 2, 4 and 8 are the usual answers.</summary>
    /// <remarks>
    /// Structural because sample count is baked into render passes, images, and pipelines. The main
    /// turntable uses 4x for silhouette quality; the secondary panel viewport remains single-sample.
    /// Scene depth is resolved when necessary so presentation and gizmos can sample it.
    /// </remarks>
    [Tune(1, 8, Group = "present", Structural = true)]
    public int MsaaSamples
    {
        get => msaaSamples;
        set { Seal(nameof(MsaaSamples), msaaSamples != value); msaaSamples = value; }
    }

    private int msaaSamples = 4;

    /// <summary>Lay depth down in a cheap pass first, so the lit pass shades fewer fragments.</summary>
    /// <remarks>
    /// Off by default: Studio's usual handful of objects does not repay another geometry pass and
    /// two pipelines. The option remains for fragment-heavy views such as terrain or crowds; enable
    /// it only with a workload-specific measurement.
    /// </remarks>
    [Tune(Group = "shadow", Structural = true)]
    public bool DepthPrePass
    {
        get => depthPrePass;
        set { Seal(nameof(DepthPrePass), depthPrePass != value); depthPrePass = value; }
    }

    private bool depthPrePass;

    /// <summary>Side of each cascade's shadow map, in texels.</summary>
    /// <remarks>
    /// Structural: the depth targets are sized when the graph is built. Three maps at this size,
    /// not one — so raising it costs three times what it looks like it costs.
    /// </remarks>
    [Tune(256, 4096, Group = "shadow", Structural = true)]
    public int ShadowMapSize
    {
        get => shadowMapSize;
        set { Seal(nameof(ShadowMapSize), shadowMapSize != value); shadowMapSize = value; }
    }

    private int shadowMapSize = 2048;

    /// <summary>Edge of the split-sum BRDF lookup table.</summary>
    /// <remarks>
    /// 64 keeps procedural startup near 84 ms on the measured machine; 256 took 1.45 s. A Rogue
    /// capture differed from 256 on 0.11% of pixels with a maximum 1/255 delta. Cooked probes carry
    /// their own LUT and do not pay this procedural fallback cost.
    /// </remarks>
    [Tune(32, 256, Group = "environment", Structural = true)]
    public int BrdfLutSize
    {
        get => brdfLutSize;
        set { Seal(nameof(BrdfLutSize), brdfLutSize != value); brdfLutSize = value; }
    }

    private int brdfLutSize = 64;

    // ── per-frame ────────────────────────────────────────────────────────────────────────────

    /// <summary>Exposure applied before tonemapping.</summary>
    [Tune(0, 4, Group = "present")] public float Exposure { get; set; } = 1.0f;

    /// <summary>0 = ACES, 1 = AgX, 2 = Reinhard, 3 = neutral. Matches blix_tonemap.</summary>
    [Tune(0, 3, Group = "present")] public float TonemapMode { get; set; }

    /// <summary>Direction TOWARD the sun. Derived from the two angles.</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));

    /// <summary>Derived: the tint this stage was authored with, scaled by <see cref="SunIntensity"/>.</summary>
    public Vector3 SunColour { get; private set; } = new(3.2f, 3.05f, 2.75f);

    private static readonly Vector3 SunTint = new(3.2f, 3.05f, 2.75f);

    /// <summary>
    /// Derives the look once, so the declared angles and the derived vector agree from frame one.
    /// </summary>
    /// <remarks>Construction computes derived vectors immediately; UI values and rendering agree.</remarks>
    public StudioLook() => Recompute();

    private bool structuralSealed;

    /// <summary>
    /// The graph has been built: from here a structural change cannot take effect.
    /// </summary>
    /// <remarks>Later structural changes are rejected loudly because their resources already exist.</remarks>
    public void SealStructural() => structuralSealed = true;

    // Same value assigned twice is not a mistake — the command line is applied once before the
    // graph is built and again with the panel's full binding, which is deliberate. Only a CHANGE
    // after sealing is the fault.
    private void Seal(string member, bool changed)
    {
        if (!structuralSealed || !changed) return;
        Console.Error.WriteLine(
            $"StudioLook.{member} is structural — read when the graph was built, so this change " +
            "does nothing. Set it before the renderer loads (the command line does).");
    }

    /// <summary>A declared value moved. Recompute what is derived from it.</summary>
    public void OnChanged(TunableChange change) => Recompute();

    /// <summary>Derive the sun's vectors from its angles. Also run once at construction.</summary>
    public void Recompute()
    {
        var elevation = SunElevation * (MathF.PI / 180f);
        var azimuth = SunAzimuth * (MathF.PI / 180f);
        var horizontal = MathF.Cos(elevation);

        SunDirection = Vector3.Normalize(new Vector3(
            horizontal * MathF.Sin(azimuth),
            MathF.Sin(elevation),
            horizontal * MathF.Cos(azimuth)));

        SunColour = SunTint * SunIntensity;
    }
}
