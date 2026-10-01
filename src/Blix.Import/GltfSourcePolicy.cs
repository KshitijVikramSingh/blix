using SharpGLTF.Schema2;

namespace Blix.Import;

/// <summary>
/// What a glTF source must not contain for Blix to read it as the file describes: one rule, for the cook and both
/// source importers, so no path accepts what another refuses.
/// </summary>
public static class GltfSourcePolicy
{
    /// <summary>
    /// Refuses morph targets that take effect. Blix does not deform by morph targets, so it draws a mesh's base; that
    /// is the file's shape exactly where every instance's effective weights are zero and nothing animates them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Effective weights, per instance.</b> glTF instantiates a mesh at a node with the node's <c>weights</c> when it
    /// has them, the mesh's otherwise, and zero when neither does. So a mesh whose own defaults are nonzero is still
    /// drawn as its base by every node that zeroes them, and a mesh no node places is drawn nowhere: neither is refused.
    /// </para>
    /// <para>
    /// <b>Any weights animation is refused,</b> even one whose keys happen to be zero: animated morphing is a dynamic
    /// semantic Blix does not have, and inspecting keys for a degenerate no-op is not worth the cleverness.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">An instance of a mesh with morph targets would not be its base.</exception>
    public static void RefuseEffectiveMorphTargets(ModelRoot model, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(model);
        static bool Morphs(Mesh? mesh) => mesh is not null && mesh.Primitives.Any(p => p.MorphTargetsCount > 0);

        foreach (var node in model.LogicalNodes.Where(n => Morphs(n.Mesh)))
        {
            var effective = node.MorphWeights is { Count: > 0 } own ? own : node.Mesh!.MorphWeights;
            if (effective is not null && effective.Any(w => w != 0f))
            {
                Refuse(node.Mesh!, $"node {node.LogicalIndex} ('{node.Name}') instantiates it with weights that are not all zero");
            }
        }

        foreach (var animation in model.LogicalAnimations)
        foreach (var channel in animation.Channels)
        {
            if (channel.TargetNodePath == PropertyPath.weights && Morphs(channel.TargetNode?.Mesh))
            {
                Refuse(channel.TargetNode!.Mesh!, $"animation '{animation.Name}' drives node {channel.TargetNode.LogicalIndex}'s weights");
            }
        }

        void Refuse(Mesh mesh, string why) => throw new InvalidDataException(
            $"'{sourcePath}' morph targets of mesh {mesh.LogicalIndex} ('{mesh.Name}') take effect ({why}), and Blix does not deform "
            + "by morph targets; drawing the base mesh would be a different shape than the file describes.");
    }
}
