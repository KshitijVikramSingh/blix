using System.Numerics;
using Blix.Graphics;

namespace Blix.Labs.Toolchain;

/// <summary>One thing to draw: a pose, a colour, and how it responds to light.</summary>
/// <remarks>
/// A record rather than a class hierarchy, and a flat list rather than a graph. The lab is not the place
/// to grow a scene model — see conventions, "what Blix deliberately does not have". If Spear needs one it
/// should say so in its own words.
/// </remarks>
public readonly record struct LabObject(
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
public sealed class LabScene
{
    private readonly List<LabObject> objects = new();

    public IReadOnlyList<LabObject> Objects => objects;

    /// <summary>Direction TOWARD the sun. Normalised on assignment.</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));

    public Vector3 SunColour { get; set; } = new(3.2f, 3.05f, 2.75f);

    /// <summary>Flat stand-in for image-based lighting, which this lab does not carry.</summary>
    public float AmbientStrength { get; set; } = 0.06f;

    public void SetSunDirection(Vector3 direction)
    {
        var length = direction.Length();
        if (length > 1e-4f) SunDirection = direction / length;
    }

    public void Clear() => objects.Clear();

    public void Add(in LabObject item) => objects.Add(item);

    /// <summary>
    /// Just the ground, for when the subject is an imported model.
    /// </summary>
    /// <remarks>
    /// A ring of boxes is a good lighting subject and a terrible backdrop: the first capture of the tank
    /// had it half-hidden behind one, with its pivot triads lost among seven unrelated silhouettes. What a
    /// model viewer needs behind the model is a floor and nothing else.
    /// </remarks>
    public static LabScene GroundOnly()
    {
        var scene = new LabScene();
        scene.Add(new LabObject(
            Matrix4x4.Identity,
            new Vector3(0.22f, 0.23f, 0.26f),
            Metallic: 0f,
            Roughness: 0.9f,
            IsGround: true));
        return scene;
    }

    /// <summary>The default lab: a ground plane and a ring of boxes at varied roughness.</summary>
    public static LabScene Default()
    {
        var scene = new LabScene();
        scene.Add(new LabObject(
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

            scene.Add(new LabObject(
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
    public Matrix4x4 SunViewProjection(float extent = 9f, float depth = 30f)
    {
        var eye = SunDirection * (depth * 0.5f);
        var up = MathF.Abs(Vector3.Dot(SunDirection, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, up);
        var projection = GraphicsMatrices.CreateOrthographicVulkan(extent * 2f, extent * 2f, 0.1f, depth);
        return view * projection;
    }
}
