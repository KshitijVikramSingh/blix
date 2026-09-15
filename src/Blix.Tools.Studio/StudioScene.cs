using System.Numerics;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>One thing to draw: a pose, a colour, and how it responds to light.</summary>
/// <remarks>
/// A record rather than a class hierarchy, and a flat list rather than a graph. The lab is not the place
/// to grow a scene model — see conventions, "what Blix deliberately does not have". If Spear needs one it
/// should say so in its own words.
/// </remarks>
public readonly record struct StudioObject(
    Matrix4x4 Model,
    Vector3 BaseColour,
    float Metallic,
    float Roughness,
    bool IsGround = false);

/// <summary>
/// What the lab draws, and the light it draws under.
/// </summary>
/// <remarks>
/// Deliberately small and owned by the lab, not the engine: a ground plane, a ring of boxes, one sun. The
/// interesting surface here is the toolchain around it — reflected binding, shared shaders, several
/// executables over one lab — not the scene graph, which is why there isn't one.
/// </remarks>
public sealed class StudioScene : ITunable
{
    private readonly List<StudioObject> objects = new();

    /// <summary>Derives the look once, so the first frame reads the same values a change would set.</summary>
    public StudioScene() => Recompute();

    public IReadOnlyList<StudioObject> Objects => objects;

    // ── the look, declared ───────────────────────────────────────────────────────────────────
    //
    // <b>The sun used to be a Vector3 with a setter, and that is the reason none of this existed.</b>
    // A direction is not something a slider or a command line can express — there is no sensible
    // control for "three floats that must stay normalised" — so the stage's most basic property was
    // unreachable from a panel, from a flag, and from a capture. Two ANGLES are the same fact in a
    // form both faces can render, and the vector becomes derived.
    //
    // Every knob here is [Tune], so every tool on this stage gets a Scene panel it did not write and
    // --sun-elevation it did not parse. That is the whole return on having built the declaration
    // layer first.

    /// <summary>Degrees around Y, from +Z toward +X.</summary>
    [Tune(0, 360)] public float SunAzimuth { get; set; } = 52.125f;

    /// <summary>Degrees above the horizon. Not 90: straight down has no stable up vector.</summary>
    [Tune(0, 89)] public float SunElevation { get; set; } = 54.526f;

    /// <summary>Scales the sun's tint. One is the light this stage was authored under.</summary>
    [Tune(0, 3)] public float SunIntensity { get; set; } = 1f;

    /// <summary>Flat stand-in for image-based lighting, which this stage does not carry.</summary>
    [Tune(0, 0.5)] public float AmbientStrength { get; set; } = 0.06f;

    /// <summary>Half-width of the sun's orthographic box, in metres.</summary>
    /// <remarks>
    /// A knob because it is a trade every subject settles differently: too wide and a small rig gets
    /// a few texels of shadow map, too narrow and a large one is cut off at the edge of the light.
    /// </remarks>
    [Tune(2, 40)] public float ShadowExtent { get; set; } = 9f;

    /// <summary>Whether the floor is drawn. Off is how you look at a thing against nothing.</summary>
    [Tune] public bool Ground { get; set; } = true;

    /// <summary>Direction TOWARD the sun. Derived from the two angles.</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));

    /// <summary>Derived: the tint this stage was authored with, scaled by <see cref="SunIntensity"/>.</summary>
    public Vector3 SunColour { get; private set; } = new(3.2f, 3.05f, 2.75f);

    private static readonly Vector3 SunTint = new(3.2f, 3.05f, 2.75f);

    /// <summary>
    /// A declared value moved — from a panel, a flag, or a replayed frame. Recompute the derived look.
    /// </summary>
    /// <remarks>
    /// <b>The first time the substrate uses its own capability rather than only providing it.</b> It
    /// reads the same as RigSession's: declared scalars in, derived state out, and no caller
    /// anywhere has to remember to call anything after writing one.
    /// </remarks>
    public void OnChanged(TunableChange change) => Recompute();

    /// <summary>Derive the vectors from the angles. Called on any change, and once at construction.</summary>
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

    public void Clear() => objects.Clear();

    public void Add(in StudioObject item) => objects.Add(item);

    /// <summary>
    /// Just the ground, for when the subject is an imported model.
    /// </summary>
    /// <remarks>
    /// A ring of boxes is a good lighting subject and a terrible backdrop: the first capture of the tank
    /// had it half-hidden behind one, with its pivot triads lost among seven unrelated silhouettes. What a
    /// model viewer needs behind the model is a floor and nothing else.
    /// </remarks>
    public static StudioScene GroundOnly()
    {
        var scene = new StudioScene();
        scene.Add(new StudioObject(
            Matrix4x4.Identity,
            new Vector3(0.22f, 0.23f, 0.26f),
            Metallic: 0f,
            Roughness: 0.9f,
            IsGround: true));
        return scene;
    }

    /// <summary>The default lab: a ground plane and a ring of boxes at varied roughness.</summary>
    public static StudioScene Default()
    {
        var scene = new StudioScene();
        scene.Add(new StudioObject(
            Matrix4x4.Identity,
            new Vector3(0.22f, 0.23f, 0.26f),
            Metallic: 0f,
            Roughness: 0.9f,
            IsGround: true));

        const int count = 7;
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)count;
            var angle = t * MathF.Tau;
            var radius = 3.2f;
            var height = 0.5f + (i % 3) * 0.45f;

            var model = Matrix4x4.CreateScale(0.9f, height * 2f, 0.9f)
                        * Matrix4x4.CreateRotationY(angle * 0.6f)
                        * Matrix4x4.CreateTranslation(
                            MathF.Cos(angle) * radius, height, MathF.Sin(angle) * radius);

            scene.Add(new StudioObject(
                model,
                Vector3.Lerp(new Vector3(0.85f, 0.35f, 0.25f), new Vector3(0.25f, 0.55f, 0.85f), t),
                Metallic: i % 3 == 0 ? 1f : 0f,
                Roughness: 0.12f + t * 0.7f));
        }

        return scene;
    }

    /// <summary>
    /// A sun view-projection that covers the scene, for the caster pass.
    /// </summary>
    /// <remarks>
    /// An orthographic box aimed down the sun direction at the origin. No cascades and no texel snapping —
    /// both belong to a renderer that has earned them (TankArena and Sponza have), and a lab that grew
    /// them by default would be quietly claiming to be one.
    /// </remarks>
    public Matrix4x4 SunViewProjection(float? extent = null, float depth = 30f)
    {
        var box = extent ?? ShadowExtent;
        var eye = SunDirection * (depth * 0.5f);
        var up = MathF.Abs(Vector3.Dot(SunDirection, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, up);
        var projection = GraphicsMatrices.CreateOrthographicVulkan(box * 2f, box * 2f, 0.1f, depth);
        return view * projection;
    }
}
