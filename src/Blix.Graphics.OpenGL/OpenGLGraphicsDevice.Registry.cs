using Blix.Graphics;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    public ResourceRegistrySnapshot SnapshotResources()
    {
        ThrowIfDisposed();

        var vertexBufferEntries = new List<VertexBufferEntry>(vertexBuffers.Count);

        foreach (var (id, resource) in vertexBuffers)
        {
            vertexBufferEntries.Add(new VertexBufferEntry(
                new VertexBufferHandle(id),
                resource.Name,
                resource.Count,
                resource.Stride));
        }

        var indexBufferEntries = new List<IndexBufferEntry>(indexBuffers.Count);

        foreach (var (id, resource) in indexBuffers)
        {
            indexBufferEntries.Add(new IndexBufferEntry(
                new IndexBufferHandle(id),
                resource.Name,
                resource.Count));
        }

        var textureEntries = new List<TextureEntry>(textures.Count);

        foreach (var (id, resource) in textures)
        {
            textureEntries.Add(new TextureEntry(
                new TextureHandle(id),
                resource.Name,
                resource.Width,
                resource.Height,
                resource.Format,
                resource.Kind));
        }

        var shaderProgramEntries = new List<ShaderProgramEntry>(shaderPrograms.Count);

        foreach (var (id, resource) in shaderPrograms)
        {
            shaderProgramEntries.Add(new ShaderProgramEntry(
                new ShaderProgramHandle(id),
                resource.Name));
        }

        var pipelineEntries = new List<PipelineEntry>(pipelines.Count);

        foreach (var (id, resource) in pipelines)
        {
            pipelineEntries.Add(new PipelineEntry(
                new PipelineHandle(id),
                resource.Name,
                resource.ShaderProgram,
                resource.Topology));
        }

        var renderSurfaceEntries = new List<RenderSurfaceEntry>(renderSurfaces.Count);

        foreach (var (id, resource) in renderSurfaces)
        {
            var depthHandle = (resource.Depth as DepthTextureResource)?.Handle;
            renderSurfaceEntries.Add(new RenderSurfaceEntry(
                new RenderSurfaceHandle(id),
                resource.Description.Name,
                resource.Width,
                resource.Height,
                resource.ColorTextures,
                depthHandle));
        }

        return new ResourceRegistrySnapshot(
            vertexBufferEntries,
            indexBufferEntries,
            textureEntries,
            shaderProgramEntries,
            pipelineEntries,
            renderSurfaceEntries);
    }
}
