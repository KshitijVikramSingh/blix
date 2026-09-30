/* Blix's flat entry point into MikkTSpace: arrays in, one tangent per triangle corner out.
 *
 * The reference interface is a table of callbacks over the caller's mesh; this shim is that table,
 * over indexed arrays, so the managed side makes one call rather than marshalling six delegates.
 *
 * Texture coordinates are handed over as (u, 1 - v). glTF's v runs down the image and its normal
 * maps' green runs up it, so the bitangent glTF wants (cross(N, T) * w) points toward DECREASING v.
 * MikkTSpace's sign makes the bitangent follow its t axis, so t = 1 - v gives glTF's convention.
 */
#include <stdint.h>
#include "mikktspace.h"

#if defined(_WIN32)
#define BLIX_MIKK_API __declspec(dllexport)
#else
#define BLIX_MIKK_API __attribute__((visibility("default")))
#endif

typedef struct
{
    const float* positions;   /* 3 per vertex */
    const float* normals;     /* 3 per vertex */
    const float* uvs;         /* 2 per vertex */
    const uint32_t* indices;  /* 3 per triangle */
    int triangles;
    float* out;               /* 4 per corner: xyz direction, w handedness */
} BlixMikkMesh;

static const BlixMikkMesh* Mesh(const SMikkTSpaceContext* c) { return (const BlixMikkMesh*)c->m_pUserData; }
static uint32_t Vertex(const SMikkTSpaceContext* c, int face, int corner) { return Mesh(c)->indices[face * 3 + corner]; }

static int NumFaces(const SMikkTSpaceContext* c) { return Mesh(c)->triangles; }
static int NumCorners(const SMikkTSpaceContext* c, int face) { (void)c; (void)face; return 3; }

static void Position(const SMikkTSpaceContext* c, float out[], int face, int corner)
{
    const float* p = Mesh(c)->positions + Vertex(c, face, corner) * 3;
    out[0] = p[0]; out[1] = p[1]; out[2] = p[2];
}

static void Normal(const SMikkTSpaceContext* c, float out[], int face, int corner)
{
    const float* n = Mesh(c)->normals + Vertex(c, face, corner) * 3;
    out[0] = n[0]; out[1] = n[1]; out[2] = n[2];
}

static void TexCoord(const SMikkTSpaceContext* c, float out[], int face, int corner)
{
    const float* t = Mesh(c)->uvs + Vertex(c, face, corner) * 2;
    out[0] = t[0]; out[1] = 1.0f - t[1];
}

static void SetTangent(const SMikkTSpaceContext* c, const float tangent[], float sign, int face, int corner)
{
    float* o = ((BlixMikkMesh*)c->m_pUserData)->out + (face * 3 + corner) * 4;
    o[0] = tangent[0]; o[1] = tangent[1]; o[2] = tangent[2]; o[3] = sign;
}

/* Returns 1 on success, 0 when MikkTSpace refused (it allocates, and can fail to). */
BLIX_MIKK_API int blix_mikk_generate(
    const float* positions, const float* normals, const float* uvs,
    const uint32_t* indices, int triangles, float* outCornerTangents)
{
    SMikkTSpaceInterface callbacks = { 0 };
    callbacks.m_getNumFaces = NumFaces;
    callbacks.m_getNumVerticesOfFace = NumCorners;
    callbacks.m_getPosition = Position;
    callbacks.m_getNormal = Normal;
    callbacks.m_getTexCoord = TexCoord;
    callbacks.m_setTSpaceBasic = SetTangent;

    BlixMikkMesh mesh = { positions, normals, uvs, indices, triangles, outCornerTangents };
    SMikkTSpaceContext context = { &callbacks, &mesh };
    return genTangSpaceDefault(&context) ? 1 : 0;
}
