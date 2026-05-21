using Blix.Graphics;

internal static class FullscreenQuadShaderSources
{
    public static ShaderSources LoadPresentShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.frag")),
            VertexName: "present.vert",
            FragmentName: "present.frag");
    }

    public static ShaderSources LoadDepthPresentShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present-depth.frag")),
            VertexName: "present.vert",
            FragmentName: "present-depth.frag");
    }

    public static ShaderSources LoadShadowMapPresentShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present-shadowmap.frag")),
            VertexName: "present.vert",
            FragmentName: "present-shadowmap.frag");
    }

    public static ShaderSources LoadBrightShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "bright.frag")),
            VertexName: "present.vert",
            FragmentName: "bright.frag");
    }

    public static ShaderSources LoadBlurShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "blur.frag")),
            VertexName: "present.vert",
            FragmentName: "blur.frag");
    }

    public static ShaderSources LoadBloomCompositeShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "bloom-composite.frag")),
            VertexName: "present.vert",
            FragmentName: "bloom-composite.frag");
    }

    public static ShaderSources LoadSkyboxShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "skybox.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "skybox.frag")),
            VertexName: "skybox.vert",
            FragmentName: "skybox.frag");
    }

    public static ShaderSources LoadCopyShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "present.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "copy.frag")),
            VertexName: "present.vert",
            FragmentName: "copy.frag");
    }

    public static ShaderSources LoadGlassShaders()
    {
        return new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "glass.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "glass.frag")),
            VertexName: "glass.vert",
            FragmentName: "glass.frag");
    }
}
