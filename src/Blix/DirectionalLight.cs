using System.Numerics;

namespace Blix;

// A single directional light source: parallel rays from a fixed world-space direction,
// like the sun. Direction is the vector pointing FROM a surface TO the light (Lambert
// convention), so a light directly above the scene has Direction = +Y. Intensity is a
// scalar multiplier on the direct-light term; the lit shader scales diffuse + specular
// contributions by it.
//
// Environment params (ambient lift, skybox intensity, fog, etc.) are intentionally not
// on this type — they describe the scene, not the light. They live separately and gain
// a typed home (Environment? Scene?) when a second consumer needs them.
public sealed class DirectionalLight
{
    public Vector3 Direction { get; set; } = -Vector3.UnitY;

    public float Intensity { get; set; } = 1.0f;
}
