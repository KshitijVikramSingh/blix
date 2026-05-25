using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Render;

// Pluggable per-submesh "should this draw?" hook for scene renderers
// (PbrSceneRenderer, future renderers). Defaults to null in renderer
// signatures so existing call sites are unaffected.
//
// Usage pattern (caller is the renderer's per-submesh loop):
//
//   if (occluder.ShouldDraw(path)) pass.DrawMesh(...);
//   occluder.RecordQuery(pass, path, bounds);
//
// RecordQuery is called for EVERY submesh that passes frustum cull
// regardless of the ShouldDraw decision — the proxy AABB query is the
// single source of truth for "is this visible?". Using one consistent
// test (always proxy AABB) avoids the visibility-strobe that
// alternating between "real-geometry-as-query" and "proxy-as-query"
// produces for loose-bounded geometry (sparse foliage where the AABB
// extends into the visible volume but the actual mesh fragments are
// fully behind an occluder). The cost is one extra proxy draw per
// visible submesh per frame; the benefit is stable cull state.
//
// EntityPath is a stable string identity — typically the scene's
// DebugName + submesh index — so the occluder can persist visibility
// state across frames and across producers without needing a typed
// handle.
public interface IOccluder
{
    bool ShouldDraw(string entityPath);

    void RecordQuery(RenderPassBuilder pass, string entityPath, Bounds3 worldBounds);
}
