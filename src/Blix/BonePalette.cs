using System.Numerics;

namespace Blix;

// GPU-ready per-bone matrices, the output of Skeleton.ComputeBonePalette. Each
// `Matrices[i]` maps a rest-pose vertex through the current pose, ready for the
// vertex shader's `Σ weight_j × Matrices[BoneIndex_j] × vertex_rest` skinning sum.
//
// Exposed as a distinct type (rather than a raw Matrix4x4[]) so render code has a
// single, named, intent-clear consumer to upload into a shader uniform array.
// Pre-allocated by the caller and reused frame to frame to avoid per-frame GC
// pressure — the typical pattern is one BonePalette per skinned object, sized to
// the skeleton's bone count.
public sealed class BonePalette
{
    public Matrix4x4[] Matrices { get; }

    public int BoneCount => Matrices.Length;

    public BonePalette(int boneCount)
    {
        if (boneCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boneCount), "Bone count must be non-negative.");
        }
        Matrices = new Matrix4x4[boneCount];
    }
}
