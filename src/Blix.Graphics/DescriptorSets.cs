namespace Blix.Graphics;

/// <summary>
/// Which descriptor set a shader resource lives in, by how long it lives: the convention every Blix
/// shader and the device share.
/// </summary>
/// <remarks>
/// A program's set 0 and 1 are written per draw from the uniforms and textures the draw supplies; set 2
/// belongs to a material (<see cref="IGraphicsDevice.CreateMaterial"/>) and is written when the
/// material is, not per draw. Named here rather than inside the device, because a shader author, a
/// library and the device all have to agree on it and none of them owns it alone.
/// </remarks>
public static class DescriptorSets
{
    /// <summary>Set 0: per frame — view-projection, sun, camera, ambient.</summary>
    public const int Frame = 0;

    /// <summary>Set 1: per pass — shadow maps, the environment, lookup tables.</summary>
    public const int Pass = 1;

    /// <summary>Set 2: per material — albedo, normal, metallic-roughness and their factors.</summary>
    public const int Material = 2;

    /// <summary>Set 3: per draw beyond push constants.</summary>
    public const int Draw = 3;
}
