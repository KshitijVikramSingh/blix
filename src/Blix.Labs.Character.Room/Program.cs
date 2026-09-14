using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Labs.Character;
using Blix.Runtime.Silk;
using ImGuiNET;

namespace Blix.Labs.Character.RoomApp;

// The character lab's ROOM: contact, with nothing else in the picture.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • One source. What is drawn and what will be swept are the same triangles —
//     Room.Vertices and Room.Collider come out of the same pass, and the flat
//     shading means every facet you can see is a facet the sweep will hit.
//   • A claim you can check from the chair: the slope tint shades every surface by
//     the normal the collider reads, so a mis-wound face reads as a floor standing
//     up rather than as a surface that merely lights oddly.
//   • The lab family generalising: a second library with its own shaders, its own
//     renderer and three roots planned over it, none of which is the toolchain lab.
//
// ── Intentionally owns ──────────────────────────────────────────────────────
//   • Camera feel, what the panel shows, which part is selected.
//   • Nothing about movement. There is no body here yet, deliberately: a room that
//     had a character in it could not tell you whether a fault was the room's.
public static class Program
{
    public static void Main(string[] args)
    {
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — character lab: room",
            Width = 1280,
            Height = 760,
        });

        var loop = new RoomLoop();
        using var window = new Window(loop, options);
        window.Run();
    }
}

internal sealed class RoomLoop : IGameLoop, IDebuggable, IUiSource, IInputHandler, IDisposable
{
    private readonly RoomRenderer renderer = new();
    private readonly RoomCamera camera = new();
    private readonly Room room = Room.Build();

    private IRenderHost? host;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private float aspect = 16f / 9f;
    private int frames;

    private bool orbiting;
    private bool panning;

    private int selected = -1;
    private bool showNormals;
    private bool showBounds = true;
    private bool showGrid = true;
    private bool depthTestGizmos = true;
    private float slopeTint;

    // Cached per selection rather than per frame: the normals of a part do not change, and
    // rebuilding two lists every frame to draw the same arrows is work a lab can see in its own
    // frame time and then misattribute to the renderer.
    private readonly List<Vector3> normalPoints = new();
    private readonly List<Vector3> normalDirections = new();

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(vk, Path.Combine(AppContext.BaseDirectory, "Shaders"), room);

        Console.WriteLine($"room — {room.TriangleCount} triangles, {room.Parts.Count} parts, {room.SolidStarts.Count} solids");
        foreach (var part in room.Parts) Console.WriteLine($"  {part.Name,-12} {part.TriangleCount,5} tri  {ClaimOf(part)}");
    }

    public void OnUpdate(Time time)
    {
        renderer.SlopeTint = slopeTint;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        viewProjection = camera.ViewProjection(aspect);
        renderer.Render(commandList, room, viewProjection, camera.Position);
    }

    public void Debug(DebugContext debug)
    {
        debug.State.DepthTestDrawing = depthTestGizmos;
        debug.Values.Value("frames", frames);
        debug.Values.Value("camera", camera.Target);
        debug.Stats.Gauge("triangles", room.TriangleCount);
        debug.Stats.Gauge("solids", room.SolidStarts.Count);

        var (logicalW, logicalH) = host?.LogicalSize ?? (debug.Frame.Width, debug.Frame.Height);
        var declaration = debug.Draw.Declare(
            "main",
            viewProjection,
            RenderSurfaceHandle.Default,
            new Rect(0f, 0f, logicalW, logicalH),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));

        using (debug.Draw.In(declaration))
        {
            // A metre grid, so every claim in the panel has something to be read against: a 0.2 m
            // riser and a 0.3 m one are hard to tell apart by eye and trivial against a ruler.
            if (showGrid)
            {
                debug.Draw.Grid("floor", new Vector3(0f, 0.01f, 0f), 28f, 28, new GraphicsColor(0.35f, 0.38f, 0.42f, 1f));
            }

            if (selected >= 0 && selected < room.Parts.Count)
            {
                var part = room.Parts[selected];
                var (min, max) = BoundsOf(part);
                if (showBounds) debug.Draw.Aabb("selected", min, max, new GraphicsColor(1f, 0.82f, 0.25f, 1f));

                // THE WINDING, DRAWN. The probe checks arithmetically that every face's winding
                // agrees with the normal it declares; this is the same fact at a glance. An arrow
                // pointing into a solid is a contact normal that will push a body the wrong way.
                if (showNormals)
                {
                    debug.Draw.Normals(
                        "face-normals", normalPoints, normalDirections, 0.45f,
                        new GraphicsColor(0.35f, 0.9f, 1f, 1f));
                }
            }

            // Where a body will start when stage R-C has one. Drawn now because the spawn point is
            // already a claim the probe checks, and a claim nothing can see is one that rots.
            var spawn = Room.SpawnPoint;
            debug.Draw.Cross("spawn", spawn + new Vector3(0f, 0.05f, 0f), 0.5f, new GraphicsColor(0.4f, 1f, 0.5f, 1f));

            debug.Draw.Arrow(
                "sun", new Vector3(0f, 6f, 0f), new Vector3(0f, 6f, 0f) + renderer.SunDirection * 4f,
                new GraphicsColor(1f, 0.92f, 0.6f, 1f));
        }
    }

    public string DebugName => "room";

    public string UiName => "room";

    public void DrawUi()
    {
        ImGui.Begin("room");

        ImGui.Text($"{room.TriangleCount} triangles · {room.Parts.Count} parts · {room.SolidStarts.Count} solids");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("view", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // The one control worth having before anything moves: it shades every surface by the
            // normal the collider reads, which is how "is this really a 30 degree ramp" becomes a
            // question you can answer by looking.
            ImGui.SliderFloat("slope tint", ref slopeTint, 0f, 1f);
            ImGui.Checkbox("grid", ref showGrid);
            ImGui.SameLine();
            ImGui.Checkbox("bounds", ref showBounds);
            ImGui.SameLine();
            ImGui.Checkbox("normals", ref showNormals);
            ImGui.Checkbox("depth-test gizmos", ref depthTestGizmos);

            var exposure = renderer.Exposure;
            if (ImGui.SliderFloat("exposure", ref exposure, 0.2f, 3f)) renderer.Exposure = exposure;
        }

        if (ImGui.CollapsingHeader("parts", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var i = 0; i < room.Parts.Count; i++)
            {
                var part = room.Parts[i];
                if (ImGui.Selectable($"{part.Name}##part{i}", selected == i))
                {
                    selected = selected == i ? -1 : i;
                    RebuildNormals();
                    if (selected >= 0)
                    {
                        var (min, max) = BoundsOf(room.Parts[selected]);
                        camera.Frame((min + max) * 0.5f, (max - min).Length() * 0.5f);
                    }
                }

                if (!ImGui.IsItemHovered()) continue;
                ImGui.SetTooltip($"{part.TriangleCount} triangles\n{ClaimOf(part)}");
            }
        }

        if (selected >= 0 && selected < room.Parts.Count)
        {
            var part = room.Parts[selected];
            ImGui.Separator();
            ImGui.Text(part.Name);
            ImGui.TextWrapped(ClaimOf(part));

            // MEASURED, not restated. The panel prints what the geometry actually is rather than
            // echoing the claim back — if the two ever differ, this is where it shows without
            // anyone running the probe.
            var (slopeMin, slopeMax) = WalkableSlopeRange(part);
            ImGui.Text(float.IsFinite(slopeMin)
                ? $"measured walkable slope: {slopeMin:0.##}° to {slopeMax:0.##}°"
                : "no walkable faces");
        }

        ImGui.End();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Left) orbiting = true;
        if (button == MouseButton.Right || button == MouseButton.Middle) panning = true;
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Left) orbiting = false;
        if (button == MouseButton.Right || button == MouseButton.Middle) panning = false;
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (orbiting) camera.Orbit(deltaX, deltaY);
        else if (panning) camera.Pan(deltaX, deltaY);
    }

    public void OnMouseWheel(float offsetX, float offsetY) => camera.Zoom(offsetY);

    public void OnKeyDown(Key key)
    {
        if (key == Key.N) showNormals = !showNormals;
        if (key == Key.G) showGrid = !showGrid;
        if (key == Key.T) slopeTint = slopeTint > 0.5f ? 0f : 1f;
    }

    public void Dispose() => renderer.Dispose();

    private void RebuildNormals()
    {
        normalPoints.Clear();
        normalDirections.Clear();
        if (selected < 0) return;

        var part = room.Parts[selected];
        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var tri = room.TriangleAt(i);
            normalPoints.Add((tri.V0 + tri.V1 + tri.V2) / 3f);

            // The WOUND normal rather than the stored one, because the wound normal is what a
            // contact will be resolved along. Drawing the stored one would be drawing the claim.
            normalDirections.Add(room.WoundNormal(i));
        }
    }

    private (Vector3 Min, Vector3 Max) BoundsOf(RoomPart part)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = part.FirstTriangle * 3; i < (part.FirstTriangle + part.TriangleCount) * 3; i++)
        {
            min = Vector3.Min(min, room.Positions[i]);
            max = Vector3.Max(max, room.Positions[i]);
        }
        return (min, max);
    }

    private (float Min, float Max) WalkableSlopeRange(RoomPart part)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var n = room.WoundNormal(i);
            if (n.Y <= 1e-3f) continue;
            var slope = Room.SlopeDegrees(n);
            min = MathF.Min(min, slope);
            max = MathF.Max(max, slope);
        }
        return (min, max);
    }

    private static string ClaimOf(RoomPart part)
    {
        var claims = new List<string>();
        if (part.WalkSlopeDegrees is { } slope) claims.Add($"every walkable face is {slope:0.##}°");
        if (part.RiserHeight is { } riser) claims.Add($"6 treads, {riser:0.00} m apart");
        if (part.ClearWidth is { } clear) claims.Add($"{clear:0.00} m clear between two solids");
        if (part.TopHeight is { } top) claims.Add($"{(part.Name == "beam" ? "underside" : "top")} at {top:0.00} m");
        if (part.CurvatureRadius is { } radius) claims.Add($"sphere of radius {radius:0.#} — slope is asin(r/R)");
        return claims.Count == 0 ? "no claim" : string.Join("; ", claims);
    }
}
