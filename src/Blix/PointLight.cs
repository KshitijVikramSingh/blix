using System.Numerics;

namespace Blix;

// A point light source: emits radially from Position, falls off with distance.
// Range is the smooth-cutoff distance beyond which the light contributes
// effectively nothing -- the shader uses a `saturate(1 - (d/range)^4)^2 / d^2`
// attenuation profile, "physically inspired" without being a true singular
// point source (which would need a non-zero minimum-distance clamp).
//
// Color is linear RGB; Intensity scales the radiance. A typical room lamp is
// around (1.0, 0.85, 0.7) at intensity 5-15; the demo's defaults are tuned for
// the LDR pipeline.
public sealed class PointLight
{
    public Vector3 Position { get; set; } = Vector3.Zero;

    public Vector3 Color { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 1.0f;

    public float Range { get; set; } = 10.0f;

    // Declarative shadow-casting flag. The renderer reserves a limited
    // number of point shadow cubemaps; shadow-casting point lights are placed at
    // the front of the array so their indices map to shadow-cube slots.
    public bool CastsShadow { get; set; } = false;
}
