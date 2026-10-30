// Where the sun's cascades may answer a shadow query: inside the camera's view, and nowhere else.
//
// The cascades are drawn with casters culled against the RECEIVERS the camera sees (SponzaLoop.Render.cs: a caster
// whose swept shadow reaches no visible surface is left out). So a cascade covers a point outside the view with a
// footprint and a depth range, but not with the casters that would shadow it, and it answers lit. The probes' rays
// trace to anywhere -- the room behind the camera -- and took the sun there: in the Cornell room, probes solved looking
// at a side wall read the back wall's indirect 46% high, and correct looking at it.
//
// receivers: what the host passes (SponzaLoop CascadeReceivers): every point to w 0, so the cascades answer nowhere and
// every hit takes its sun by a ray.
bool cascadeReceives(mat4 receivers, vec3 world) {
    vec4 c = receivers * vec4(world, 1.0);
    return c.w > 0.0 && all(lessThanEqual(abs(c.xy), vec2(c.w))) && c.z >= 0.0 && c.z <= c.w;
}
