namespace Blix.Graphics;

/// <summary>Optional device features an application can depend on, and the device enabled.</summary>
/// <remarks>
/// A feature listed here is one the device supports and turned on at creation. An application that relies
/// on one checks <see cref="IGraphicsDevice.Features"/> and refuses by name when it is absent, rather than
/// recording commands that are invalid without it: validation cannot always see the difference (it cannot
/// read an indirect buffer's contents), so the device being silent is not the same as it being fine.
/// </remarks>
[Flags]
public enum GraphicsFeatures
{
    None = 0,

    /// <summary>One indirect draw command issuing several sub-draws (multiDrawIndirect).</summary>
    MultiDrawIndirect = 1 << 0,

    /// <summary>Indirect draw records whose firstInstance is not zero (drawIndirectFirstInstance).</summary>
    DrawIndirectFirstInstance = 1 << 1,

    /// <summary>Fragment shaders that write storage images or buffers (fragmentStoresAndAtomics).</summary>
    FragmentStoresAndAtomics = 1 << 2,
}
