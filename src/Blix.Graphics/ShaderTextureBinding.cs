namespace Blix.Graphics;

// One texture bound to a shader's sampler, by name or at a Slot.
//
// By name is the usual form: new ShaderTextureBinding("uHdr", texture) finds uHdr's binding in the
// program's reflection, and a name the program does not have is an error that lists the names it
// does. An element of an array binding is named with its index, "uCascades[2]". A Slot is the
// binding number, for a caller that has one; when a Slot and a name are both given and reflection
// names that binding differently, binding it is an error, because one of the two is wrong.
//
// ArrayIndex selects the element for array bindings given by Slot, and is 0 for a single texture.
public sealed record ShaderTextureBinding(string Name, TextureHandle Texture, int Slot, int ArrayIndex = 0)
{
    /// <summary>The Slot of a binding resolved from its name.</summary>
    public const int ByName = -1;

    /// <summary>Bind <paramref name="Texture"/> to the sampler the shader calls <paramref name="Name"/>.</summary>
    public ShaderTextureBinding(string Name, TextureHandle Texture) : this(Name, Texture, ByName) { }
}
