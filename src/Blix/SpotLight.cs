using System.Numerics;

namespace Blix;

// A cone-shaped light source: same point-light distance falloff, plus a cone
// angular falloff so light is bright in the centre and smoothly fades to zero
// at the cone's outer edge.
//
// Direction is the direction the cone points (FROM the light TO whatever it's
// illuminating). A spot aimed at the floor from directly above has
// Direction = -UnitY.
//
// InnerConeAngle is the half-angle of the inner cone (full brightness inside);
// OuterConeAngle is the half-angle where contribution falls to zero. Both are
// stored in radians. A typical "tight spot" is inner=10 deg, outer=15 deg; a
// "wide stage spot" is inner=20 deg, outer=35 deg.
public sealed class SpotLight
{
    public Vector3 Position { get; set; } = Vector3.Zero;

    public Vector3 Direction { get; set; } = -Vector3.UnitY;

    public Vector3 Color { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 1.0f;

    public float Range { get; set; } = 10.0f;

    public float InnerConeAngle { get; set; } = MathF.PI / 12.0f;   // 15 degrees

    public float OuterConeAngle { get; set; } = MathF.PI / 6.0f;    // 30 degrees

    // Declarative flag for shadow casting. The renderer reserves a
    // limited number of shadow-map slots (currently 2 for spots); shadow-casting
    // lights are placed at the front of the spot-light array so their indices
    // map 1:1 to those slots. Lights with CastsShadow = false consume no shadow
    // resources -- they're cheaper but their light doesn't cast.
    public bool CastsShadow { get; set; } = false;
}
