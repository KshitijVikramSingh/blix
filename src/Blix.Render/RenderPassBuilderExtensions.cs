using Blix.Graphics;

namespace Blix.Render;

public static class RenderPassBuilderExtensions
{
    public static void DrawMesh(this RenderPassBuilder pass, Mesh mesh, Material material)
    {
        DrawMesh(pass, mesh, material, perDrawUniforms: null, perDrawTextures: null);
    }

    public static void DrawMesh(
        this RenderPassBuilder pass,
        Mesh mesh,
        Material material,
        IReadOnlyList<ShaderUniform>? perDrawUniforms,
        IReadOnlyList<ShaderTextureBinding>? perDrawTextures = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);

        var uniforms = Merge(material.Uniforms, perDrawUniforms);
        var textures = Merge(material.Textures, perDrawTextures);

        pass.DrawIndexed(
            mesh.VertexBuffer,
            mesh.IndexBuffer,
            material.Pipeline,
            mesh.IndexCount,
            uniforms,
            textures);
    }

    private static IReadOnlyList<T> Merge<T>(IReadOnlyList<T> baseline, IReadOnlyList<T>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return baseline;
        }

        if (baseline.Count == 0)
        {
            return overrides;
        }

        var merged = new List<T>(baseline.Count + overrides.Count);
        merged.AddRange(baseline);
        merged.AddRange(overrides);
        return merged;
    }
}
