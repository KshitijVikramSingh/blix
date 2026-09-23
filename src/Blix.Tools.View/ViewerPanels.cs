using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Tools.Studio;
using Blix.Tools.Studio.Shell;
using ImGuiNET;

namespace Blix.Tools.View;

/// <summary>
/// Every panel the viewer draws, and the display-only state they toggle.
/// </summary>
/// <remarks>
/// <b>Local decomposition, and the reason is not line count alone.</b> The viewer had grown to 1,645
/// lines with the camera, the animation session, picking and the whole UI in one type — but the thing
/// that made it worth splitting was that the UI state and the SCENE state had become impossible to
/// tell apart. A checkbox that hides a skeleton and a clock that advances one are different kinds of
/// fact, and a reader had to know the codebase to say which a given field was.
/// <para>
/// What lives here is what a panel owns: which toggles are on, which clip row is highlighted, how big
/// a thumbnail is. What lives on the root is what the viewer owns: the rig, the session, the cameras, the
/// selection. The gizmo drawing reads these toggles back through the same reference, which is normal
/// for a view — it is the model that must not know about the view, not the other way round.
/// </para>
/// <para>
/// <b>Constructed by the root and called by it.</b> No discovery, no registration, no "active tool"
/// branch — a different executable simply builds a different root. Holding the app by reference is
/// what keeps each panel method's dependency list from being a dozen parameters long, and it is the
/// ordinary shape for a view over a model.
/// </para>
/// </remarks>
internal sealed class ViewerPanels
{
    private readonly ViewerLoop app;

    public ViewerPanels(ViewerLoop app) => this.app = app;

    // ── Display-only state ──────────────────────────────────────────────────
    // Every field here answers "what is shown", never "what is true". Nothing in this block affects
    // a pose, a clock or a camera — which is exactly the separation that had stopped being visible.
    public bool ShowPivots = true;
    public bool ShowAllPivots;
    public bool ShowBounds = true;
    public bool ShowSkeleton = true;
    public bool ShowRestGhost;
    public bool ShowAllBoneAxes;
    public bool ShowJoints = true;
    public bool ShowLeafStubs = true;
    public bool DeformBonesOnly = true;
    public float GizmoScale = 1f;
    public bool DepthTestGizmos = true;
    public bool ShowTrail = true;
    public bool ShowRootTrail = true;
    public float TrailSeconds = 1.5f;
    public float ThumbnailScale = 1f;
    private readonly ViewportPanel viewport = new();

    /// <summary>Whether the viewport window is up. The shell owns the flag; this forwards it.</summary>
    public ref bool ViewportOpen => ref viewport.Open;

    private string clipFilter = string.Empty;
    private int clipIndexA;
    private int clipIndexB;

    /// <summary>Which clip row is highlighted as the subject's. The root seeds it at load.</summary>
    public int SubjectClipIndex { get => clipIndexA; set => clipIndexA = value; }

    /// <summary>Which row is highlighted as the second clip's.</summary>
    public int SecondaryClipIndex { get => clipIndexB; set => clipIndexB = value; }

    // The clip list, the transport, and the selected bone — the panel half of "see a pose".
    //
    // Seventy-six clips is past the point where a list is browsable, hence the filter box: the
    // Rogue's are named by weapon and action, so typing "walk" or "2H" is how anyone actually finds
    // one. A list this long without a filter is a list nobody reads.
    public void DrawRigPanel()
    {
        if (app.Rig is null || app.Session is null) return;

        // Both numbers, because they answer different questions: how much of the app.Rig the mesh is
        // attached to, and how much of it has to be drawn to show those chains unbroken.
        ImGui.TextDisabled(
            $"{app.Rig.Skeleton.BoneCount} bones ({app.Rig.WeightedBoneCount} weighted, " +
            $"{app.Rig.DeformHierarchyCount} drawn) · {app.Rig.Clips.Count} clips · {app.Rig.Parts.Count} prims");

        var mode = (int)app.Session.Driven.Mode;
        if (ImGui.Combo("compose", ref mode, "single\0blend A to B\0additive B on A\0B masked onto A\0"))
        {
            app.Session.Driven.Mode = (PoseMode)mode;
        }

        if (app.Session.Driven.Mode != PoseMode.Single)
        {
            var weight = app.Session.Driven.Weight;
            var label = app.Session.Driven.Mode switch
            {
                PoseMode.Blend => "weight",
                PoseMode.Masked => "layer weight",
                _ => "overlay",
            };
            if (ImGui.SliderFloat(label, ref weight, 0f, 1f))
            {
                app.Session.Driven.Weight = weight;
            }
        }

        if (app.Session.Driven.Mode == PoseMode.Masked) DrawMask(app);

        DrawTransport(app.Session.Driven.Subject, "A");
        if (app.Session.Driven.Mode != PoseMode.Single) DrawTransport(app.Session.Driven.Secondary, "B");

        ImGui.Separator();
        // ##-prefixed id, so ImGui draws no label. The visible "filter" label sat between the
        // field and the button and squeezed "clear" down to "c" — a row that reads as a rendering
        // fault and is a layout one.
        ImGui.SetNextItemWidth(-56f);
        ImGui.InputText("##filter", ref clipFilter, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton("clear")) clipFilter = string.Empty;

        // Which player the list assigns to. A single list that always targets A would make picking
        // B's clip impossible in blend mode; two lists would double the height of the panel for one
        // extra bit of state.
        var target = app.Session.Driven.Mode == PoseMode.Single ? 0 : ImGui.GetIO().KeyShift ? 1 : 0;
        ImGui.TextDisabled(app.Session.Driven.Mode == PoseMode.Single
            ? "click to play"
            : target == 0 ? "click -> A   (hold shift -> B)" : "click -> B");

        if (ImGui.BeginChild("clips", new Vector2(0, 160), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < app.Rig.Clips.Count; i++)
            {
                var clip = app.Rig.Clips[i];
                if (clipFilter.Length > 0 &&
                    clip.Name.IndexOf(clipFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var selected = i == clipIndexA || (app.Session.Driven.Mode != PoseMode.Single && i == clipIndexB);
                var tag = i == clipIndexA ? "A" : i == clipIndexB && app.Session.Driven.Mode != PoseMode.Single ? "B" : " ";

                // A zero-length clip is a POSE, not a fault — the Rogue ships seven of them. Marked
                // rather than hidden: selecting one and seeing the body hold that shape is how you
                // find out which pose it is.
                var length = clip.Duration > 0 ? $"{clip.Duration,5:0.00}s" : "  pose";
                if (!ImGui.Selectable($"{tag} {clip.Name}  {length}", selected)) continue;

                if (target == 1)
                {
                    clipIndexB = i;
                    app.Session.Driven.Secondary.Clip = clip;
                }
                else
                {
                    clipIndexA = i;
                    app.Session.Driven.Subject.Clip = clip;
                    app.ResetTravel();
                }
            }
        }

        ImGui.EndChild();

        DrawInstancePanel();
        DrawAttachmentPanel();
        DrawUnreadPanel();
        DrawRootMotionPanel();
        DrawBonePanel();
    }

    // What each body is actually doing, and whether the mechanism is holding.
    //
    // The two rows that matter are the checkbox and the verdict beside it. Varied should read
    // "distinct"; lockstep is the control — every body on one clip at one instant — and if THAT
    // still shows three different walks, the slice is not doing what it claims.
    public void DrawInstancePanel()
    {
        if (app.Rig is null || app.Session is null) return;
        if (!ImGui.CollapsingHeader("instances", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.TextDisabled($"{app.Session.Count} of max {StudioRig.MaxInstances} · one draw, one palette buffer");

        if (app.Session.Count <= 1)
        {
            ImGui.TextDisabled("pass --instances N to draw more");
            return;
        }

        var lockstep = app.Session.Lockstep;
        if (ImGui.Checkbox("lockstep (negative control)", ref lockstep)) app.Session.Lockstep = lockstep;

        // Varied wants one distinct pose per body; lockstep wants exactly one in total. Stating the
        // expectation beside the count is what makes a reader able to tell a pass from a number.
        var want = app.Session.Lockstep ? 1 : app.Session.Count;
        var ok = app.Session.DistinctPoses == want;
        ImGui.SameLine();
        if (ok) ImGui.TextDisabled($"{app.Session.DistinctPoses}/{want} distinct poses");
        else ImGui.TextColored(new Vector4(1f, 0.4f, 0.35f, 1f), $"{app.Session.DistinctPoses} poses, expected {want}");

        if (ImGui.BeginChild("instancelist", new Vector2(0, 96), ImGuiChildFlags.Borders))
        {
            ImGui.Text($"0  {app.Session.Driven.Subject.Clip?.Name ?? "(rest)"}");
            ImGui.SameLine(220f);
            ImGui.TextDisabled($"t {app.Session.Driven.Subject.Time,5:0.00}  x{app.Session.Driven.Subject.Rate:0.00}");

            for (var i = 0; i < (app.Session.Count - 1); i++)
            {
                var other = app.Session[i + 1].Subject;
                ImGui.Text($"{i + 1}  {other.Clip?.Name ?? "(rest)"}");
                ImGui.SameLine(220f);
                ImGui.TextDisabled($"t {other.Time,5:0.00}  x{other.Rate:0.00}");
            }
        }

        ImGui.EndChild();
    }

    /// <summary>Which bones the layer reaches, and how softly it stops reaching them.</summary>
    /// <remarks>
    /// <b>The root is picked from the rig's own bone list, not typed.</b> A mask names a bone, and a
    /// bone's name comes from whoever exported the rig — "Spine", "spine_01" and "mixamorig:Spine"
    /// are all real, and a viewer that makes you guess which is a viewer that mostly reports typos. The
    /// names are right there; offering them is free.
    /// <para>
    /// The falloff has no correct value. It depends on the rig and on taste, which is exactly why it
    /// is a slider beside a skeleton that paints it — the number is found by looking at the joint it
    /// softens, and there is no other way to find it.
    /// </para>
    /// </remarks>
    private void DrawMask(ViewerLoop app)
    {
        var session = app.Session?.Driven;
        if (session is null || app.Rig is null) return;

        var bones = app.Rig.Skeleton.Bones;

        if (ImGui.BeginCombo("mask from", session.Mask is null ? "<none>" : session.MaskRoot))
        {
            for (var i = 0; i < bones.Length; i++)
            {
                if (!ImGui.Selectable(bones[i].Name, bones[i].Name == session.MaskRoot)) continue;
                session.SetMask(bones[i].Name, session.MaskFalloff);
            }
            ImGui.EndCombo();
        }

        var falloff = session.MaskFalloff;
        if (ImGui.SliderInt("falloff bones", ref falloff, 0, 6))
        {
            session.SetMask(session.MaskRoot, falloff);
        }

        // THE NUMBER BEHIND THE PICTURE. A mask reaching every bone is a whole-body blend wearing a
        // mask's name; one reaching none is a layer that costs and does nothing. Both look plausible
        // on a slider and neither does on a count.
        ImGui.TextDisabled(session.Mask is null
            ? "no mask — B is ignored"
            : $"reaches {session.Mask.Reach()} of {bones.Length} bones, " +
              $"{session.Mask.Reach(0.999f)} fully");
    }

    public void DrawTransport(ClipPlayer player, string label)
    {
        ImGui.PushID(label);
        var paused = player.Paused;
        if (ImGui.Button(paused ? $"play {label}" : $"pause {label}")) player.Paused = !paused;
        ImGui.SameLine();

        // A thirtieth of a second rather than a keyframe, because a clip's keyframes are not
        // uniformly spaced and "one frame" in an animator's sense is a sampling rate, not a track
        // entry. Stepping by time is also what makes the root-motion readout comparable between
        // steps.
        if (ImGui.Button("<")) player.Step(-1.0 / 30.0);
        ImGui.SameLine();
        if (ImGui.Button(">")) player.Step(1.0 / 30.0);
        ImGui.SameLine();
        ImGui.TextDisabled($"{player.Time,5:0.000} / {player.Duration:0.00}s");

        var t = (float)player.Time;
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.SliderFloat($"t {label}", ref t, 0f, MathF.Max(0.0001f, (float)player.Duration)))
        {
            // A scrub is a jump, not travel — ClipPlayer clears the root delta for exactly this, so
            // dragging the slider does not launch the body across the map.
            player.ScrubTo(t);
            player.Paused = true;
        }

        var rate = player.Rate;
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.SliderFloat($"rate {label}", ref rate, -3f, 3f)) player.Rate = rate;
        ImGui.PopID();
    }


    /// <summary>What the rig hangs off its joints, and which of it to draw.</summary>
    /// <remarks>
    /// <b>This panel is the reason the arc exists.</b> The importer dropped these for as long as it
    /// existed and nobody noticed, because a character with empty hands looks exactly like a
    /// character — so the fix is not only that they load, it is that you can see the list and see
    /// what happens when you tick one.
    /// </remarks>
    public void DrawAttachmentPanel()
    {
        var rig = app.Rig;
        if (rig is null) return;

        if (!ImGui.CollapsingHeader($"attachments ({rig.Attachments.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (rig.Attachments.Count == 0)
        {
            // Said rather than left blank. An empty panel and a panel that is not there look the
            // same, and "this rig has none" is a different fact from "this build cannot show them".
            ImGui.TextDisabled("this rig hangs nothing off its joints");
            return;
        }

        var visible = app.VisibleAttachments;

        // <b>All / none, because the interesting states are at the ends.</b> None is the honest
        // default and All is the picture that shows you what the asset actually contains — four
        // weapons in one hand, which is a thing to see once and then never leave on.
        if (ImGui.SmallButton("all"))
        {
            foreach (var a in rig.Attachments) visible.Add(a.Name);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("none")) visible.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled($"{visible.Count} shown");

        foreach (var attachment in rig.Attachments)
        {
            var shown = visible.Contains(attachment.Name);
            if (ImGui.Checkbox(attachment.Name, ref shown))
            {
                if (shown) visible.Add(attachment.Name);
                else visible.Remove(attachment.Name);
            }

            // The joint is the part that is worth seeing beside the name: an attachment on the
            // wrong bone is the failure this has, and it is invisible unless the bone is written
            // down next to the thing hanging off it.
            ImGui.SameLine();
            ImGui.TextDisabled($"on {attachment.JointName} [{attachment.JointIndex}]");
        }

        // ── per body ────────────────────────────────────────────────────────────────────────
        //
        // <b>The reason attachments were built per instance at all: one body with a knife and its
        // neighbour with a crossbow.</b> Bodies without an override are not listed and cost nothing;
        // the common case is everyone carrying the same thing, and a panel that demanded a row per
        // body would make the ordinary case the expensive one.
        var bodies = app.Session?.Count ?? 1;
        if (bodies > 1)
        {
            ImGui.Separator();
            ImGui.TextDisabled($"per body ({bodies} drawn)");

            for (var i = 0; i < bodies; i++)
            {
                var own = app.HasAttachmentOverride(i);
                var label = own ? $"body {i} (own)" : $"body {i} (shared)";
                if (!ImGui.TreeNode(label)) continue;

                if (!own)
                {
                    if (ImGui.SmallButton($"give body {i} its own set"))
                    {
                        app.OverrideAttachmentsFor(i);
                    }

                    ImGui.TextDisabled("  follows the shared selection above");
                }
                else
                {
                    if (ImGui.SmallButton($"share##{i}")) app.ClearAttachmentOverride(i);
                    var mine = app.AttachmentsForInstance(i);
                    foreach (var attachment in rig.Attachments)
                    {
                        var on = mine.Contains(attachment.Name);
                        if (ImGui.Checkbox($"{attachment.Name}##body{i}", ref on))
                        {
                            if (on) mine.Add(attachment.Name);
                            else mine.Remove(attachment.Name);
                        }
                    }
                }

                ImGui.TreePop();
            }
        }

        // Several on one joint is ordinary — four of the Rogue's six share handslot.r — so the
        // overlap a viewer sees with "all" on is expected rather than a bug being demonstrated.
        var crowded = rig.Attachments
            .GroupBy(a => a.JointName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1 && g.Count(x => visible.Contains(x.Name)) > 1)
            .ToArray();
        foreach (var g in crowded)
        {
            ImGui.TextDisabled($"  {g.Count(x => visible.Contains(x.Name))} shown on '{g.Key}' — they overlap");
        }
    }

    /// <summary>Channels this file carries that the importer did not read.</summary>
    /// <remarks>
    /// <para>
    /// <b>This panel used to have two halves and now has one, which is the point.</b> It listed
    /// mesh NODES the import dropped alongside the attributes it skipped — and both dropping rules
    /// turned out to belong to this importer rather than to glTF. A mesh on a second skin is read
    /// now; so is a static mesh under no joint. Nothing is left to report, so that half is gone
    /// rather than sitting here always showing zero.
    /// </para>
    /// <para>
    /// <b>Silent when there is nothing to say.</b> A panel that is always present trains you to stop
    /// reading it, and "this file was read whole" is the ordinary case.
    /// </para>
    /// </remarks>
    public void DrawUnreadPanel()
    {
        var rig = app.Rig;
        if (rig is null || rig.Ignored.Count == 0) return;

        if (!ImGui.CollapsingHeader($"channels not read ({rig.Ignored.Count})###unread",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        foreach (var i in rig.Ignored)
        {
            ImGui.TextUnformatted($"  {i.Semantic}");
            ImGui.SameLine();
            ImGui.TextDisabled($"{i.Primitives} prim — {i.Explanation}");
        }
    }

    public void DrawRootMotionPanel()
    {
        if (app.Session is null) return;
        if (!ImGui.CollapsingHeader("root motion")) return;

        var subject = app.Session.Driven.Subject;
        var perCycle = subject.Clip is { } clip
            ? RootMotion.PerCycle(clip, subject.RootBone, subject.RestPose.Locals[subject.RootBone])
            : RootMotion.None;

        // <b>The number that says whether a clip travels at all.</b> Most authored loops are made in
        // place — the root returns to where it started, per-cycle travel is ~0, and locomotion is the
        // game's job. A clip with real travel reports a metre or two here, and that is the clip whose
        // delta is worth driving anything with.
        ImGui.Text($"per cycle  {perCycle.Translation.X:0.000}, {perCycle.Translation.Y:0.000}, {perCycle.Translation.Z:0.000}");
        ImGui.TextDisabled($"           {perCycle.Distance:0.000} m, {RigAnimation.DegreesOf(perCycle.Rotation):0.0}°");
        var travel = app.Session.Driven.RootTravel;
        ImGui.Text($"travelled  {travel.X:0.000}, {travel.Y:0.000}, {travel.Z:0.000}");
        ImGui.TextDisabled(
            $"           {travel.Length():0.000} m net, {RigAnimation.DegreesOf(app.Session.Driven.RootTurn):0.0}° net turn");
        ImGui.TextDisabled($"           {app.Session.Driven.RootTurnPathDegrees:0.0}° of turning done to get there");

        // The whole row, not just the body this panel drives — see RigInstances.DriveRoot.
        var drive = app.Session.DriveRoot;
        if (ImGui.Checkbox("drive the model", ref drive))
        {
            app.Session.DriveRoot = drive;
        }

        ImGui.SameLine();
        ImGui.Checkbox("trail", ref ShowRootTrail);
        if (ImGui.Button("reset travel")) app.ResetTravel();
        ImGui.SameLine();
        ImGui.TextDisabled($"{app.RootPathCount} samples");
    }

    // The selected bone, in both spaces. Local TRS is what a clip authored; world is where it ended
    // up. Seeing them together is what separates "this bone's track is wrong" from "this bone's
    // PARENT is wrong and it is being carried" — the single most common misreading of a bad pose.
    public void DrawBonePanel()
    {
        if (app.Rig is null || app.Session is null) return;
        if (!ImGui.CollapsingHeader("bones")) return;

        if (ImGui.BeginChild("bonelist", new Vector2(0, 140), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < app.Rig.Skeleton.BoneCount; i++)
            {
                var bone = app.Rig.Skeleton.Bones[i];
                var indent = 0;
                for (var p = bone.ParentIndex; p >= 0; p = app.Rig.Skeleton.Bones[p].ParentIndex) indent++;
                // A control bone is dimmed rather than hidden: it is still in the palette and still
                // animated, so a reader looking for "why is nothing moving" needs to be able to find
                // it — just not to have it shouting alongside the bones the mesh follows.
                var deform = app.Rig.WeightedBones[i];
                var label = new string(' ', indent * 2) + bone.Name + (deform ? string.Empty : "  ·");
                if (!deform) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.6f, 1f));
                if (ImGui.Selectable($"{label}##{i}", app.Selection.Bone == i)) app.Selection.Bone = i;
                if (!deform) ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();

        if (app.Selection.Bone < 0 || app.Selection.Bone >= app.Rig.Skeleton.BoneCount) return;

        var local = app.Session.Driven.Posed.Locals[app.Selection.Bone];
        var rest = app.Session.Driven.Subject.RestPose.Locals[app.Selection.Bone];
        var world = app.Session.Driven.BoneWorlds[app.Selection.Bone] * app.Rig.MeshNodeTransform * app.SubjectPlacementOf;

        ImGui.TextDisabled($"bone {app.Selection.Bone} · parent {app.Rig.Skeleton.Bones[app.Selection.Bone].ParentIndex}");
        ImGui.Text($"local T {local.Translation.X:0.000}, {local.Translation.Y:0.000}, {local.Translation.Z:0.000}");
        ImGui.Text($"local R {local.Rotation.X:0.000}, {local.Rotation.Y:0.000}, {local.Rotation.Z:0.000}, {local.Rotation.W:0.000}");
        ImGui.Text($"local S {local.Scale.X:0.000}, {local.Scale.Y:0.000}, {local.Scale.Z:0.000}");
        ImGui.Separator();
        ImGui.Text($"world   {world.M41:0.000}, {world.M42:0.000}, {world.M43:0.000}");

        // How far this bone has moved off its rest value, which is the one number that answers
        // "is this clip even touching this bone?" A bone a clip has no track for reads exactly 0.
        var offset = (local.Translation - rest.Translation).Length();
        var turned = RigAnimation.DegreesOf(Quaternion.Inverse(rest.Rotation) * local.Rotation);
        ImGui.TextDisabled($"from rest  {offset:0.000} m, {turned:0.0}°");
    }

    // The node tree, and what the selected one actually is.
    //
    // The same facts blix inspect prints — name, parent, composed pivot, bounds, vertex
    // count — except the selected node is simultaneously outlined in the app.Scene, so a number and
    // the thing it describes are in front of you at once. That pairing is the entire reason this
    // exists; either half alone is what the project already had.
    public void DrawNodeList()
    {
        if (app.Model is null) return;

        ImGui.TextDisabled($"{app.Model.Nodes.Count} nodes · {app.Model.Parts.Count} meshes");
        if (ImGui.BeginChild("nodes", new Vector2(0, 150), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < app.Model.Nodes.Count; i++)
            {
                var node = app.Model.Nodes[i];
                if (node.PrimitiveCount == 0 && !ShowAllPivots) continue;

                var label = node.PrimitiveCount > 0
                    ? $"{node.Name}  ({node.VertexCount} v)"
                    : $"{node.Name}";
                if (ImGui.Selectable(label, app.Selection.Node == i)) app.Selection.Node = i;
            }
        }

        ImGui.EndChild();

        if (app.Selection.Node < 0 || app.Selection.Node >= app.Model.Nodes.Count) return;

        var s = app.Model.Nodes[app.Selection.Node];
        var world = s.WorldTransform * app.ModelTransform;
        ImGui.TextDisabled($"parent {s.ParentIndex}   prims {s.PrimitiveCount}");

        // The composed translation IS the app.Rig pivot — the number a game drives the part about.
        ImGui.Text($"pivot  {world.M41:0.000}, {world.M42:0.000}, {world.M43:0.000}");

        if (Matrix4x4.Decompose(s.LocalTransform, out var ls, out var lr, out var lt))
        {
            ImGui.Text($"local T {lt.X:0.000}, {lt.Y:0.000}, {lt.Z:0.000}");
            ImGui.Text($"local S {ls.X:0.000}, {ls.Y:0.000}, {ls.Z:0.000}");
            ImGui.TextDisabled($"local R {lr.X:0.00}, {lr.Y:0.00}, {lr.Z:0.00}, {lr.W:0.00}");
        }
        else
        {
            ImGui.TextDisabled("local transform does not decompose (sheared?)");
        }
    }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(340, 440), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(20, 20), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("lab"))
        {
            ImGui.End();
            return;
        }

        // Grouped rather than one flat list of knobs. The panel grew a control at a time and had
        // become a wall — which is the same complaint about legibility that got the gizmos
        // depth-tested, one layer up.
        if (app.Rig is not null) ImGui.TextDisabled(Path.GetFileName(app.Rig.SourcePath));
        else if (app.Model is not null) ImGui.TextDisabled(Path.GetFileName(app.Model.SourcePath));
        else ImGui.TextDisabled("nothing loaded — pass --model <path.glb> or --rig <rigged.glb>");

        if (ImGui.CollapsingHeader("image", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var exposure = app.Renderer.Look.Exposure;
            if (ImGui.SliderFloat("exposure", ref exposure, 0.1f, 4f)) app.Renderer.Look.Exposure = exposure;

            var mode = (int)app.Renderer.Look.TonemapMode;
            if (ImGui.Combo("tonemap", ref mode, "ACES\0AgX\0Reinhard\0Neutral\0"))
            {
                app.Renderer.Look.TonemapMode = mode;
            }
        }

        if (ImGui.CollapsingHeader("sun", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // The sun's own controls are GONE from here, not moved: StudioScene declares azimuth,
            // elevation, intensity, ambient and shadow extent with [Tune], so the diagnostics
            // overlay renders them and the command line parses them without this file mentioning
            // any of it. What is left below is this viewer's own — a trail is not the stage's.
            ImGui.TextDisabled("sun, ambient and shadow extent are in the Scene panel");

            ImGui.Checkbox("trail", ref ShowTrail);
            if (ShowTrail) ImGui.SliderFloat("trail seconds", ref TrailSeconds, 0.25f, 8f);
        }

        if (ImGui.CollapsingHeader("gizmos", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("depth-tested", ref DepthTestGizmos);
            if (app.Model is not null)
            {
                ImGui.Checkbox("node pivots", ref ShowPivots);
                ImGui.SameLine();
                ImGui.Checkbox("bounds", ref ShowBounds);
                if (ShowPivots) ImGui.Checkbox("transform-only nodes too", ref ShowAllPivots);
            }

            // A closed window needs a way back, and the X is the only way out. Here rather than in
            // a menu bar the viewer does not have.
            ImGui.Checkbox("viewport panel", ref ViewportOpen);

            if (app.Rig is not null)
            {
                ImGui.Checkbox("skeleton", ref ShowSkeleton);
                ImGui.SameLine();
                ImGui.Checkbox("joints", ref ShowJoints);
                ImGui.Checkbox("rest ghost", ref ShowRestGhost);
                ImGui.SameLine();
                ImGui.Checkbox("all axes", ref ShowAllBoneAxes);
                ImGui.Checkbox("leaf stubs", ref ShowLeafStubs);
                ImGui.SameLine();
                // The default, because 20 of the Rogue's 41 bones are IK handles and roll controls
                // hanging off the root, and drawing them makes a starburst at the feet.
                ImGui.Checkbox("deform only", ref DeformBonesOnly);
                ImGui.SliderFloat("gizmo size", ref GizmoScale, 0.25f, 4f);
            }
        }

        if (app.Rig is not null && ImGui.CollapsingHeader("animation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRigPanel();
        }

        if (app.Model is not null && ImGui.CollapsingHeader("nodes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawNodeList();
        }

        if (ImGui.CollapsingHeader("materials", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawMaterialPanel();
        }

        DrawImagePanel();
        DrawViewportPanel();

        ImGui.Separator();
        ImGui.TextDisabled(app.Rig is not null
            ? $"drag to orbit · wheel to zoom · space plays · left/right step · {app.Frames} app.Frames"
            : $"drag to orbit · wheel to zoom · {app.Frames} app.Frames");
        ImGui.End();
    }

    // Transport on the keyboard, because scrubbing a pose means looking at the app.Model rather than at
    // the slider you are dragging. Space and the arrows are what every animation tool uses; a lab
    // that invented its own would be asking to be relearned.
    // <b>A second camera, rendered off-screen and shown in a panel.</b> Stage B of the view arc.
    //
    // The picture here is the SAME app.Scene through a different view-projection, drawn by the same
    // pipelines into a target of its own — not a copy of the main view, which would prove a texture
    // can be drawn (stage A did that) and nothing about views.
    //
    // Two things this settles, both written down because stage D has to weigh them:
    //
    //   • <b>The panel's rect is one frame old.</b> UI layout runs after views are declared and
    //     after the app.Scene is recorded, so this frame's picture is drawn at last frame's size. A
    //     resize therefore shows one frame of stale aspect. That is what every immediate-mode
    //     editor does; the alternative is splitting layout from submission, which moves who owns
    //     the frame.
    //   • <b>The image rect is not the panel rect.</b> The target has its own aspect and the panel
    //     has another, so the picture is fitted inside with letterboxing. Stage C picks through
    //     THAT rect, not the panel's — which is exactly the offset-and-scale case ViewDeclaration's
    //     two rectangles exist for, and the case a full-window view could never exercise.
    /// <summary>
    /// The viewport, drawn by the shell — and the pick, which stays here.
    /// </summary>
    /// <remarks>
    /// What is left of a 130-line panel: a call, and the one decision the widget must not make.
    /// <see cref="ViewportPanel.Clicked"/> says the pointer was pressed on the picture; what that
    /// MEANS — which view declaration to cast through, whether a node or a joint is selected —
    /// is this tool's business and nobody else's.
    /// </remarks>
    public void DrawViewportPanel()
    {
        viewport.Draw(
            app.ViewportId,
            app.ViewportCamera,
            app.Host?.LogicalSize ?? (16, 9),
            footer: $"frame {app.Frames} · yaw {app.ViewportCamera.Yaw:0.00} · " +
                    $"{viewport.ImageSize.X:0}x{viewport.ImageSize.Y:0}");

        app.ViewportPanelSize = viewport.PanelSize;
        app.ViewportImageMin = viewport.ImageMin;
        app.ViewportImageSize = viewport.ImageSize;

        if (viewport.Clicked is { } pointer && app.ViewportView is { } declared)
        {
            app.PickThrough(declared, pointer);
        }
    }

    // <b>The asset's own textures, drawn.</b> Stage A of the view arc, and the first thing in this
    // engine to put a non-font image on screen.
    //
    // The viewer has reported texture COUNTS since it learned to load a model — "1 image across 12
    // parts" — and a count is the least interesting fact about a texture. Which image, at what
    // size, and whether it is the one you meant are all answerable by looking, and until the UI
    // layer could read `cmd.TextureId` there was nowhere to look.
    public void DrawImagePanel()
    {
        if (app.UiImages.Count == 0 && app.ShadowMapId == 0) return;

        // <b>Its own window, not a section of the viewer panel.</b> It was a collapsing header at the
        // bottom of a panel that already holds image, sun, gizmo, animation, instance and node
        // sections — and it drew nothing, because ImGui clips items scrolled out of a window and
        // emits no draw for them. The draw count said 3 before and 3 after, which is what a
        // correct feature looks like when nothing can see it.
        //
        // A picture also wants room a sidebar cannot give it, and Stage B of the view arc needs a
        // window to put a viewport in. This is that window, arriving early.
        ImGui.SetNextWindowSize(new Vector2(320, 420), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(480, 20), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("images"))
        {
            ImGui.End();
            return;
        }

        ImGui.SliderFloat("thumb", ref ThumbnailScale, 0.25f, 3f);

        foreach (var (label, id, width, height) in app.UiImages)
        {
            ImGui.TextDisabled($"{label}  {width}x{height}");

            // Fitted to the panel rather than drawn at native size: a 1024px atlas in a 340px
            // panel would push everything else off the edge, and the aspect has to be kept or the
            // thing being inspected is not the thing that shipped.
            var available = MathF.Max(64f, ImGui.GetContentRegionAvail().X);
            var side = MathF.Min(available, 160f * ThumbnailScale);
            var scale = height > 0 ? side / width : side;
            ImGui.Image(id, new Vector2(side, MathF.Max(16f, height * scale)));
        }

        if (app.ShadowMapId == 0)
        {
            ImGui.End();
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled($"sun depth  {StudioRenderer.ShadowMapSize}x{StudioRenderer.ShadowMapSize}");

        // Red-scale, and that is the format rather than a fault: a single-channel depth image
        // sampled by a colour shader is (d, 0, 0, 1). It answers a coarse question — is the caster
        // drawing, and does the sun cover the subject — and a shader that knew it was depth is a
        // second pipeline the arc has not earned yet.
        var shadowSide = MathF.Min(MathF.Max(64f, ImGui.GetContentRegionAvail().X), 160f * ThumbnailScale);
        ImGui.Image(app.ShadowMapId, new Vector2(shadowSide, shadowSide));
        ImGui.End();
    }

    /// <summary>
    /// Every material the asset names, with the colour it is being drawn in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The panel exists because a kit asset ships a name and no colour.</b> All 40 materials
    /// across this tree's nature kit are <c>baseColorFactor = (1,1,1,1)</c> with no texture, so this
    /// viewer rendered them faithfully and still could not show what any of them looks like in a
    /// game. Something downstream turns "Grass" into a green; this is where you find out which
    /// green, by trying one.
    /// </para>
    /// <para>
    /// <b>It knows nothing about any game.</b> RTSGame's SettlementArt holds the real table and the
    /// studio must not reference a game — but the NAME is the surface that table keys on, so listing
    /// names and taking colours shows exactly that surface. Audition a green, read the RGB off the
    /// swatch, go and write it down where the table lives.
    /// </para>
    /// <para>
    /// Gathered per frame rather than cached. It is a few dozen strings against a model that is
    /// reloaded by a keystroke, and a cache here is a thing that can show the previous asset's
    /// materials — which is the failure this panel would be least able to explain.
    /// </para>
    /// </remarks>
    private void DrawMaterialPanel()
    {
        materials.Clear();
        if (app.Model is { } model)
        {
            foreach (var part in model.Parts) Note(part.MaterialName, part.BaseColour);
        }

        if (app.Rig is { } rig)
        {
            foreach (var part in rig.Parts) Note(part.MaterialName, part.BaseColour);
            foreach (var a in rig.Attachments) Note(a.MaterialName, a.BaseColour);
            foreach (var sp in rig.StaticParts) Note(sp.MaterialName, sp.BaseColour);
        }

        if (materials.Count == 0)
        {
            ImGui.TextDisabled("this asset names no materials");
            return;
        }

        var tints = app.Tints;
        ImGui.TextDisabled($"{materials.Count} material(s) · {tints.Count} overridden");
        ImGui.SameLine();
        if (ImGui.SmallButton("reset all")) tints.ClearAll();

        if (ImGui.BeginChild("materiallist", new Vector2(0, 190), ImGuiChildFlags.Borders))
        {
            foreach (var (name, assetColour) in materials)
            {
                var colour = tints.Resolve(name, assetColour);
                // ColorEdit3 writes through, so the override is set the moment it changes rather
                // than on some apply button — the point is to watch the model while dragging.
                if (ImGui.ColorEdit3(name, ref colour, ImGuiColorEditFlags.NoInputs)) tints.Set(name, colour);

                if (tints.Has(name))
                {
                    ImGui.SameLine();
                    // The RGB is the deliverable. It is going into a table in another repository
                    // path by hand, so it has to be readable, not just visible.
                    ImGui.TextDisabled($"{colour.X:0.000}, {colour.Y:0.000}, {colour.Z:0.000}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"reset##{name}")) tints.Clear(name);
                }
                else
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(as authored)");
                }
            }
        }

        ImGui.EndChild();

        void Note(string name, Vector3 assetColour)
        {
            if (name.Length == 0) return;
            foreach (var (existing, _) in materials)
            {
                if (string.Equals(existing, name, StringComparison.Ordinal)) return;
            }

            materials.Add((name, assetColour));
        }
    }

    private readonly List<(string Name, Vector3 AssetColour)> materials = new();

}
