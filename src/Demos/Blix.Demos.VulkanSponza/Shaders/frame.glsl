#pragma once

// Sponza's per-frame uniform block, declared ONCE.
//
// Every shader that binds set 0 / binding 0 must agree on this layout exactly, and three of them
// used to declare it separately. skybox.vert declared a sparse copy naming three members at
// hand-written absolute byte offsets, so removing a vec4 from this block silently pointed it at
// the member after the one it meant -- which happened, and was caught only because the device
// cross-checks shared blocks at pipeline creation and refused the mismatch by name.
//
// Including one declaration makes the offsets the compiler's problem. A shader that needs three
// members now declares all of them, which costs nothing: the block is one buffer either way.
//
// <b>The //@tune decorators live here, so whatever scans for them must scan the PREPROCESSED
// source.</b> The shader tool does, at build time: it expands every include into lit.frag and
// writes the decorators to lit.frag.spv.tune.json, which SponzaLoop.Setup loads. Nothing reads
// this file at runtime.

layout(set = 0, binding = 0) uniform Frame {
    mat4  uViewProjection;
    vec3  uSunDirection;
    float uSunPad;
    // Irradiance measured from the environment probe. The extracted disc is removed from the IBL,
    // so sun and sky arrive once in the same units; exposure owns overall image brightness.
    vec3  uSunIrradiance;
    float uIblPad;
    vec3  uCameraPos;
    float uEnvMipCount;
    float uSheenMipCount;
    // Sample count, so the coverage dither knows how big one quantum is.
    float uMsaaSamples;
    float uShadowStrength;         // 0 = sun shadows off, 1 = on
    // The scene's render size in pixels: a fragment's screen uv is gl_FragCoord.xy over it.
    vec2  uScreenSize;
    // Makes cutout fragments fully covered so diagnostic and lit paths address the same foliage
    // pixels. This separates coverage compositing from shading-term faults.
    //@tune bool
    float uForceOpaqueCutout;
    // Transport diagnostic: drop the incident field's light (all indirect diffuse) from the lit sum.
    // Measurement helpers must forward argument arrays with "$@"; collapsed multi-flag invocations
    // invalidate these A/B controls.
    //@tune bool
    float uNoBounceTerm;
    // Measurement switches, one per --ab mode. Each removes one term from the fragment so a paired
    // interleaved run can price it:
    //   x  collapse every material UV to a constant, so the five material samples all hit one
    //      cached texel. The sample INSTRUCTIONS remain — this prices BANDWIDTH, not instruction
    //      count, which is the distinction the whole question turns on.
    //   y  skip the GGX/Smith/Fresnel specular lobe, leaving Lambert. Prices ALU.
    //   z  skip the specular IBL lookups and the split-sum. Prices IBL whole.
    //   w  skip the normal map sample and the tangent-space transform.
    vec4  uAbFlags;
    // --viz N: write one of the shading inputs instead of the lit colour. The normal path is the
    // hardest thing here to be sure about by reading code — a double-sided sheet whose back face
    // lights from the wrong hemisphere looks exactly like a material problem — so it is worth being
    // able to LOOK at the inputs rather than reason about them. SponzaLoop's VizChannelNames names them all.
    //   1 geometric normal (world, after the facing flip)   2 shading normal (after the map)
    //   3 tangent-space normal-map value                    4 front/back facing
    //   5 world tangent                                     6 world bitangent
    float uVizChannel;
    // The incident field (RTGI's light: all of the indirect diffuse, sky included; zero until its readout is ready).
    // x 1 when the field already holds its own occlusion -- RTGI's traced readout -- so the material's occlusion texture
    // does not darken it again (2: --material-ao-on-gi, it does). y 1 when the traced sun shadows ran at half
    // resolution (RenderSettings.HalfResShadows): the traced sun's visibility is then the incident gradient's w,
    // upsampled (lighting_upsample.frag), not the mask at this pixel.
    vec2  uIncident;
    // How the lit pass carries the incident field's light to its normal-mapped normal (the readout's
    // gradient): x the mip bias the normal map is read at for it (indirect diffuse answers to folds and relief, not
    // to a weave finer than a pixel, which under TAA's jitter shimmered), y how far the correction may scale the
    // light either way (factor in [1 - y, 1 + y]).
    vec2  uIncidentGradient;
    // Stage 4e's velocity: this frame's UN-jittered view-projection and last frame's. The pre-pass projects a
    // vertex through both (current and previous world) and writes the difference, so motion never carries the
    // jitter.
    mat4  uViewProjUnjittered;
    mat4  uPrevViewProjUnjittered;
    // 1 (--ibl-brdf-lut): the specular IBL's split sum from the LUT, not the analytic fit (lit_body.glsl).
    float uIblBrdfLut;
} frame;
