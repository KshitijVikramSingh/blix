using System.Numerics;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Diagnostics;

// Polymorphic primitive for the debug-draw channel. Each command is a
// sealed derived record so consumers (the GL renderer, JSON dump sink,
// future picking-aware sinks) can pattern-match without a Kind enum or
// nullable grab-bag fields.
//
// Path is the hierarchical identifier (scope + name, exactly like
// Values/Stats/Timers paths). It serves double duty in Phase 8+:
//   - Layer toggles: DebugState.LayersEnabled["physics/aabb"] = false
//     hides every command whose path begins with that prefix.
//   - Entity identity (Phase 10): the path locates the producer-defined
//     thing the command attaches to, so selection / inspection wire up
//     against the same string the producer already names.
//
// Color is on the base record because every primitive renders with one
// uniform color; per-vertex coloring would be a separate primitive type.
// View is the view this primitive belongs to, resolved from the ambient
// scope at emit time and STORED — exactly as Path is. It is not fused with
// Path on purpose: Path answers "what kind of thing is this" and is the
// layer-toggle key, View answers "where is this seen from". Two notions
// wearing one name is the near-synonym fault that has cost this codebase
// days elsewhere (see JobSystem.IsAtItsPlace).
//
// Storing it per command rather than per channel is what makes a frame
// self-describing, which is what lets a dump be read without replaying the
// frame that produced it. The old per-channel field justified itself with
// "every primitive in a single frame shares one camera" — an assumption,
// not a measurement, and the one this whole arc retires. Cost was never the
// argument: DebugDrawFrustum has always carried a full 4x4 per command.
public abstract record DebugDrawCommand(string Path, GraphicsColor Color, ViewId View);

public sealed record DebugDrawLine(string Path, GraphicsColor Color, ViewId View, Vector3 A, Vector3 B)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawAabb(string Path, GraphicsColor Color, ViewId View, Vector3 Min, Vector3 Max)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawGrid(string Path, GraphicsColor Color, ViewId View, Vector3 Center, float Size, int Divisions)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawFrustum(string Path, GraphicsColor Color, ViewId View, Matrix4x4 ViewProjection)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawSphere(string Path, GraphicsColor Color, ViewId View, Vector3 Center, float Radius, int Segments)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawPlane(string Path, GraphicsColor Color, ViewId View, Vector3 Center, Vector3 Normal, float Size)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawRay(string Path, GraphicsColor Color, ViewId View, Vector3 Origin, Vector3 Direction, float Length)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawCapsule(string Path, GraphicsColor Color, ViewId View, Vector3 A, Vector3 B, float Radius, int Segments)
    : DebugDrawCommand(Path, Color, View);

// Obb is described by a single Transform that maps the unit cube
// [-1, 1]^3 into world space. Position lives in column 4; per-axis
// extents are baked into the basis vector lengths in columns 1..3.
// Renderers expand into the 12 transformed edges; no separate
// (center, rotation, extents) tuple needed.
public sealed record DebugDrawObb(string Path, GraphicsColor Color, ViewId View, Matrix4x4 Transform)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawCross(string Path, GraphicsColor Color, ViewId View, Vector3 Center, float Size)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawCone(string Path, GraphicsColor Color, ViewId View, Vector3 Apex, Vector3 Axis, float Length, float HalfAngleRad, int Segments)
    : DebugDrawCommand(Path, Color, View);

public sealed record DebugDrawArrow(string Path, GraphicsColor Color, ViewId View, Vector3 From, Vector3 To)
    : DebugDrawCommand(Path, Color, View);

// World-space wireframe of a mesh. Vertices are positions; Edges is a
// flat (i0, i1, i0, i1, ...) index array — one pair per line segment.
//
// The arrays are held by reference, not copied at emit time: the
// producer is responsible for treating them as immutable for as long as
// any frame snapshot might reference them (same contract as
// DebugEventEntry.Payload). For typical scene producers this is fine —
// per-submesh edge tables are precomputed once and reused every frame.
//
// JSON dump records vertex/edge COUNTS, not the arrays themselves;
// embedding a 100k-vertex mesh in a debug dump file would be useless.
public sealed record DebugDrawMeshWireframe(
    string Path,
    GraphicsColor Color,
    ViewId View,
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<int> Edges)
    : DebugDrawCommand(Path, Color, View);

// An open path through space: consecutive points joined by segments. The
// primitive a trail renders as, and the one the line family was missing —
// everything else here is a closed shape or a single segment.
//
// Points are COPIED by the producer, not held by reference like
// MeshWireframe: a trail's backing store is rewritten every frame, and a
// DebugFrame snapshot that pointed at it would silently change after the
// fact. That is the same rule the view declarations follow, and for the
// same reason — Freeze() holds frames past the builder's lifetime.
public sealed record DebugDrawPolyline(
    string Path,
    GraphicsColor Color,
    ViewId View,
    IReadOnlyList<Vector3> Points)
    : DebugDrawCommand(Path, Color, View);

// Per-vertex normals visualisation. Each (position, normal) pair becomes
// a single line from position to position + normal*Length. Length is the
// world-space draw length, not a per-normal magnitude (normals are
// expected to be unit-length); use a small value (~0.05–0.5) for most
// scenes or the lines drown the mesh.
public sealed record DebugDrawNormals(
    string Path,
    GraphicsColor Color,
    ViewId View,
    IReadOnlyList<Vector3> Positions,
    IReadOnlyList<Vector3> Normals,
    float Length)
    : DebugDrawCommand(Path, Color, View);
