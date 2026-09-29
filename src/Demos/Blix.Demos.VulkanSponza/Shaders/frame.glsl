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
    vec4  uSkyDims;        // xyz visibility probe counts
    vec4  uBounceDims;     // xyz bounce probe counts
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
    // Four-corner tetrahedral probe reconstruction instead of eight-corner trilinear. Halves this
    // lookup's fetches and pays a few compares; watch for LEAKING rather than blurring, since
    // fewer candidates means the visibility test empties the set more often.
    //@tune bool
    float uProbeTetrahedral;
    // Whether the probe blend trusts a marched line of sight through the occupancy grid over the
    // Chebyshev depth-moment test. Off is the shipped behaviour exactly; on rejects any probe the
    // march says is behind geometry. Read the leak census (--viz 21) and the fallback rate
    // together: rejecting everything reports no leak and no light.
    //
    // <b>Off by default, and that reverts a measured improvement on purpose.</b> With it on,
    // leak falls from 17.4% of blend weight to zero while surviving weight falls from 24.4% to
    // 21.0% -- so the number that justified defaulting it on is real and is kept here rather than
    // deleted with it. What the number does not capture is what the darkening costs by eye, which
    // is the judgement this default now follows.
    //
    // It was also a 0..1 slider, and the intermediate values were a fiction: nothing chose 0.4
    // between two rejection strategies. A switch is what it is.
    //@tune bool
    float uProbeOcclusion;
    // Transport diagnostics. Measurement helpers must forward argument arrays with "$@"; collapsed
    // multi-flag invocations invalidate these A/B controls.
    //@tune bool
    float uSkyDropL2;
    //@tune bool
    float uNoBounceTerm;
    // Which normal the inline ambient asks the world-space fields along: 1 (the default) is the
    // INTERPOLATED GEOMETRIC normal, 0 is the normal-mapped one.
    //
    // <b>Defaults to geometric because these are metre-scale fields.</b> Baked sky visibility and
    // the bounce probes resolve enclosure at roughly the size of a room; a normal map varies over
    // millimetres. Feeding the mapped normal into them adds high-frequency variation to a query
    // that has no high-frequency information to give back, so the relief shows up as noise on the
    // ambient term rather than as detail. Direct lighting and specular keep the mapped normal,
    // which is where surface relief belongs and is visible.
    //
    // Kept as a dial rather than welded shut: the half-res incident field's error is all normal
    // and does not shrink with resolution, so flipping this to 0 still splits that error into the
    // two halves with different fixes -- relief the field can never see, versus a depth
    // reconstruction a prepass normal target would repair.
    //@tune bool
    float uAmbientGeoNormal;
    // Measurement switches, one per --ab mode. Each removes one term from the fragment so a paired
    // interleaved run can price it:
    //   x  collapse every material UV to a constant, so the five material samples all hit one
    //      cached texel. The sample INSTRUCTIONS remain — this prices BANDWIDTH, not instruction
    //      count, which is the distinction the whole question turns on.
    //   y  skip the GGX/Smith/Fresnel specular lobe, leaving Lambert. Prices ALU.
    //   z  skip the three IBL lookups and the split-sum, using a flat ambient. Prices IBL whole.
    //   w  skip the normal map sample and the tangent-space transform.
    vec4  uAbFlags;
    //   x  skip the probe bounce lookup AND the baked sky-visibility evaluation. Prices the two
    //      terms that read the probe volumes, which is the pair a half-res pass would move.
    //   y  drop the occupancy line-of-sight test back to Chebyshev alone. Prices the march.
    vec4  uAbFlags2;
    // --viz N: write one of the shading inputs instead of the lit colour. The normal path is the
    // hardest thing here to be sure about by reading code — a double-sided sheet whose back face
    // lights from the wrong hemisphere looks exactly like a material problem — so it is worth being
    // able to LOOK at the inputs rather than reason about them.
    //   1 geometric normal (world, after the facing flip)   2 shading normal (after the map)
    //   3 tangent-space normal-map value                    4 front/back facing
    //   5 world tangent                                     6 world bitangent
    float uVizChannel;
    // xyz = world-space min of the sky volume, w = 1 when the volume is loaded.
    vec4  uSkyMin;
    // xyz = 1 / (max - min), w = how far along the normal to push the lookup, in metres.
    vec4  uSkyScale;
    float uBounceStrength;
    vec3  _occPad;
    // xyz = occupancy grid dims, w = 1 when the grid is bound. Read only by the leak metric
    // (viz channel 21), which marches it for ground-truth line of sight between a point and the
    // probes voting on it — the thing the Chebyshev test in probe_volume.glsl only approximates.
    vec4  uOccupancyDims;
    // xy = the incident field's size in pixels, z = 1 when the lit pass should read it instead of
    // reconstructing the probe volumes itself, w unused.
    vec4  uIncident;
} frame;
