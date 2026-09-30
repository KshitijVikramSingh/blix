using System.Numerics;

namespace Blix.Graphics;

/// <summary>
/// A material's own descriptor set: the uniforms and textures a program reads from
/// <see cref="DescriptorSets.Material"/>, written when the material changes rather than per draw.
/// </summary>
/// <remarks>
/// Made by <see cref="IGraphicsDevice.CreateMaterial"/> and drawn with through its
/// <see cref="Handle"/>. Uniforms are set by the member's name in the shader's block, as reflection
/// reports it, and a name the block does not have is an error rather than a silent no-op. Every setter
/// returns the material, so a description reads as one expression.
/// </remarks>
public interface IMaterialBindings
{
    /// <summary>What a draw names to use this material.</summary>
    MaterialHandle Handle { get; }

    /// <summary>The name it was created with, for diagnostics.</summary>
    string Name { get; }

    /// <summary>The descriptor set it binds, normally <see cref="DescriptorSets.Material"/>.</summary>
    int SetIndex { get; }

    /// <summary>How many copies it keeps, one per frame in flight when it is rewritten every frame.</summary>
    int FramesInFlight { get; }

    /// <summary>Sets one member of the uniform block at <paramref name="binding"/>.</summary>
    IMaterialBindings SetUniform(int binding, string memberName, float value);

    /// <inheritdoc cref="SetUniform(int, string, float)"/>
    IMaterialBindings SetUniform(int binding, string memberName, Vector2 value);

    /// <inheritdoc cref="SetUniform(int, string, float)"/>
    IMaterialBindings SetUniform(int binding, string memberName, Vector3 value);

    /// <inheritdoc cref="SetUniform(int, string, float)"/>
    IMaterialBindings SetUniform(int binding, string memberName, Vector4 value);

    /// <inheritdoc cref="SetUniform(int, string, float)"/>
    IMaterialBindings SetUniform(int binding, string memberName, Matrix4x4 value);

    /// <summary>Binds <paramref name="texture"/> at <paramref name="binding"/>.</summary>
    IMaterialBindings SetTexture(int binding, TextureHandle texture);

    /// <summary>Writes a whole buffer binding for one frame slot, for data rewritten every frame.</summary>
    IMaterialBindings WriteBuffer(int frameSlot, int binding, ReadOnlySpan<byte> payload);
}
