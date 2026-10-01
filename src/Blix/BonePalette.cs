using System.Numerics;

namespace Blix;

// GPU-ready per-joint matrices, the output of SkinBinding.ComputePalette. Each
// `Matrices[j]` maps a bind-pose vertex through the current pose, ready for the
// vertex shader's `Σ weight × Matrices[joint] × vertex_bind` skinning sum, where a
// vertex's joint index is the skin's joint, not a bone of the skeleton.
//
// Exposed as a distinct type (rather than a raw Matrix4x4[]) so render code has a
// single, named, intent-clear consumer to upload into a shader uniform array.
// Pre-allocated by the caller and reused frame to frame to avoid per-frame GC
// pressure — the typical pattern is one BonePalette per skinned object, sized to
// its skin's joint count (SkinBinding.JointCount), which need not be its
// skeleton's bone count.
public sealed class BonePalette
{
    public Matrix4x4[] Matrices { get; }

    /// <summary>One matrix per skin joint.</summary>
    public int JointCount => Matrices.Length;

    public BonePalette(int jointCount)
    {
        if (jointCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jointCount), "Joint count must be non-negative.");
        }
        Matrices = new Matrix4x4[jointCount];
    }
}
