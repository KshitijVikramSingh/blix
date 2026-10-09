#pragma once

// Sponza's per-frame uniform block, declared ONCE.
//
// Every shader that binds set 0 / binding 0 must agree on this layout exactly, and three of them
// used to declare it separately. skybox.vert declared a sparse copy naming three members at
// hand-written absolute byte offsets, so removing a vec4 from this block silently pointed it at
// the member after uFog -- which happened, and was caught only because the device cross-checks
// shared blocks at pipeline creation and refused the mismatch by name.
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
    vec3  _cascadePad;
    mat4  uCascadeViewProj[3];     // light view-proj per cascade
    // .xyz = one shadow texel in world units per cascade, derived from the fitted ortho footprint.
    vec4  uCascadeTexels;
    vec4  uFog;                    // x=screenW, y=screenH, z=fogFar, w=enabled(0/1)
    // Named live-tunable members carry their own range/default metadata for reflected diagnostics.
    //@tune bool
    float uVisualizeCascades;
    // 1 = visibility as greyscale, 2 = bent normal as RGB. Keep the term directly inspectable so
    // its structure can be judged independently of the final composition.
    // Note it still goes through exposure + tonemap in the present pass, so read it for STRUCTURE
    // (where the corners darken, where the normals bend) rather than as calibrated values.
    //@tune enum{ Off, Visibility, Bent normal }
    float uVisualizeAmbient;
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
    //   z  skip the three IBL lookups and the split-sum, using a flat ambient. Prices IBL whole.
    //   w  skip the normal map sample and the tangent-space transform.
    vec4  uAbFlags;
    // --viz N: write one of the shading inputs instead of the lit colour. The normal path is the
    // hardest thing here to be sure about by reading code — a double-sided sheet whose back face
    // lights from the wrong hemisphere looks exactly like a material problem — so it is worth being
    // able to LOOK at the inputs rather than reason about them.
    //   1 geometric normal (world, after the facing flip)   2 shading normal (after the map)
    //   3 tangent-space normal-map value                    4 front/back facing
    //   5 world tangent                                     6 world bitangent
    float uVizChannel;
    // z = 1 when the lit pass reads the incident field (the probe clipmap's light: all of its indirect
    // diffuse, sky included). Off -- --no-incident, or before the clipmap's first solve -- the lit
    // pass falls back to the open-sky irradiance cube with sky visibility 1.
    // x = 1 when it takes the field's sky visibility and light at all (--no-sky / --sky-no-sample
    // zero it: sky visibility 1, no incident light). y = 1 when the field already holds its own occlusion -- the
    // reference view's path trace, or texels with --texel-gtao 0 -- so GTAO does not darken it again. w unused.
    vec4  uIncident;
    // How the lit pass carries the incident field's light to its normal-mapped normal (incident_clipmap.frag's
    // gradient): x the mip bias the normal map is read at for it (indirect diffuse answers to folds and relief, not
    // to a weave finer than a pixel, which under TAA's jitter shimmered), y how far the correction may scale the
    // light either way (factor in [1 - y, 1 + y]).
    vec4  uIncidentGradient;
    // Stage 4e's velocity: this frame's UN-jittered view-projection and last frame's. The pre-pass projects a
    // vertex through both (current and previous world) and writes the difference, so motion never carries the
    // jitter.
    mat4  uViewProjUnjittered;
    mat4  uPrevViewProjUnjittered;
    // The probe clipmap, for the glass reflection's sky visibility (lit.frag): xyz probes per level, w base spacing;
    // each level's lowest cell (xyz), and in uClipOrigin0.w the blend band (probes). Appended last: skybox.vert reads
    // fields above by byte offset.
    vec4  uClipDims;
    vec4  uClipOrigin0;
    vec4  uClipOrigin1;
    vec4  uClipOrigin2;
    vec4  uClipOrigin3;
} frame;
