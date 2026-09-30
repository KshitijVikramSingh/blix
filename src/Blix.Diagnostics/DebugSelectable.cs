using Blix.Geometry;

namespace Blix.Diagnostics;

// A pickable entity collected from a producer. Value-typed so the picker
// can build large transient arrays without per-entity heap pressure.
//
// EntityPath is the stable identity that goes into DebugSystem.SelectedPath
// when this is the closest ray-hit. It serves double duty in Phase 10:
//   - selection key (mutation target)
//   - layer prefix (auto-emitted selection highlight inherits it)
//   - inspection key (IDebugInspectable matches against it)
//
// Bounds is world-space AABB. Refined picking (OBB / mesh) is a future
// extension — Phase 10 ray-tests AABBs only.
// Label is what the Selections tab lists it as: a path identifies, a name is what a person recognises
// ("scene/opaque/12" is the tree nobody could find in a pick list). Defaults to the path.
public readonly record struct DebugSelectable(string EntityPath, Blix.Geometry.Bounds3 Bounds, string? Label = null);
