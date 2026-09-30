using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Tools.Studio;

/// <summary>
/// The push-constant layout the stage's standard pipelines read, and the two calls that fill it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public because rung two needs it.</b> A tool bringing its own draw — a heightfield, a
/// navigation mesh, a gizmo — has to write the same bytes the stage's lit shader expects, and
/// making it re-derive that layout from a GLSL file would mean the first thing anyone does on this
/// stage is guess at a memory layout. The one that got this wrong once would look correct and shade
/// wrong.
/// </para>
/// <para>The layout is stated once, on <see cref="LitBytes"/>.</para>
/// </remarks>
public static class StudioPush
{
    /// <summary>Bytes a lit draw pushes: a model matrix and a material.</summary>
    /// <remarks>
    /// <c>mat4 model</c>, <c>vec4 (baseColour, baseAlpha)</c>, <c>vec4 (metallic, roughness, stride,
    /// alphaCutoff)</c>, <c>vec4 (albedoUvSet, normalScale, channelUvSets, _)</c>, <c>vec4 (emissive,
    /// occlusionStrength)</c>. Exactly the 128-byte floor every Vulkan implementation guarantees,
    /// which is why nothing on this stage needs a per-object descriptor set; a further material term
    /// needs a block of its own. Push constants belong to the draw, so a term a draw does not write
    /// is zero for that draw rather than whatever the previous draw left.
    /// </remarks>
    public const int LitBytes = 128;

    /// <summary>Bytes a shadow caster pushes: the model matrix, and what it takes to cut out.</summary>
    /// <remarks>
    /// <b>Sixty-four until a caster could discard.</b> "It shades nothing" was true and still is —
    /// this is not shading, it is whether the fragment exists. A cutout that the lit pass honours and
    /// the caster does not gives a leaf a solid rectangular shadow.
    /// </remarks>
    public const int CasterBytes = 80;

    /// <summary>Bytes a SKINNED caster pushes: a palette stride, with no model matrix at all.</summary>
    /// <remarks>
    /// The bones carry the placement, so a model matrix here would apply it twice. Still sixteen
    /// after the cutout arrived: the cutoff and the material's alpha ride components of the same
    /// vec4 that were already being pushed and ignored.
    /// </remarks>
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

    /// <summary>What a STATIC caster needs to cut out: the cutoff and the material's alpha.</summary>
    /// <remarks>
    /// Written at the vec4 straight after the matrix, which is where <c>studio_shadow.frag</c> reads
    /// it. Separate from <see cref="Material"/> because a caster's block is 80 bytes and that one
    /// writes 128 — calling it here would run off the end, which is the same edge the skinned
    /// caster's 16-byte buffer has always had.
    /// </remarks>
    public static void CasterCutout(byte[] target, float alphaCutoff, float baseAlpha, float albedoUvSet = 0f)
    {
        ArgumentNullException.ThrowIfNull(target);
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        if (floats.Length < 20) return;
        floats[16] = alphaCutoff; floats[17] = baseAlpha; floats[18] = albedoUvSet; floats[19] = 0f;
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
    /// </param>
    /// <param name="normalScale">
    /// How strongly the bound normal map bends the surface normal. <b>Zero ignores the map</b>, which
    /// is what a draw binding the white stand-in needs.
    /// </param>
    /// <param name="emissive">Linear radiance added after lighting, multiplied by the emissive texture.</param>
    /// <param name="occlusionStrength">
    /// How much the occlusion texture's red channel darkens ambient light. Zero ignores it.
    /// </param>
    /// <param name="normalUvSet">
    /// The TEXCOORD set the normal map samples; the next three name the metallic-roughness, occlusion and
    /// emissive maps'. Any set above 0 reads the stage's second set, the last one its layouts carry.
    /// Packed one bit per channel into a single float.
    /// </param>
    public static void Material(
        byte[] target, Vector3 baseColour, float metallic, float roughness, float stride = 0f,
        float alphaCutoff = 0f, float baseAlpha = 1f, float albedoUvSet = 0f,
        float normalScale = 0f, Vector3 emissive = default, float occlusionStrength = 0f,
        int normalUvSet = 0, int metallicRoughnessUvSet = 0, int occlusionUvSet = 0, int emissiveUvSet = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        if (floats.Length < 24) return;   // a caster's 64 bytes are the matrix and nothing else
        floats[16] = baseColour.X; floats[17] = baseColour.Y; floats[18] = baseColour.Z; floats[19] = baseAlpha;
        floats[20] = metallic; floats[21] = roughness; floats[22] = stride; floats[23] = alphaCutoff;
        if (floats.Length < 32) return;
        var channelUvSets = (normalUvSet > 0 ? 1 : 0) | (metallicRoughnessUvSet > 0 ? 2 : 0)
                            | (occlusionUvSet > 0 ? 4 : 0) | (emissiveUvSet > 0 ? 8 : 0);
        floats[24] = albedoUvSet; floats[25] = normalScale; floats[26] = channelUvSets; floats[27] = 0f;
        floats[28] = emissive.X; floats[29] = emissive.Y; floats[30] = emissive.Z; floats[31] = occlusionStrength;
    }

    /// <summary><see cref="Material"/> with every term a glTF surface carries beyond base colour.</summary>
    internal static void Material(
        byte[] target, Vector3 baseColour, float metallic, float roughness, in StudioSurface surface,
        float stride = 0f, float alphaCutoff = 0f, float baseAlpha = 1f, float albedoUvSet = 0f) =>
        Material(target, baseColour, metallic, roughness, stride, alphaCutoff, baseAlpha, albedoUvSet,
            surface.NormalScale, surface.Emissive, surface.OcclusionStrength,
            surface.NormalUvSet, surface.MetallicRoughnessUvSet, surface.OcclusionUvSet, surface.EmissiveUvSet);
}
