#version 450

// The selection plate: a translucent mark on the ground under whatever is picked.
//
// Its own shader rather than the contact shadow's, because that one hard-codes black — it reads only the
// instance alpha as a strength and throws the colour away, which is correct for an occlusion smudge and
// useless for a highlight. Sharing it produced an invisible plate: a quartic-faded black disc at a third
// alpha over dark ground is nothing at all.
//
// Same disc mesh, same layout and the same decal discipline: depth-tested so it sits under the world,
// depth-write off and no culling so it survives a slope seen at a grazing angle.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

struct Instance {
    mat4 model;
    vec4 tint;
};

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uFade;
    vec4 uCamPos;
};

layout(location = 0) out float vRadius;
layout(location = 1) out vec4 vTint;

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * world;
    vRadius = inUv.x;
    // Deliberately not distance-faded the way a contact shadow is. A shadow at range is noise; a selection
    // marker at range is the one thing you are looking for, and it has to stay legible across the map.
    vTint = inst.tint;
}
