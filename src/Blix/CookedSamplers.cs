using Blix.Assets;
using Blix.Graphics;

namespace Blix;

/// <summary>How the engine samples a cooked image row: glTF's sampler codes, mapped.</summary>
public static class CookedSamplers
{
    /// <summary>A glTF sampler as the engine samples it; null for the spec's defaults (the loader's own).</summary>
    /// <remarks>
    /// glTF leaves the filters to the implementation when unspecified, and Blix's answer is trilinear:
    /// a row that says nothing keeps that, and the loader's shared default texture. NEAREST and LINEAR
    /// as min filters name no mipmap mode, so they sample the base level only (TextureMipFilter.None).
    /// </remarks>
    public static SamplerDescription? ToSamplerDescription(this BlixMeshSampler sampler)
    {
        if (sampler is { WrapS: 0 or 10497, WrapT: 0 or 10497, MinFilter: 0, MagFilter: 0 }) return null;

        static TextureWrap Wrap(int code) => code switch
        {
            33071 => TextureWrap.ClampToEdge,
            33648 => TextureWrap.MirroredRepeat,
            _ => TextureWrap.Repeat,
        };

        var (min, mip) = sampler.MinFilter switch
        {
            9728 => (TextureFilter.Nearest, TextureMipFilter.None),
            9729 => (TextureFilter.Linear, TextureMipFilter.None),
            9984 => (TextureFilter.Nearest, TextureMipFilter.Nearest),
            9985 => (TextureFilter.Linear, TextureMipFilter.Nearest),
            9986 => (TextureFilter.Nearest, TextureMipFilter.Linear),
            _ => (TextureFilter.Linear, TextureMipFilter.Linear),
        };
        var mag = sampler.MagFilter == 9728 ? TextureFilter.Nearest : TextureFilter.Linear;
        return new SamplerDescription(min, mag, Wrap(sampler.WrapS), Wrap(sampler.WrapT), GenerateMipmaps: false, MipFilter: mip);
    }
}
