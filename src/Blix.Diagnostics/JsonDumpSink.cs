using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Blix.Diagnostics;

// IDebugFrameSink that serialises a DebugFrame to disk as JSON. Two trigger
// paths share one writer:
//
//   RequestDump()      — arms the sink; next Consume() writes that frame.
//                        Used when the runtime wants the next live frame.
//   Dump(DebugFrame)   — writes the given frame immediately, used when the
//                        runtime already has a specific frame in hand
//                        (typically the FrozenFrame).
//
// We dump to a stable DTO rather than the live entry records so future
// internal-type changes don't silently break the on-disk format. The DTO
// is what a JSON consumer should pin against; the live records remain
// free to evolve.
//
// Object-typed values (DebugValueEntry.Value, DebugEventEntry.Payload) are
// serialised via Object handling so System.Text.Json walks their runtime
// type. Producers should pass records / value types / primitives — any
// non-serialisable graph falls back to the default { } emit.
public sealed class JsonDumpSink : IDebugFrameSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IncludeFields = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Enums-as-strings makes dumps grep-friendly ("Fallback" instead
        // of "2") and keeps the on-disk format stable across reorders
        // of enum cases. Applies to any enum-typed property in payload
        // graphs as well as the DTO's own Kind / Severity fields, since
        // payload capture (Capture) flows through the same options.
        Converters = { new JsonStringEnumConverter() }
    };

    // System.Text.Json serialises `object`-typed properties using the
    // declared (object) type and emits `{}`. We pre-serialise dynamically-
    // typed payloads to JsonNode at DTO-build time so the runtime type is
    // captured at the only point we still know it. JsonNode is then
    // serialised natively, no custom converter required.
    private static JsonNode? CaptureRuntime(object? value)
    {
        if (value is null)
        {
            return null;
        }
        return JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);
    }

    public string OutputDirectory { get; }

    private bool armed;

    public JsonDumpSink()
        : this(Path.Combine(AppContext.BaseDirectory, "dumps"))
    {
    }

    public JsonDumpSink(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = outputDirectory;
    }

    // Arms the sink so the next Consume() writes its frame. Multiple
    // RequestDump() calls before a single Consume() collapse — the sink
    // either fires next frame or it doesn't.
    public void RequestDump()
    {
        armed = true;
    }

    public bool IsArmed => armed;

    // Writes the given frame to OutputDirectory/frame-NNNN.json. Returns
    // the absolute path written. Throws nothing on I/O failure — the path
    // is returned regardless so the caller can log it, and any exception
    // is caught here and reported to stderr (a JSON dump should never
    // tear down the host process).
    public string Dump(DebugFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var path = Path.Combine(OutputDirectory, $"frame-{frame.Number:D6}.json");
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            var dto = JsonDebugFrame.From(frame);
            var json = JsonSerializer.Serialize(dto, SerializerOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[diagnostics] JsonDumpSink failed writing {path}: {ex.Message}");
        }
        return path;
    }

    public void Consume(DebugFrame frame)
    {
        if (!armed)
        {
            return;
        }
        armed = false;
        Dump(frame);
    }
}

// === On-disk schema =========================================================
//
// Anything below is the JSON contract. Changing field names / shapes here is
// a breaking change for external consumers (regression tooling, bug-repro
// readers). Add fields rather than rename or restructure.
//
// SCHEMA 2 BREAKS THAT RULE, once, deliberately. `DrawViewProjection` — one
// matrix for the whole frame — is gone, replaced by a `Views` array, and each
// draw command names the view it belongs to. There is no add-a-field version
// of that change: the old shape encodes "a frame is one world seen one way",
// which is the assumption being retired.
//
// Schema 1 had no version field at all, despite the paragraph above claiming
// a stable contract. That is why 2 is the first number that appears: an
// unversioned dump is schema 1 by elimination.
internal sealed record JsonDebugFrame(
    int SchemaVersion,
    int Number,
    double WallClockMs,
    JsonRenderFrameContext Frame,
    JsonValueEntry[] Values,
    JsonControlEntry[] Controls,
    JsonDrawCommand[] DrawCommands,
    JsonView[] Views,
    JsonStatEntry[] Stats,
    JsonTimerEntry[] Timers,
    JsonEventEntry[] Events,
    string? SelectedPath)
{
    /// <summary>The current on-disk schema. Bump whenever a field changes shape or leaves.</summary>
    public const int CurrentSchemaVersion = 2;

    public static JsonDebugFrame From(DebugFrame f)
    {
        // Commands carry a ViewId, which is a process-local index and means nothing in a file. The name is
        // the identity that survives the trip to disk, so it is resolved here, once, rather than leaving
        // every reader to join against the Views array.
        var names = new Dictionary<Blix.Core.ViewId, string>();
        foreach (var v in f.Views) names[v.Id] = v.Name;

        return new(
        SchemaVersion: CurrentSchemaVersion,
        Number: f.Number,
        WallClockMs: f.WallClockMs,
        Frame: new JsonRenderFrameContext(f.Frame.Width, f.Frame.Height),
        Values: f.Values.Select(JsonValueEntry.From).ToArray(),
        Controls: f.Controls.Select(JsonControlEntry.From).ToArray(),
        DrawCommands: f.DrawCommands
            .Select(c => JsonDrawCommand.From(c, names.GetValueOrDefault(c.View, "<undeclared>")))
            .ToArray(),
        Views: f.Views.Select(JsonView.From).ToArray(),
        Stats: f.Stats.Select(JsonStatEntry.From).ToArray(),
        Timers: f.Timers.Select(JsonTimerEntry.From).ToArray(),
        Events: f.Events.Select(JsonEventEntry.From).ToArray(),
        SelectedPath: f.SelectedPath);
    }
}

// A view as it appears on disk: where the world was seen from, and where that picture landed.
// Target is the raw surface id — opaque, but enough to tell two viewports apart in a dump.
internal sealed record JsonView(
    string Name,
    JsonMatrix4 ViewProjection,
    int Target,
    JsonRect LogicalViewport,
    JsonRect PhysicalViewport)
{
    public static JsonView From(Blix.Core.ViewDeclaration v) => new(
        v.Name,
        JsonMatrix4.From(v.ViewProjection),
        v.Target.Id,
        JsonRect.From(v.LogicalViewport),
        JsonRect.From(v.PhysicalViewport));
}

internal sealed record JsonRect(float X, float Y, float Width, float Height)
{
    public static JsonRect From(Blix.Graphics.Rect r) => new(r.X, r.Y, r.Width, r.Height);
}

internal sealed record JsonRenderFrameContext(int Width, int Height);

internal sealed record JsonValueEntry(string Path, string Scope, string Name, JsonNode? Value)
{
    public static JsonValueEntry From(DebugValueEntry e) =>
        new(e.Path, e.Scope, e.Name, JsonDumpSink_CaptureBridge.Capture(e.Value));
}

internal sealed record JsonControlEntry(
    string Path, string Scope, string Name,
    string Kind, JsonNode? Value, float Min, float Max, string[]? Options)
{
    public static JsonControlEntry From(DebugControlEntry e) => new(
        e.Path, e.Scope, e.Name,
        e.Kind.ToString(), JsonDumpSink_CaptureBridge.Capture(e.Value), e.Min, e.Max,
        e.Options?.ToArray());
}

// Flat DTO with a Kind discriminator + nullable per-primitive fields.
// Stable on-disk schema even as the polymorphic record family grows: an
// external consumer can read Kind and pull only the fields it knows
// about. New primitives extend by adding new optional fields, not by
// restructuring.
internal sealed record JsonDrawCommand(
    string Kind, string Path, string View, JsonColor Color,
    JsonVec3? A = null,
    JsonVec3? B = null,
    JsonVec3? Min = null,
    JsonVec3? Max = null,
    JsonVec3? Center = null,
    JsonVec3? Normal = null,
    JsonVec3? Origin = null,
    JsonVec3? Direction = null,
    JsonVec3? Apex = null,
    JsonVec3? Axis = null,
    // Arrow endpoints — named with the "Point" suffix to dodge a
    // C# name collision with the static JsonDrawCommand.From factory
    // below; the suffix has no semantic meaning beyond that.
    JsonVec3? FromPoint = null,
    JsonVec3? ToPoint = null,
    float? Size = null,
    float? Radius = null,
    float? Length = null,
    float? HalfAngleRad = null,
    int? Divisions = null,
    int? Segments = null,
    // Mesh primitives dump only summary stats — embedding a 100k-vertex
    // array per frame would make dumps unusable. The producer's
    // identification path (DebugDrawCommand.Path) is enough for a
    // consumer to correlate back to a specific submesh.
    // A polyline's points are the thing being reported — a motion question is unanswerable from a count —
    // so unlike the mesh primitives these are written out. Bounded by DebugTrails.MaxPointsPerTrail.
    JsonVec3[]? Points = null,
    int? PointCount = null,
    int? VertexCount = null,
    int? EdgeCount = null,
    int? NormalCount = null,
    JsonMatrix4? Matrix = null)
{
    public static JsonDrawCommand From(DebugDrawCommand c, string view)
    {
        var kind = c.GetType().Name.StartsWith("DebugDraw", StringComparison.Ordinal)
            ? c.GetType().Name.Substring("DebugDraw".Length)
            : c.GetType().Name;
        var color = JsonColor.From(c.Color);
        return c switch
        {
            DebugDrawLine x => new(kind, x.Path, view, color,
                A: JsonVec3.From(x.A), B: JsonVec3.From(x.B)),
            DebugDrawAabb x => new(kind, x.Path, view, color,
                Min: JsonVec3.From(x.Min), Max: JsonVec3.From(x.Max)),
            DebugDrawGrid x => new(kind, x.Path, view, color,
                Center: JsonVec3.From(x.Center), Size: x.Size, Divisions: x.Divisions),
            DebugDrawFrustum x => new(kind, x.Path, view, color,
                Matrix: JsonMatrix4.From(x.ViewProjection)),
            DebugDrawSphere x => new(kind, x.Path, view, color,
                Center: JsonVec3.From(x.Center), Radius: x.Radius, Segments: x.Segments),
            DebugDrawPlane x => new(kind, x.Path, view, color,
                Center: JsonVec3.From(x.Center), Normal: JsonVec3.From(x.Normal), Size: x.Size),
            DebugDrawRay x => new(kind, x.Path, view, color,
                Origin: JsonVec3.From(x.Origin), Direction: JsonVec3.From(x.Direction), Length: x.Length),
            DebugDrawCapsule x => new(kind, x.Path, view, color,
                A: JsonVec3.From(x.A), B: JsonVec3.From(x.B), Radius: x.Radius, Segments: x.Segments),
            DebugDrawObb x => new(kind, x.Path, view, color,
                Matrix: JsonMatrix4.From(x.Transform)),
            DebugDrawCross x => new(kind, x.Path, view, color,
                Center: JsonVec3.From(x.Center), Size: x.Size),
            DebugDrawCone x => new(kind, x.Path, view, color,
                Apex: JsonVec3.From(x.Apex), Axis: JsonVec3.From(x.Axis),
                Length: x.Length, HalfAngleRad: x.HalfAngleRad, Segments: x.Segments),
            DebugDrawArrow x => new(kind, x.Path, view, color,
                FromPoint: JsonVec3.From(x.From), ToPoint: JsonVec3.From(x.To)),
            DebugDrawPolyline x => new(kind, x.Path, view, color,
                Points: x.Points.Select(JsonVec3.From).ToArray(), PointCount: x.Points.Count),
            DebugDrawMeshWireframe x => new(kind, x.Path, view, color,
                VertexCount: x.Vertices.Count, EdgeCount: x.Edges.Count / 2),
            DebugDrawNormals x => new(kind, x.Path, view, color,
                NormalCount: Math.Min(x.Positions.Count, x.Normals.Count),
                Length: x.Length),
            _ => new(kind, c.Path, view, color)
        };
    }
}

internal sealed record JsonStatEntry(string Path, string Scope, string Name, string Kind, double Value)
{
    public static JsonStatEntry From(DebugStatEntry e) =>
        new(e.Path, e.Scope, e.Name, e.Kind.ToString(), e.Value);
}

internal sealed record JsonTimerEntry(string Path, string Scope, string Name, double TotalMs, int CallCount)
{
    public static JsonTimerEntry From(DebugTimerEntry e) =>
        new(e.Path, e.Scope, e.Name, e.TotalMs, e.CallCount);
}

internal sealed record JsonEventEntry(
    string Path, string Scope, string Severity, string Message, JsonNode? Payload, double TimestampMs)
{
    public static JsonEventEntry From(DebugEventEntry e) => new(
        e.Path, e.Scope, e.Severity.ToString(), e.Message,
        JsonDumpSink_CaptureBridge.Capture(e.Payload), e.TimestampMs);
}

// Internal bridge so DTO `From` methods can reach the JsonNode capture
// helper without that helper having to live as a public member.
internal static class JsonDumpSink_CaptureBridge
{
    // Mirror the outer SerializerOptions so payload captures share the
    // same enum-as-string handling and other formatting choices. We
    // deliberately don't reach back into JsonDumpSink.SerializerOptions
    // (it would couple this static to that one's initialization order);
    // duplication here is tiny and stable.
    // <b>IncludeFields, and the one word it used to be.</b> With fields excluded, every
    // System.Numerics value published through Values.Value(...) serialised as `{}` — Vector2,
    // Vector3, Vector4, Quaternion and Matrix4x4 all expose their components as FIELDS, not
    // properties. So a dump carried the NAME of every position and direction an application
    // reported and none of the numbers, which is the one thing a dump exists for.
    //
    // It had been that way since the dump existed and was invisible because the draw commands —
    // which are what a dump is usually read for — go through explicit JsonVec3 records and were
    // always fine. It surfaced the first time an application published its whole state as values
    // and someone read the file.
    //
    // Additive rather than a schema break: payloads that were empty gain contents, and payloads of
    // types with no public fields are unchanged. No version bump, by the rule at the top of this
    // file — add fields rather than rename or restructure.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static JsonNode? Capture(object? value)
    {
        if (value is null)
        {
            return null;
        }
        return JsonSerializer.SerializeToNode(value, value.GetType(), Options);
    }
}

internal sealed record JsonVec3(float X, float Y, float Z)
{
    public static JsonVec3 From(Vector3 v) => new(v.X, v.Y, v.Z);
}

internal sealed record JsonColor(float R, float G, float B, float A)
{
    public static JsonColor From(Blix.Graphics.GraphicsColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);
}

internal sealed record JsonMatrix4(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34,
    float M41, float M42, float M43, float M44)
{
    public static JsonMatrix4 From(Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}
