using System.Numerics;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Labs.Toolchain;

/// <summary>
/// Draws a pose: bones as lines, joints as crosses, a selected bone's axes, and the rest pose behind it.
/// </summary>
/// <remarks>
/// <b>Why this is the first stage of the animation arc and not the last.</b> Without it, "the character
/// folded inside out" has two indistinguishable causes — the clip was already wrong, or the solver that
/// read it was. A skeleton drawn over the mesh separates them in one glance: bones in the right places
/// under a mangled mesh is a skinning fault; bones in the wrong places is a clip or a pose fault. Every
/// stage after this one is debuggable because of it.
/// <para>
/// <b>In the lab library rather than in the viewer.</b> The capture tool draws the same skeleton, and a
/// capture that disagreed with the window would be worse than no capture — a screenshot is only evidence
/// if it is the same picture. That is the reason the lab is a library at all.
/// </para>
/// <para>
/// Everything here is <see cref="DebugDrawChannel"/> vocabulary that already existed. The arc needed no
/// new debug primitive, which is the sort of thing worth saying out loud: the drawing layer was ready
/// and the missing piece was that nothing had ever asked it about a skeleton.
/// </para>
/// </remarks>
public static class LabSkeletonView
{
    /// <summary>What to draw. Every flag is a question someone asked while looking at a broken pose.</summary>
    /// <remarks>
    /// <b>Build one with <see cref="Default"/>, never with <c>new Options()</c>.</b> A record struct
    /// always keeps an implicit parameterless constructor that zero-initialises every field, and it
    /// does NOT run the primary constructor's default values — so <c>new Options()</c> means "draw no
    /// bones, no joints, no stubs, at scale zero", which renders as nothing at all and reads as a
    /// broken overlay. The defaults below are for callers who name at least one argument; the
    /// <see cref="Default"/> property is for everyone else.
    /// </remarks>
    public readonly record struct Options(
        bool Bones = true,
        bool Joints = true,
        bool RestGhost = false,
        bool AllAxes = false,
        // Draw a leaf bone as a stub along its own +Y, so a hand or a foot is visible as a
        // direction rather than as a bare point. glTF joints carry no length, so this is a
        // drawing convention, not a fact about the rig — hence a flag.
        bool LeafStubs = true,
        float Scale = 1f)
    {
        /// <summary>Bones, joints and leaf stubs at full size — what you want unless you have said otherwise.</summary>
        public static Options Default { get; } = new(
            Bones: true, Joints: true, RestGhost: false, AllAxes: false, LeafStubs: true, Scale: 1f);
    }

    private static readonly GraphicsColor BoneColour = new(0.45f, 0.85f, 1f, 1f);
    private static readonly GraphicsColor JointColour = new(1f, 0.85f, 0.35f, 1f);
    private static readonly GraphicsColor RestColour = new(0.35f, 0.38f, 0.45f, 1f);
    private static readonly GraphicsColor SelectedColour = new(1f, 1f, 1f, 1f);

    // The mask ramp: out, halfway, in. THREE stops rather than two, because the interesting part of a
    // mask is not its inside or its outside — it is the fade between them, and a two-stop ramp makes
    // a falloff of two bones look like one dark bone.
    private static readonly GraphicsColor MaskOut = new(0.26f, 0.28f, 0.34f, 1f);
    private static readonly GraphicsColor MaskHalf = new(1f, 0.72f, 0.2f, 1f);
    private static readonly GraphicsColor MaskIn = new(1f, 0.3f, 0.85f, 1f);

    /// <summary>What colour a bone is when a mask is shown: how much of a layer reaches it.</summary>
    /// <remarks>
    /// <b>The whole reason stage D2 exists.</b> "Which bones" is a question no amount of arithmetic
    /// answers as well as a picture — a subtree is easy to describe and hard to be sure of — and the
    /// falloff is a number nobody can derive. Painting the weight onto the skeleton makes both
    /// things you check by looking.
    /// </remarks>
    private static GraphicsColor MaskColour(float weight)
    {
        var w = Math.Clamp(weight, 0f, 1f);
        return w <= 0.5f ? Mix(MaskOut, MaskHalf, w * 2f) : Mix(MaskHalf, MaskIn, (w - 0.5f) * 2f);
    }

    private static GraphicsColor Mix(GraphicsColor a, GraphicsColor b, float t) =>
        new(a.Red + ((b.Red - a.Red) * t),
            a.Green + ((b.Green - a.Green) * t),
            a.Blue + ((b.Blue - a.Blue) * t),
            1f);

    /// <summary>
    /// Draws the skeleton implied by <paramref name="boneWorlds"/>, placed by <paramref name="modelTransform"/>.
    /// </summary>
    /// <param name="boneWorlds">
    /// Object-space bone transforms from <see cref="LabRig.ComputeBoneWorlds"/> — NOT palette matrices.
    /// A palette matrix's translation is a displacement from rest, so a skeleton drawn from one collapses
    /// into a knot at the origin: correct arithmetic, wrong question.
    /// </param>
    /// <param name="restWorlds">
    /// The same, for the rest pose. Drawn behind in grey when <c>Options.RestGhost</c> is set, which is
    /// what turns "this looks a bit off" into "this bone is 30 degrees out and the rest are fine".
    /// </param>
    /// <param name="include">
    /// Optional per-bone filter. <c>LabRig.DeformHierarchy</c> is the one to pass, NOT
    /// <c>LabRig.WeightedBones</c>: a joint no vertex weights can still carry a chain that several do,
    /// and filtering on the literal census leaves those chains as floating segments. A rig's IK handles
    /// and roll controls skin nothing and hang off the root, so drawing all of them turns a skeleton
    /// into a starburst at the character's feet. Null draws everything, which is the honest default
    /// when nobody has said which bones the question is about.
    /// </param>
    public static void Draw(
        DebugContext debug,
        Skeleton skeleton,
        IReadOnlyList<Matrix4x4> boneWorlds,
        Matrix4x4 modelTransform,
        Options options,
        int selectedBone = -1,
        IReadOnlyList<Matrix4x4>? restWorlds = null,
        IReadOnlyList<bool>? include = null,
        BoneMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(debug);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(boneWorlds);

        using var scope = debug.Scope("skeleton");

        bool Shown(int bone) => include is null || (bone < include.Count && include[bone]);

        // Sized off the rig rather than fixed, because the same lab loads a 1.8 m character and a
        // 14 m tank and a joint cross authored for one is invisible or enormous on the other.
        var span = Span(skeleton, boneWorlds, modelTransform, include);

        // A zero scale draws zero-length lines, which is invisible and looks exactly like a
        // skeleton that failed to build. Treating it as 1 means a zero-initialised Options is at
        // worst wrong about WHICH parts to draw, never silently empty.
        var scale = options.Scale > 0f ? options.Scale : 1f;
        var tick = MathF.Max(0.005f, span * 0.012f) * scale;
        var axis = MathF.Max(0.01f, span * 0.05f) * scale;

        if (options.RestGhost && restWorlds is not null)
        {
            for (var i = 0; i < skeleton.BoneCount; i++)
            {
                var parent = skeleton.Bones[i].ParentIndex;
                if (parent < 0 || !Shown(i)) continue;
                debug.Draw.Line(
                    $"rest/{i}",
                    Origin(restWorlds[i] * modelTransform),
                    Origin(restWorlds[parent] * modelTransform),
                    RestColour);
            }
        }

        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (!Shown(i)) continue;
            var world = boneWorlds[i] * modelTransform;
            var origin = Origin(world);
            var selected = i == selectedBone;

            // A bone's colour says one of two things: which one is selected, or how much of the layer
            // reaches it. Selection wins, because it is the thing you are pointing at.
            var painted = mask is not null && i < mask.BoneCount;
            var boneColour = selected ? SelectedColour : painted ? MaskColour(mask![i]) : BoneColour;
            var jointColour = selected ? SelectedColour : painted ? MaskColour(mask![i]) : JointColour;

            if (options.Bones)
            {
                var parent = skeleton.Bones[i].ParentIndex;
                if (parent >= 0)
                {
                    // The line belongs to the CHILD: a bone is the segment from its parent's joint to
                    // its own, and naming it after the child is what makes "bone 17 is wrong" point at
                    // one line rather than at a fan of them.
                    debug.Draw.Line(
                        $"bone/{i}",
                        Origin(boneWorlds[parent] * modelTransform),
                        origin,
                        boneColour);
                }
                else
                {
                    // A root has no parent to draw from; a cross marks where the rig's origin sits,
                    // which is the thing root motion moves.
                    debug.Draw.Cross($"root/{i}", origin, tick * 2f, boneColour);
                }
            }

            if (options.Joints)
            {
                debug.Draw.Cross($"joint/{i}", origin, tick, jointColour);
            }

            if (options.LeafStubs && options.Bones && IsLeaf(skeleton, i, include))
            {
                // +Y is the glTF/Blender joint convention: a bone points along its own Y toward its
                // child. Stated because it is a convention and not a measurement — a rig authored
                // another way gets stubs pointing sideways, and seeing that is the point.
                debug.Draw.Line(
                    $"leaf/{i}", origin, origin + (Row(world, 1) * tick * 3f),
                    boneColour);
            }

            if (options.AllAxes || selected) DrawAxes(debug, i, world, selected ? axis : axis * 0.4f);
        }
    }

    /// <summary>The local axes of one bone, in world space — where THIS joint thinks forward is.</summary>
    public static void DrawAxes(DebugContext debug, int bone, Matrix4x4 world, float length)
    {
        var origin = Origin(world);
        debug.Draw.Line($"axis/{bone}/x", origin, origin + (Row(world, 0) * length), new GraphicsColor(0.95f, 0.3f, 0.3f, 1f));
        debug.Draw.Line($"axis/{bone}/y", origin, origin + (Row(world, 1) * length), new GraphicsColor(0.3f, 0.95f, 0.4f, 1f));
        debug.Draw.Line($"axis/{bone}/z", origin, origin + (Row(world, 2) * length), new GraphicsColor(0.4f, 0.55f, 0.95f, 1f));
    }

    /// <summary>Whether no DRAWN bone claims <paramref name="bone"/> as its parent.</summary>
    /// <remarks>
    /// Leafness depends on the filter, not only on the rig: with IK controls hidden, a foot whose only
    /// children are roll handles becomes a leaf and earns a stub. Ignoring the filter here would leave
    /// the hands and feet of a filtered skeleton as bare points.
    /// </remarks>
    public static bool IsLeaf(Skeleton skeleton, int bone, IReadOnlyList<bool>? include = null)
    {
        // Children always follow their parent (the hierarchy-order invariant), so the search can
        // start after the bone rather than at zero.
        for (var i = bone + 1; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].ParentIndex != bone) continue;
            if (include is null || (i < include.Count && include[i])) return false;
        }

        return true;
    }

    /// <summary>Largest extent across every joint position — the number every gizmo size is a fraction of.</summary>
    public static float Span(
        Skeleton skeleton,
        IReadOnlyList<Matrix4x4> boneWorlds,
        Matrix4x4 modelTransform,
        IReadOnlyList<bool>? include = null)
    {
        if (skeleton.BoneCount == 0) return 1f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (include is not null && (i >= include.Count || !include[i])) continue;
            var at = Origin(boneWorlds[i] * modelTransform);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }

        var size = max - min;
        var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        return longest > 0.0001f ? longest : 1f;
    }

    private static Vector3 Origin(Matrix4x4 m) => new(m.M41, m.M42, m.M43);

    // Row i of the row-vector matrix: this bone's local axis expressed in world space.
    private static Vector3 Row(Matrix4x4 m, int i)
    {
        var row = i switch
        {
            0 => new Vector3(m.M11, m.M12, m.M13),
            1 => new Vector3(m.M21, m.M22, m.M23),
            _ => new Vector3(m.M31, m.M32, m.M33),
        };
        return row.LengthSquared() > 1e-10f ? Vector3.Normalize(row) : Vector3.UnitY;
    }
}
