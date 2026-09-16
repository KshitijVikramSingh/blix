using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Tools.Studio;

/// <summary>
/// The push-constant layout the stage's standard pipelines read, and the two calls that fill it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public because rung two needs it.</b> A tool bringing its own draw — a heightfield, a
/// navigation mesh, a gizmo — has to write the same 96 bytes the stage's lit shader expects, and
/// making it re-derive that layout from a GLSL file would mean the first thing anyone does on this
/// stage is guess at a memory layout. The one that got this wrong once would look correct and shade
/// wrong.
/// </para>
/// <para>
/// <c>mat4 model</c>, <c>vec4 baseColour</c>, <c>vec4 (metallic, roughness, stride, _)</c> — 96
/// bytes, inside the 128-byte floor every Vulkan implementation guarantees, which is why nothing on
/// this stage needs a per-object descriptor set.
/// </para>
/// </remarks>
public static class StudioPush
{
    /// <summary>Bytes a lit draw pushes: a model matrix and a material.</summary>
    public const int LitBytes = 96;

    /// <summary>Bytes a shadow caster pushes: the model matrix alone. It shades nothing.</summary>
    public const int CasterBytes = 64;

    /// <summary>Bytes a SKINNED caster pushes: a palette stride, with no model matrix at all.</summary>
    /// <remarks>The bones carry the placement, so a model matrix here would apply it twice.</remarks>
    public const int SkinnedCasterBytes = 16;

    /// <summary>Writes a model matrix into the first 64 bytes.</summary>
    public static void Matrix(in Matrix4x4 m, byte[] target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
        floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
        floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
        floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;
    }

    /// <summary>Writes the material block after the matrix. A no-op on a caster-sized buffer.</summary>
    /// <param name="stride">
    /// Bones per instance, which the skinned vertex stage reads out of a material slot — see
    /// studio_skinned.vert for why it rides there rather than in a block of its own.
    /// </param>
    /// <param name="alphaCutoff">
    /// Discard the fragment when its alpha falls below this. <b>Zero means never</b>, which is what
    /// makes OPAQUE the default without a second pipeline: a cutout is a comparison the fragment
    /// stage can make, so MASK needs a number rather than a pipeline variant. BLEND does need one,
    /// because blending is pipeline state.
    /// </param>
    /// <param name="baseAlpha">
    /// The material's <c>baseColorFactor.a</c>, multiplied into the sampled alpha before the test.
    /// It was hardcoded to 1 here, which silently ignored every material that dimmed its own alpha —
    /// the generator's AlphaMask_05 is exactly that case and looked identical to AlphaMask_01.
    /// </param>
    public static void Material(
        byte[] target, Vector3 baseColour, float metallic, float roughness, float stride = 0f,
        float alphaCutoff = 0f, float baseAlpha = 1f)
    {
        ArgumentNullException.ThrowIfNull(target);
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        if (floats.Length < 24) return;   // a caster's 64 bytes are the matrix and nothing else
        floats[16] = baseColour.X; floats[17] = baseColour.Y; floats[18] = baseColour.Z; floats[19] = baseAlpha;
        floats[20] = metallic; floats[21] = roughness; floats[22] = stride; floats[23] = alphaCutoff;
    }
}
