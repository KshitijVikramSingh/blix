using System.Numerics;
using Blix.Tools.Studio;
using ImGuiNET;

namespace Blix.Tools.Studio.Shell;

/// <summary>
/// The stage, in a panel: a second camera's picture drawn inside an ImGui window you can orbit,
/// zoom and click into.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reports; it never calls back.</b> The version this was lifted from reached into its host
/// for the pick — <c>app.PickThrough(view, pointer)</c> — and that single line is the difference
/// between a widget and a host. A thing you call inside your own <c>ImGui.Begin</c>, which hands
/// back what the pointer did, is a library. A thing that calls your code is a shell that has begun
/// deciding what your application is.
/// </para>
/// <para>
/// So picking stays with the tool: this says <see cref="Clicked"/> and the caller decides what a
/// click means. Only the viewer picks anyway — the capture has no pointer and the probe has no
/// device — which is why selection itself never left the executable.
/// </para>
/// <para>
/// <b>An Image is not an interactive item.</b> <c>ImGui.Image</c> calls ItemAdd with a bounding box,
/// which is enough for IsItemHovered, but it runs no ButtonBehavior and mints no id — so nothing can
/// ever hold it active and dragging on the picture was silently inert. An InvisibleButton over the
/// same rectangle is the idiom: it gives the picture an id and real press state, and the image
/// underneath still draws because the button is placed back at the image's own origin.
/// </para>
/// </remarks>
public sealed class ViewportPanel
{
    private bool open = true;

    public ViewportPanel(string title = "viewport") => Title = title;

    /// <summary>The window's name, and its ImGui id. One per panel if a tool wants several.</summary>
    public string Title { get; }

    /// <summary>
    /// Whether the window is up.
    /// </summary>
    /// <remarks>
    /// <b>ImGui does not close a window for you.</b> Begin(name, ref open) draws the X and sets the
    /// flag; NOT calling Begin next frame is what actually closes it. Calling it regardless left the
    /// window on screen with its render switched off, so the X looked like it froze the picture —
    /// which is a considerably worse thing for a button to appear to do.
    /// </remarks>
    public ref bool Open => ref open;

    /// <summary>Size of the panel's content region, for the caller to size its target by next frame.</summary>
    public Vector2 PanelSize { get; private set; }

    /// <summary>Top-left of the drawn picture in screen space — the rect a ray must be cast through.</summary>
    public Vector2 ImageMin { get; private set; }

    /// <summary>Size of the drawn picture, which is not the panel's: it is letterboxed to the target.</summary>
    public Vector2 ImageSize { get; private set; }

    /// <summary>Was the pointer over the picture this frame.</summary>
    public bool Hovered { get; private set; }

    /// <summary>Where the pointer was when it was clicked on the picture, or null.</summary>
    /// <remarks>
    /// The whole of what replaces a callback. A caller reads this after <see cref="Draw"/> and casts
    /// its own ray, through its own view declaration, into its own selection — none of which this
    /// knows about or should.
    /// </remarks>
    public Vector2? Clicked { get; private set; }

    /// <summary>
    /// Draw the panel. Call it from inside your own layout, not the other way around.
    /// </summary>
    /// <param name="textureId">The stage's viewport colour, registered through <c>IRenderHost</c>.</param>
    /// <param name="camera">The panel's own camera. Orbited and zoomed in place.</param>
    /// <param name="targetSize">
    /// The render target's dimensions, whose aspect the picture is letterboxed to. Stretching to
    /// fill would make the picture disagree with the projection it was drawn through, and every ray
    /// cast into it afterwards would be wrong by that same stretch — silently, and only on panels
    /// whose shape happened not to match.
    /// </param>
    /// <param name="footer">An optional line under the picture. The tool's words, not the shell's.</param>
    public void Draw(nint textureId, StudioCamera camera, (int Width, int Height) targetSize, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Clicked = null;
        Hovered = false;

        if (textureId == 0 || !open) return;

        ImGui.SetNextWindowSize(new Vector2(520, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(480, 470), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(Title, ref open))
        {
            ImGui.End();
            return;
        }

        var yaw = camera.Yaw;
        var pitch = camera.Pitch;
        var distance = camera.Distance;
        if (ImGui.SliderFloat("yaw", ref yaw, -MathF.PI, MathF.PI)) camera.Yaw = yaw;
        if (ImGui.SliderFloat("pitch", ref pitch, camera.MinPitch, camera.MaxPitch)) camera.Pitch = pitch;
        if (ImGui.SliderFloat("dist", ref distance, camera.MinDistance, camera.MaxDistance))
        {
            camera.Distance = distance;
        }

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 32f || available.Y < 32f)
        {
            ImGui.End();
            return;
        }

        // Read for NEXT frame's projection. Reading it here rather than guessing is the whole
        // reason the lag is one frame and not permanent.
        PanelSize = available;

        var targetAspect = targetSize.Height > 0
            ? targetSize.Width / (float)targetSize.Height
            : 16f / 9f;
        var fitted = available.X / available.Y > targetAspect
            ? new Vector2(available.Y * targetAspect, available.Y)
            : new Vector2(available.X, available.X / targetAspect);

        var cursor = ImGui.GetCursorScreenPos();
        ImageMin = cursor + ((available - fitted) * 0.5f);
        ImageSize = fitted;

        ImGui.SetCursorScreenPos(ImageMin);
        ImGui.Image(textureId, fitted);

        ImGui.SetCursorScreenPos(ImageMin);
        ImGui.InvisibleButton("##viewport-surface", fitted, ImGuiButtonFlags.MouseButtonLeft);

        // <b>Not through IInputHandler, and that is forced rather than chosen.</b> This is an ImGui
        // window, so ImGui captures the pointer over it, GestureOwnership hands the press to the UI,
        // and the application's input handler is never called — correctly. The picture is an ImGui
        // ITEM, so the only place that can ask "is the pointer on it" is here, during layout, using
        // the item state ImGui just computed. The engine needed no change to allow this: the capture
        // rule was already right, and a widget asking about itself is what the rule leaves room for.
        Hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var drag = ImGui.GetIO().MouseDelta;
            camera.Orbit(drag.X, drag.Y);
        }

        if (Hovered)
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f) camera.Zoom(wheel);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Clicked = ImGui.GetMousePos();
        }

        var pointer = ImGui.GetMousePos();
        ImGui.TextDisabled(Hovered
            ? $"pointer {pointer.X - ImageMin.X:0}, {pointer.Y - ImageMin.Y:0} in view"
            : "drag to orbit · wheel to zoom · click to pick");

        if (footer is not null) ImGui.TextDisabled(footer);
        ImGui.End();
    }
}
