// Where the sun's cascades may answer a shadow query: inside the camera's view, and nowhere else.
//
// The cascades are drawn with casters culled against the RECEIVERS the camera sees (SponzaLoop.Render.cs: a caster
// whose swept shadow reaches no visible surface is left out). So a cascade covers a point outside the view with a
// footprint and a depth range, but not with the casters that would shadow it, and it answers lit. The gather passes
// trace to anywhere -- a probe's, a texel's, a screen probe's ray hits the room behind the camera -- and took the sun
// there: in the Cornell room, probes solved looking at a side wall read the back wall's indirect 46% high, and correct
// looking at it (the surface transfer's training captures, plan.md stage 5).
//
// receivers: the camera's view-projection, unjittered, as the cull took it; or the host's always-inside matrix (every
// point to (0, 0, 0.5, 1)) when nothing is culled or the check is off (it is on with --offscreen-shadow-rays).
bool cascadeReceives(mat4 receivers, vec3 world) {
    vec4 c = receivers * vec4(world, 1.0);
    return c.w > 0.0 && all(lessThanEqual(abs(c.xy), vec2(c.w))) && c.z >= 0.0 && c.z <= c.w;
}
