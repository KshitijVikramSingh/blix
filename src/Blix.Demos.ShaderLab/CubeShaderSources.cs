using Blix.Graphics;

internal static class CubeShaderSources
{
    // Shared file-system include resolver. Shaders/ directory acts as the include
    // root so a `#include "pbr_core.glsl"` from any shader looks up siblings.
    private static readonly string ShadersDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
    private static string ReadInclude(string name) => File.ReadAllText(Path.Combine(ShadersDir, name));

    private static ShaderSources Load(string vertName, string fragName)
    {
        var vertSource = GlslPreprocessor.Preprocess(
            File.ReadAllText(Path.Combine(ShadersDir, vertName)), ReadInclude);
        var fragSource = GlslPreprocessor.Preprocess(
            File.ReadAllText(Path.Combine(ShadersDir, fragName)), ReadInclude);
        return new ShaderSources(vertSource, fragSource, VertexName: vertName, FragmentName: fragName);
    }

    public static ShaderSources LoadShaders()       => Load("cube.vert", "cube.frag");
    public static ShaderSources LoadShadowShaders() => Load("shadow.vert", "shadow.frag");
    public static ShaderSources LoadFurShaders()    => Load("fur.vert", "fur.frag");
    public static ShaderSources LoadHologramShaders() => Load("hologram.vert", "hologram.frag");

    // Skinned content uses per-vertex tangents (vec4 at location 5) for normal
    // mapping, falling back to dFdx/dFdy synthesis when the imported glTF didn't
    // provide TANGENT. Both .frag files #include "pbr_core.glsl" for shared
    // PBR + lighting code.
    public static ShaderSources LoadSkinLitShaders() => Load("skin.lit.vert", "skin.lit.frag");

    // shadow.skin.vert pairs with shadow.frag (depth-only). Skinned shadow casters
    // share the same bone palette uniform as the scene pass, so skin deformations
    // cast accurate shadows.
    public static ShaderSources LoadShadowSkinShaders() => Load("shadow.skin.vert", "shadow.frag");
}
