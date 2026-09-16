using System.Numerics;
using Blix.Diagnostics;

namespace Blix.Tools.Studio;

/// <summary>
/// Every dial that decides what the stage looks like — Blix's house style, written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defaults on this type ARE the house style.</b> <c>new StudioLook()</c> is what Blix thinks
/// an ordinary model should look like, and <c>view</c> takes it without being asked. That is the only
/// place the engine expresses a visual opinion: nothing in <c>Blix.*</c> says how bright a sun is,
/// and nothing has to, because the answer lives here where a person can look at it, diff it across
/// versions, and take it or leave it.
/// </para>
/// <para>
/// <b>This type used to be argued against, in a comment, by me.</b> The argument was that gathering
/// these dissolves the moment they are sorted by what READS them — the sun and the ambient belong to
/// the lit pass, the shadow extent to the caster, the exposure and tonemap to the present pass, which
/// is three sets of pass parameters and not one concept. That is correct, and it answers a different
/// question. Sorted by who DECIDES them they are plainly one thing: one person answers all of these in
/// one sitting while looking at one picture, and changing any of them is the same kind of act. Both
/// groupings are real and they cut across each other, which is what <see cref="TuneAttribute.Group"/>
/// is for — the members below are grouped by pass for the panel while remaining one authored artifact.
/// <c>RTSGame</c>'s <c>LookSettings</c> reached the same shape independently, spanning the same passes,
/// and did not dissolve either.
/// </para>
/// <para>
/// <b>None of these can be settled by measurement, which is why none of them is a constant in a
/// shader.</b> A lighting constant looks exactly like a physical one — "sun intensity 2.05" reads as
/// though somebody derived it — so a number nobody can measure should be visibly a question. Settle
/// one and it belongs here with a note saying what it was judged against, not pushed back into GLSL.
/// </para>
/// <para>
/// <b>Two kinds of member, and the difference is load-bearing.</b> Most are read every frame: move the
/// slider, see it. A few are <see cref="TuneAttribute.Structural"/> — read once when the pipelines and
/// targets are built, and never again. Those are flags rather than sliders, and the panel shows them
/// among the values rather than offering to move them, because a control that changes nothing is worse
/// than one that does not exist.
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
    /// <b>It used to be described as a "flat stand-in for image-based lighting", and now it scales
    /// the real thing.</b> The meaning a caller sees is unchanged — how strong the fill is — so the
    /// knob did not have to move when IBL arrived underneath it. The DEFAULT did.
    /// <para>
    /// 0.06 was tuned when the fill was flat and its whole job was stopping a shadowed face going
    /// black; any more than that and a featureless wash sat on everything. A directional fill earns
    /// more, and at 0.06 it earned nothing visible — IBL on and off was a seam you had to look for.
    /// 0.30 is where the shaded side of a face reads without the key light losing its authority,
    /// judged on the Rogue at Walking_A 0.35s against 0.06 and 0.35.
    /// </para>
    /// <para>
    /// Zero is a legitimate inspection mode rather than a broken one: flattening the fill is how a
    /// silhouette becomes readable, and how you find out whether a shape is being carried by the key
    /// light or by the environment.
    /// </para>
    /// </remarks>
    [Tune(0, 0.5f, Group = "sun")] public float AmbientStrength { get; set; } = 0.30f;

    /// <summary>Metres around the origin the SHARPEST cascade covers — the subject's own box.</summary>
    /// <remarks>
    /// <b>This replaced ShadowExtent, which was the half-width of a single shadow box.</b> That box
    /// is gone: the stage fits three concentric cascades now, so "how big is the one box" had no
    /// meaning left and the number it carried was being multiplied by 0.3 at its only remaining use
    /// — a knob whose value meant something other than what it said.
    /// <para>
    /// This is the radius of the innermost cascade, which is the one the subject stands in and the
    /// one whose resolution decides whether a contact shadow reads. A model is normalised to about
    /// three units on this stage, so 2.7 m puts the whole of it inside the sharp box with room for
    /// what it is holding. Raising it trades the subject's shadow resolution for reach.
    /// </para>
    /// </remarks>
    [Tune(1, 12, Group = "shadow")] public float ShadowSubjectRadius { get; set; } = 2.7f;

    /// <summary>How far from the camera shadows are cast, in metres — the last cascade's far bound.</summary>
    /// <remarks>
    /// <b>A distance, where <see cref="ShadowSubjectRadius"/> is a radius around the origin.</b> The
    /// camera sits 11 m from the subject by default, so a 9 m range would cut the shadow off in
    /// FRONT of the thing being looked at. 30 m covers the stage's 12 m ground from the far side of
    /// the orbit.
    /// </remarks>
    [Tune(5, 120, Group = "shadow")] public float ShadowDistance { get; set; } = 30f;

    /// <summary>How the three cascades divide the range: 0 splits evenly, 1 logarithmically.</summary>
    /// <remarks>
    /// <b>0.85 rather than the usual 0.7, because this stage's subject is small and close.</b> The
    /// textbook value assumes a camera standing among what it looks at, with real distance to spend.
    /// Here the whole subject is a few metres across, so a near cascade sized by a uniform split is
    /// mostly air — pushing toward logarithmic spends the first cascade on the thing you are actually
    /// looking at, which is where a contact shadow lives.
    /// </remarks>
    [Tune(0, 1, Group = "shadow")] public float CascadeSplitLambda { get; set; } = 0.85f;

    /// <summary>Paint each cascade a flat colour instead of shading. Magenta = past the last one.</summary>
    /// <remarks>
    /// <b>The one instrument that says whether three passes are doing three cascades' work.</b> A
    /// scene whose near cascade never appears is paying three caster redraws for one cascade — a
    /// fault with no symptom in the picture, because a correct-looking shadow from the wrong cascade
    /// looks like a shadow.
    /// </remarks>
    [Tune(Group = "shadow")] public bool ShowCascades { get; set; }

    /// <summary>Whether the floor is drawn. Off is how you look at a thing against nothing.</summary>
    /// <remarks>
    /// It is a lit mesh rather than a gizmo, and it has to be: a shadow needs something to land on.
    /// The GRID over it is a gizmo and always was — <c>debug.Draw.Grid</c>, engine-native, drawn by
    /// the tool. The two were never one thing; they only ever looked like one.
    /// </remarks>
    [Tune(Group = "stage")] public bool Ground { get; set; } = true;

    // ── structural: read when the graph is built, never again ────────────────────────────────

    /// <summary>Whether the environment lights the scene, or a flat fill stands in for it.</summary>
    /// <remarks>
    /// <b>Structural because the probe is baked once.</b> Turning this off is how you find out
    /// whether a shape is being carried by the key light or by the environment, which is a real
    /// inspection question — so it stays a flag rather than being removed once IBL works.
    /// </remarks>
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
    /// <remarks>
    /// The shader reads <c>roughness * (this - 1)</c>, and that arithmetic is the reason this is a
    /// declared value rather than a constant in GLSL: a demo carried the ceiling as
    /// <c>const float MAX_REFLECTION_LOD = 6.0</c>, which has to equal a number produced by a C#
    /// bake. A constant that silently disagrees with another language is a picture that is wrong
    /// and compiles.
    /// </remarks>
    [Tune(2, 8, Group = "environment", Structural = true)]
    public int EnvMipCount
    {
        get => envMipCount;
        set { Seal(nameof(EnvMipCount), envMipCount != value); envMipCount = value; }
    }

    private int envMipCount = 5;

    /// <summary>Samples per pixel in the scene pass. 1 is off; 2, 4 and 8 are the usual answers.</summary>
    /// <remarks>
    /// <para>
    /// <b>Structural, and unusually so: the sample count is baked into the render pass, the images
    /// AND every pipeline.</b> There is no version of changing this at runtime that is not a rebuild,
    /// which is what the flag on this attribute exists to say.
    /// </para>
    /// <para>
    /// <b>On, because a turntable shows a silhouette turning against a background and a
    /// stair-stepped silhouette is the most visible artefact this stage has.</b> Unlike the depth
    /// pre-pass beside it — a performance trade that could not be measured here — this is an
    /// image-quality change anyone can see in one frame.
    /// <para>
    /// It needed an engine change to work at all: a multisampled attachment is not sampleable, and
    /// this stage's present pass SAMPLES the scene depth to carry it to the swapchain so debug
    /// gizmos depth-test against the scene. The render graph had ResolveColor and no ResolveDepth;
    /// it has both now, and a pass that asks for depth resolve is built with vkCreateRenderPass2.
    /// </para>
    /// </para>
    /// <para>
    /// Costs a multisampled colour and depth target and a resolve. The panel's viewport stays at 1x
    /// deliberately: it is a fraction of the window and already renders the scene a second time.
    /// </para>
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
    /// <para>
    /// <b>OFF, because the benefit could not be measured and the cost could.</b> The stage renders
    /// correctly either way — with and without, a capture is byte-identical — so what is left is the
    /// trade, and on this stage it does not pay: the pre-pass adds a whole geometry pass and two
    /// pipelines (8 to 10) to save fragment work on a scene of a handful of objects.
    /// </para>
    /// <para>
    /// <b>The instrument could not see it, and that is the finding rather than an excuse.</b> Frame
    /// time here is vsync-locked with no present-mode switch, so it quantises to the refresh:
    /// repeated A/B runs at eight bodies gave 7.93 ms on and 16.34 ms off, then 16.17 ms on and
    /// 8.02 ms off — the same pair, inverted, which is noise reading as a 2x result. The only number
    /// that stayed put was the CPU cost of building the extra pass, about +0.04 ms.
    /// </para>
    /// <para>
    /// So it stays, structural and one flag away, for the case that changes the answer: a stage
    /// showing terrain or a crowd IS fragment-bound, and that is the case IStudioView was shaped
    /// around. Turning it on then is a measurement, not a guess.
    /// </para>
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
    /// <b>64 because 256 costs 1.45 seconds of startup, measured, for a table of constants.</b> The
    /// LUT is the Karis split-sum integration — a function of (NdotV, roughness) and of nothing
    /// else, so it does not depend on the environment, the sun, or the model. Its cost is O(n²) on
    /// this machine: 32 → 25 ms, 64 → 84 ms, 128 → 407 ms, 256 → 1451 ms. At the engine's default of
    /// 256 it was <b>96% of the entire environment bake</b>, with the probe itself at 35-70 ms.
    /// <para>
    /// The function is smooth in both axes, which is what makes a small table viable, and the choice
    /// was CHECKED against 256 rather than assumed: on a Rogue capture, 64 differs from 256 on 0.11%
    /// of pixels with a maximum difference of <b>1/255</b> — rounding. Even 32 stays within 2/255.
    /// (128 differs on marginally more pixels than 64, also at 1/255; that is dither, not a quality
    /// trend, and saying so is cheaper than implying the curve is monotonic.) The real fix is not a smaller table but a COOKED one
    /// — it is the same numbers on every machine forever, and <c>Blix.Tools.Cook</c> already writes
    /// a BRDF LUT into a <c>.blixprobe</c>. That is engine work and is not this arc's.
    /// </para>
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
    /// <remarks>
    /// <b>Without this the two disagreed silently.</b> The vector fields carry the hardcoded direction
    /// this stage was authored with, and nothing recomputed them until something MOVED — so a run with
    /// no flags lit the scene from the old vector while the panel showed angles that did not produce
    /// it. The capture is what caught it: it came back matching the picture from before the angles
    /// existed, byte for byte, which is exactly what "the knob is ignored" looks like when the default
    /// happens to be close.
    /// </remarks>
    public StudioLook() => Recompute();

    private bool structuralSealed;

    /// <summary>
    /// The graph has been built: from here a structural change cannot take effect.
    /// </summary>
    /// <remarks>
    /// <b>Because the first structural setting shipped unable to be set.</b> Both tools called
    /// <c>renderer.Load</c> — which bakes the probe — BEFORE applying the command line, so
    /// <c>--image-based-lighting false</c> parsed correctly, assigned correctly, and changed
    /// nothing. Byte-identical captures with the flag on and off were the only symptom, and only
    /// because someone went looking.
    /// <para>
    /// The ordering is fixed in both tools. This exists so the next one is loud: cascades, MSAA and
    /// a depth pre-pass are all structural, and each is another chance to make the same mistake in a
    /// place where the picture looks plausible either way.
    /// </para>
    /// </remarks>
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
