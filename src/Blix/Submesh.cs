using Blix.Render;

namespace Blix;

// One drawable piece of a multi-primitive game object: a mesh + the material it
// renders with. glTF primitives become Submeshes 1:1 (one primitive per material
// assignment, which is how content authors typically split a model).
//
// Static GameObject carries a single (Mesh, Material) — its conceptual one-submesh
// shape. SkinnedGameObject grows a Submeshes[] for the multi-primitive case (one
// character split into body/hair/eyes/clothing primitives, each with its own
// material).
public readonly record struct Submesh(Mesh Mesh, Material Material);
