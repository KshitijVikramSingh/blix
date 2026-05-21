using Blix.Geometry;
using Blix.Render;

namespace Blix;

// A GameObject that carries a skeleton, pose, and bone palette alongside its mesh +
// material, and hosts animations that drive the pose. The natural sibling to
// AnimatedGameObject and PhysicsGameObject: same composition-by-host pattern, just
// a richer per-frame Update — reset pose to rest, tick animations into it, compute
// the GPU palette.
//
// Per-frame `Update` runs the standard reset-sample-palette sequence:
//   1. Pose.CopyFrom(RestPose) — partial clips overlay onto a known base.
//   2. AnimationHost.Update(time) — every attached ClipAnimation samples its clip
//      into Pose. Animations that return false get removed (one-shot clips).
//   3. Skeleton.ComputeBonePalette(Pose, Palette) — GPU-ready matrices for the
//      vertex shader's `uBones` uniform.
//
// Render code in game/demo treats SkinnedGameObject like any GameObject for
// transform + material, then reads Palette.Matrices to bind the bone-palette
// uniform on top.
public sealed class SkinnedGameObject : GameObject, IUpdateable, IAnimated
{
    private readonly AnimationHost host = new();

    public SkinnedGameObject(
        string name,
        Submesh[] submeshes,
        Skeleton skeleton,
        Transform3D? transform = null,
        System.Numerics.Matrix4x4? meshNodeTransform = null)
        : base(
            name,
            ValidateSubmeshes(submeshes)[0].Mesh,
            submeshes[0].Material,
            transform)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Submeshes = submeshes;
        Skeleton = skeleton;
        RestPose = skeleton.CreateRestPose();
        Pose = skeleton.CreateRestPose();
        Palette = new BonePalette(skeleton.BoneCount);
        MeshNodeTransform = meshNodeTransform ?? System.Numerics.Matrix4x4.Identity;
        AggregateBounds = ComputeAggregateBounds(submeshes);
    }

    private static Blix.Geometry.Bounds3 ComputeAggregateBounds(Submesh[] submeshes)
    {
        var min = submeshes[0].Mesh.Bounds.Min;
        var max = submeshes[0].Mesh.Bounds.Max;
        for (var i = 1; i < submeshes.Length; i++)
        {
            min = System.Numerics.Vector3.Min(min, submeshes[i].Mesh.Bounds.Min);
            max = System.Numerics.Vector3.Max(max, submeshes[i].Mesh.Bounds.Max);
        }
        return new Blix.Geometry.Bounds3(min, max);
    }

    // Intrinsic asset transform (typically glTF's mesh-node ancestor chain) applied
    // between the asset's vertex space and the GameObject's Transform3D-driven
    // space. Render code composes: `uModel = Transform.ToMatrix() * MeshNodeTransform`.
    // Identity for hand-built skinned content (no asset-side orientation correction).
    public System.Numerics.Matrix4x4 MeshNodeTransform { get; }

    // Every drawable piece of this skinned object. All submeshes share one
    // skeleton + pose + palette — the per-frame Update computes the palette once;
    // the render loop binds it across every submesh's draw call.
    //
    // The base GameObject's Mesh + Material reflect Submeshes[0] for compatibility
    // with anything that reads them generically (debug bounds, scene-list code
    // that doesn't know about submeshes yet). Multi-submesh-aware render code
    // iterates Submeshes directly.
    public Submesh[] Submeshes { get; }

    // Union of every submesh's mesh-local AABB. Computed once at construction.
    // Useful for editor pick volumes / culling against the whole character
    // regardless of which submesh's bounds individually contain a point. The
    // base class's Mesh.Bounds is Submeshes[0]'s bounds only.
    //
    // Bounds are in mesh-local space (pre-MeshNodeTransform, pre-Transform).
    // Rest-pose only: doesn't reflect the current Pose's deformation. Accurate
    // posed bounds would need per-frame recomputation from Skeleton.Palette,
    // which lands when consumers (culling, hit-tests on the deformed character)
    // require it.
    public Blix.Geometry.Bounds3 AggregateBounds { get; }

    public Skeleton Skeleton { get; }

    // Cached at construction; serves as the base each frame copies into Pose before
    // animations run. Game code can mutate this if the rest pose needs to change
    // (e.g. swapping skeletons would mean a new SkinnedGameObject anyway).
    public Pose RestPose { get; }

    // The per-frame working pose. Animations write into it; the palette is computed
    // from it; outside code can read it (debug visualisation) but shouldn't mutate
    // it directly between AddAnimation calls or it'll get overwritten next Update.
    public Pose Pose { get; }

    // GPU-ready per-bone matrices, recomputed each Update from the current Pose.
    public BonePalette Palette { get; }

    public void AddAnimation(IAnimation animation) => host.AddAnimation(animation);

    public void Update(Time time)
    {
        Pose.CopyFrom(RestPose);
        host.Update(time);
        Skeleton.ComputeBonePalette(Pose, Palette);
    }

    private static Submesh[] ValidateSubmeshes(Submesh[] submeshes)
    {
        ArgumentNullException.ThrowIfNull(submeshes);
        if (submeshes.Length == 0)
        {
            throw new ArgumentException("SkinnedGameObject requires at least one submesh.", nameof(submeshes));
        }
        return submeshes;
    }
}
