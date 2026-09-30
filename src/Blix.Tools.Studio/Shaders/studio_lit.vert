#version 450

// The complete static vertex (VertexPosition3NormalTangentTexture2Color), as the cook writes it.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
// Authored or MikkTSpace's, never absent; w is the bitangent's handedness, per glTF.
layout(location = 2) in vec4 aTangent;
layout(location = 3) in vec2 aTexCoord;
layout(location = 4) in vec2 aTexCoord1;
// Baked ambient occlusion on the nature kit, white everywhere else. UByte4Norm, so the
// hardware hands it over already expanded to 0..1.
layout(location = 5) in vec4 aColour;

// Only the member this stage reads. A LEADING PREFIX of the fragment stage's block is legal and
// costs nothing: std140 puts uViewProjection at offset 0 either way.
//
// It used to name five, and the last four were both unread AND at the wrong offsets -- the
// fragment stage has three cascade matrices where this had one uSunViewProjection, so this
// stage's uCameraPosition sat on the fragment stage's uCascadeVP1. Harmless only for as long as
// nobody read it, which is not a property worth relying on.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
};

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;       // x = metallic, y = roughness
    vec4 uExtra;          // x = albedo UV set, y = normal scale, z = other channels' UV sets (see studio_lit.frag)
    vec4 uEmission;       // rgb = emissive radiance, a = occlusion strength
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec4 vColour;
layout(location = 4) out vec2 vUv1;
layout(location = 5) out vec4 vTangent;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    vWorld = world.xyz;

    // Uniform scale only in this lab, so the upper 3x3 is enough and no inverse
    // transpose is needed. Stated rather than assumed: non-uniform scale would shear
    // these normals.
    vNormal = normalize(mat3(uModel) * aNormal);
    // A direction on the surface, so the same upper 3x3 carries it; handedness rides along.
    vTangent = vec4(normalize(mat3(uModel) * aTangent.xyz), aTangent.w);

    vUv = aTexCoord;
    vUv1 = aTexCoord1;
    vColour = aColour;
    gl_Position = uViewProjection * world;
}
