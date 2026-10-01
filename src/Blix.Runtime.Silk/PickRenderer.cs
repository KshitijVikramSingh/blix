using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Runtime.Silk;

/// <summary>
/// Answers "what is under the cursor" by drawing every selectable into the one pixel under it.
/// </summary>
/// <remarks>
/// <b>What Unity, Unreal and Blender do, for the reason they do it:</b> a box says where a thing might be,
/// not where its surface is. This draws the geometry itself, with an ID per selectable written into an
/// RGBA8 target, and reads back the pixel: the nearest surface there, exactly, with no box and no ray.
/// <para>
/// <b>One pixel, not a picture.</b> The target is 1x1, and each draw's matrix carries the pixel crop
/// (<see cref="ViewPicking.PixelCrop"/>), so the rasteriser samples only the cursor's pixel centre and
/// everything else is clipped for almost nothing. The cost is one vertex pass over what is drawn, once per
/// click, and the readback's queue wait, which is the trade readback already makes on purpose.
/// </para>
/// <para>
/// Made on the first pick, so an application that never picks pays nothing.
/// </para>
/// </remarks>
internal sealed class PickRenderer : IDisposable
{
    private const int PushBytes = 68;   // mat4 uMvp (vertex) + uint uId at offset 64 (fragment)
    private readonly IGraphicsDevice device;
    private readonly ShaderProgramHandle shader;
    private readonly RenderSurface target;
    private readonly Dictionary<(int Stride, int Offset), PipelineHandle> pipelines = new();

    public PickRenderer(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
        var shaderDir = AppFiles.Shaders;
        var pickInterface = ShaderReflection.ForProgram(shaderDir, "pick.vert", "pick.frag");
        shader = device.CreateShaderProgramFromSpv(
            File.ReadAllBytes(AppFiles.Shader("pick.vert.spv")),
            File.ReadAllBytes(AppFiles.Shader("pick.frag.spv")),
            pickInterface, "pick");
        target = device.CreateRenderSurface(new RenderSurfaceDescription(
            "pick",
            new FixedRenderSurfaceSize(1, 1),
            new[] { new ColorAttachmentDescription(TextureFormat.Rgba8, SamplerDescription.NearestClamp) },
            new DepthRenderbuffer()));
    }

    /// <summary>Records the pick pass: each candidate drawn with ID index + 1, nearest surface wins.</summary>
    public void Record(RenderCommandList commands, in ViewDeclaration view, Vector2 pixel, IReadOnlyList<DebugSelectable> candidates)
    {
        var cropped = view.ViewProjection * ViewPicking.PixelCrop(view, pixel);
        commands.Pass(
            "pick",
            new RenderPassDescription(
                Target: target.Handle,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0f, 0f, 0f, 0f) },
                ClearDepth: true),
            pass =>
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var g = candidates[i].Geometry;
                    var push = new byte[PushBytes];
                    var mvp = g.Model * cropped;
                    MemoryMarshal.Write(push.AsSpan(0, 64), in mvp);
                    var id = (uint)(i + 1);
                    MemoryMarshal.Write(push.AsSpan(64, 4), in id);
                    pass.DrawIndexed(
                        g.Vertices, g.Indices, PipelineFor(g, candidates[i].EntityPath), g.IndexCount,
                        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), push,
                        indexOffset: g.FirstIndex, vertexOffset: g.BaseVertex);
                }
            });
    }

    /// <summary>The candidate index under the pixel after the pass has run, or -1 for nothing.</summary>
    /// <remarks>Reads the 1x1 target back, which waits for the GPU: call it after the frame is submitted.</remarks>
    public int ReadHit()
    {
        var bytes = device.ReadTexture(target.ColorAttachments[0], out _, out _, out _);
        var id = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return id - 1;
    }

    // One pipeline per vertex stride and position offset: the pass reads the position and nothing else, so
    // every layout with the same two numbers draws through the same pipeline.
    private PipelineHandle PipelineFor(in DebugPickGeometry geometry, string path)
    {
        VertexAttribute? position = null;
        foreach (var attribute in geometry.Layout.Attributes)
        {
            if (attribute.Location == geometry.PositionLocation) position = attribute;
        }

        if (position is not { Format: VertexAttributeFormat.Float3 } p)
        {
            throw new InvalidOperationException(
                $"Selectable '{path}' offers geometry with no float3 position at location {geometry.PositionLocation}, " +
                "which is the one attribute the pick pass reads.");
        }

        var key = (geometry.Layout.Stride, p.Offset);
        if (pipelines.TryGetValue(key, out var existing)) return existing;
        var created = device.CreatePipeline(
            new PipelineDescription(
                shader,
                new VertexLayout(geometry.Layout.Stride, new[] { new VertexAttribute(0, VertexAttributeFormat.Float3, p.Offset) }),
                PrimitiveTopology.Triangles,
                new DepthState(Enabled: true, WriteEnabled: true, DepthCompare.Less),
                // Both faces: a click on the inside of a shell is still on it.
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled },
                RenderTarget: target.Handle),
            $"pick.stride{key.Stride}.pos{key.Offset}");
        pipelines[key] = created;
        return created;
    }

    public void Dispose()
    {
        foreach (var pipeline in pipelines.Values) device.DestroyPipeline(pipeline);
        device.DestroyShaderProgram(shader);
        device.DestroyRenderSurface(target.Handle);
    }
}
