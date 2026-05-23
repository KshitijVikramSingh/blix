using System.Numerics;
using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix;

// Per-frame inputs to PbrSceneRenderer. The renderer reads these to produce
// the shared uniform array every lit submesh draw receives. Demos hand-pack
// any extra tuning knobs into `ExtraUniforms`; PbrFrameContext deliberately
// stops at the physics inputs (light state, env probe, camera) so it stays
// useful across demos that don't share the Walkthrough's tuning surface.
public sealed record PbrFrameContext
{
    public required Matrix4x4 View { get; init; }
    public required Matrix4x4 Projection { get; init; }
    public required Vector3 CameraPosition { get; init; }
    public required Vector3 SunDirection { get; init; }
    public required Vector3 SunColor { get; init; }
    public required EnvironmentProbe Environment { get; init; }

    // Optional: when null, the lit shader gets a single fallback VP rather
    // than the cascade array. Demos that use CSM populate this.
    public CascadeShadowState? Cascades { get; init; }

    // Optional: when null, the lit shader's point-light count is 0.
    public PbrPointLightState? PointLights { get; init; }

    public float Exposure { get; init; } = 1.0f;

    // Demo-specific tuning uniforms (point-light specular scale, horizon
    // fade, point-shadow bias, emissive boost, etc.) packed into a list
    // the renderer appends after the physics inputs. Empty by default.
    public IReadOnlyList<ShaderUniform>? ExtraUniforms { get; init; }
}

public sealed record CascadeShadowState(
    // One light VP per cascade. Length must match the lit shader's cascade
    // count (typically 3 in the Walkthrough demo).
    Matrix4x4[] LightViewProjections,
    // View-space depth boundaries. Length = cascade count + 1 (inclusive
    // near, inclusive far). The lit shader uses this to pick a cascade
    // from the fragment's view depth.
    float[] Splits,
    // R/G/B-tint the lit fragments by which cascade they sampled.
    bool Visualize);

public sealed record PbrPointLightState(
    // World-space positions of all active point lights. Length = ActiveCount;
    // arrays may be over-sized for fixed-capacity shader arrays.
    Vector3[] Positions,
    Vector3[] Colors,
    float[] Ranges,
    int ActiveCount);
