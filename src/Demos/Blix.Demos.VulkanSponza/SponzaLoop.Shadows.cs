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
            // <b>Padded, so a cascade may be reused for frames after the one it was fitted to.</b>
            // The far cascades cover 30 m and 60 m, where a shadow changes slowly and re-rendering
            // every frame redraws thousands of casters to move almost nothing. Reuse needs slack:
            // the map has to still cover the view a few frames later, and the only way to buy that
            // is to fit a larger box than the slice needs. It is paid for in texel size, which is
            // why cascade 0 gets none — it is the one whose sharpness is looked at.
            radius = MathF.Ceiling(radius * (1f + CascadePad[c]));

            // Texel-snap the sphere centre in light space so the ortho footprint
            // lands on a stable grid (kills the shimmer under camera motion).
            var eye = center - L * (shadows.SunDistance + radius);
            var lightView = Matrix4x4.CreateLookAt(eye, center, sunUp);
            var texelSize = (2f * radius) / ShadowMapSizes[c];

            // <b>Kept, because the shadow lookup needs it and used to guess it.</b> This number was
            // computed for texel-snapping and thrown away, while the shader offset its samples by
            // uCascadeBias * (1 + slope * uSlopeScale) — four hand-tuned constants standing in for
            // the one derived quantity that was already sitting here.
            cascadeTexelWorld[c] = texelSize;
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

            // <b>Due, or moved far enough that the padding no longer covers.</b> Interval alone
            // would tear at the edges the moment the camera outran the slack; distance alone would
            // never refresh a cascade under a rotating-but-stationary camera, whose slice sweeps
            // through the scene while its centre barely moves. Both, and either one triggers.
            var moved = Vector3.Distance(snapped, cascadeFitCentre[c]);
            var slack = radius * CascadePad[c];
            var due = (framesRendered - cascadeFittedFrame[c]) >= CascadeInterval[c];
            if (cascadeFittedFrame[c] == 0 || due || moved > slack)
            {
                cascadeFitCentre[c] = snapped;
                cascadeFittedFrame[c] = framesRendered;
                cascadeRenderViewProj[c] = cascadeViewProj[c];
                cascadeTexelRendered[c] = texelSize;
                cascadeDue[c] = true;
            }
            else
            {
                cascadeDue[c] = false;
            }
            // <b>The lit pass samples the map that EXISTS, not the fit this frame computed.</b> A
            // reused cascade was rendered with an older matrix, and sampling it with a newer one
            // reads the right texture through the wrong transform — every shadow in that cascade
            // displaced by however far the camera moved since.
            cascadeViewProj[c] = cascadeRenderViewProj[c];
            cascadeTexelWorld[c] = cascadeTexelRendered[c];
        }
    }
}
