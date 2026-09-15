using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // Refit the cascade light view-projections to the current camera. Each
    // cascade bounds a slice [Splits[c], Splits[c+1]] of the camera frustum:
    // we take the slice's 8 corners, wrap them in a bounding sphere (so the
    // ortho is rotation-invariant), aim the light at the sphere centre, and
    // snap that centre to the shadow-map texel grid so shadows don't swim as
    // the camera moves. Ported from the GL SponzaModern reference, expressed
    // in Vulkan depth-[0,1] conventions (CreateOrthoVulkan + System.Numerics
    // right-handed CreateLookAt, same matrix-multiply order as the camera's
    // viewProj = view * proj).
    private void UpdateCascades()
    {
        var fwd = cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var camUp = Vector3.Cross(right, fwd);
        var tanV = MathF.Tan(fovYRadians * 0.5f);
        var tanH = tanV * aspect;

        // Light travel direction; place the eye opposite it, behind the slab.
        var L = Vector3.Normalize(sunDirection);
        var sunUp = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        Span<Vector3> corners = stackalloc Vector3[8];
        for (var c = 0; c < CascadeCount; c++)
        {
            var nearD = cascadeSplits[c];
            var farD = cascadeSplits[c + 1];
            var k = 0;
            for (var di = 0; di < 2; di++)
            {
                var d = di == 0 ? nearD : farD;
                var centre = cameraPosition + fwd * d;
                var hh = d * tanV;
                var hw = d * tanH;
                corners[k++] = centre - right * hw - camUp * hh;
                corners[k++] = centre + right * hw - camUp * hh;
                corners[k++] = centre - right * hw + camUp * hh;
                corners[k++] = centre + right * hw + camUp * hh;
            }

            // Bounding sphere of the slice corners.
            var center = Vector3.Zero;
            for (var i = 0; i < 8; i++) center += corners[i];
            center /= 8f;
            var radius = 0f;
            for (var i = 0; i < 8; i++) radius = MathF.Max(radius, Vector3.Distance(corners[i], center));
            radius = MathF.Ceiling(radius);

            // Texel-snap the sphere centre in light space so the ortho footprint
            // lands on a stable grid (kills the shimmer under camera motion).
            var eye = center - L * (shadows.SunDistance + radius);
            var lightView = Matrix4x4.CreateLookAt(eye, center, sunUp);
            var texelSize = (2f * radius) / ShadowMapSizes[c];
            var centreLight = Vector3.Transform(center, lightView);
            centreLight.X = MathF.Round(centreLight.X / texelSize) * texelSize;
            centreLight.Y = MathF.Round(centreLight.Y / texelSize) * texelSize;
            Matrix4x4.Invert(lightView, out var invLightView);
            var snapped = Vector3.Transform(centreLight, invLightView);

            var eye2 = snapped - L * (shadows.SunDistance + radius);
            var lightView2 = Matrix4x4.CreateLookAt(eye2, snapped, sunUp);
            var farPlane = 2f * (shadows.SunDistance + radius);
            var ortho = GraphicsMatrices.CreateOrthographicVulkan(2f * radius, 2f * radius, 0.1f, farPlane);
            cascadeViewProj[c] = lightView2 * ortho;

            // Base depth bias = BiasTexels shadow-texels of world offset,
            // converted to this cascade's NDC depth units (ortho z is linear,
            // so world→NDC depth scale is 1/farPlane). Keeps the bias visually
            // constant across cascades despite their very different extents.
            cascadeDepthBias[c] = (shadows.BiasTexels * texelSize) / farPlane;
        }
    }
}
