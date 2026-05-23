using System.Numerics;
using Blix.Graphics;
using Blix.Render;

namespace Blix;

// PbrSceneRenderer is the engine-level "render a glTF scene through the PBR
// pipeline" helper. It does three things and nothing more:
//
//  1. Packs the shared per-frame uniforms for the lit shader from a
//     PbrFrameContext (camera, sun, IBL probe, cascades, point lights,
//     exposure, plus any demo-supplied ExtraUniforms).
//  2. Iterates a GltfSceneInstance's submeshes inside an already-open
//     opaque pass, issuing DrawMesh per submesh.
//  3. Same for shadow passes -- one cascade VP or one cube-face VP per
//     open shadow pass.
//
// Deliberate non-goals:
// - Does NOT open render passes. The demo owns the pass orchestration
//   (which surface, clear behaviour, what other materials draw inside).
//   The demo also handles non-opaque draws (sky, flames, volumes) inside
//   its opaque pass since they share the framebuffer + depth state.
// - Does NOT own pipelines or shaders. The demo creates the lit /
//   cascade-shadow / cube-shadow pipelines (so its shader fragments can
//   compose Blix.Shaders/lib/* however the demo wants) and hands the
//   resulting Materials to the renderer.
// - Does NOT enforce uniform names. The lit shader's expected uniforms
//   are a convention (uSunDirection, uCascadeLightVPs, etc.); a future
//   uniform-block scheme would tighten this.
public sealed class PbrSceneRenderer
{
    private readonly string namePrefix;
    private readonly Material shadowMaterial;
    private readonly Material cubeShadowMaterial;

    public PbrSceneRenderer(
        string namePrefix,
        Material shadowMaterial,
        Material cubeShadowMaterial)
    {
        ArgumentNullException.ThrowIfNull(namePrefix);
        ArgumentNullException.ThrowIfNull(shadowMaterial);
        ArgumentNullException.ThrowIfNull(cubeShadowMaterial);
        this.namePrefix = namePrefix;
        this.shadowMaterial = shadowMaterial;
        this.cubeShadowMaterial = cubeShadowMaterial;
    }

    public string NamePrefix => namePrefix;

    // Packs the physics-input uniforms + demo ExtraUniforms into a single
    // array the lit shader receives. Demos build this once per frame and
    // pass the same array to every submesh draw inside the opaque pass.
    public ShaderUniform[] PackSceneUniforms(PbrFrameContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var uniforms = new List<ShaderUniform>(32)
        {
            new("uView", new Matrix4x4Uniform(ctx.View)),
            new("uProjection", new Matrix4x4Uniform(ctx.Projection)),
            new("uCameraPosition", new Vector3Uniform(ctx.CameraPosition)),
            new("uSunDirection", new Vector3Uniform(ctx.SunDirection)),
            new("uSunColor", new Vector3Uniform(ctx.SunColor)),
            new("uEnvMapMipCount", new FloatUniform((float)ctx.Environment.EnvCubeMipCount)),
            new("uSpecularPrefilterMipCount", new FloatUniform((float)ctx.Environment.PrefilteredSpecularMipCount)),
            new("uExposure", new FloatUniform(ctx.Exposure)),
            // Sponza-and-friends bake world transforms into vertex positions,
            // so uModel + uNormalMatrix are identity by default. Demos that
            // need per-mesh transforms override these via ExtraUniforms or
            // material per-draw uniforms.
            new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity)),
            new("uNormalMatrix", new Matrix4x4Uniform(Matrix4x4.Identity)),
        };

        if (ctx.Cascades is { } cascades)
        {
            uniforms.Add(new("uCascadeLightVPs", new Matrix4x4ArrayUniform(cascades.LightViewProjections)));
            uniforms.Add(new("uCascadeSplits", new FloatArrayUniform(cascades.Splits)));
            uniforms.Add(new("uVisualizeCascades", new FloatUniform(cascades.Visualize ? 1.0f : 0.0f)));
        }

        if (ctx.PointLights is { } pls)
        {
            uniforms.Add(new("uPointLightPositions", new Vector3ArrayUniform(pls.Positions)));
            uniforms.Add(new("uPointLightColors", new Vector3ArrayUniform(pls.Colors)));
            uniforms.Add(new("uPointLightRanges", new FloatArrayUniform(pls.Ranges)));
            uniforms.Add(new("uPointLightCount", new FloatUniform(pls.ActiveCount)));
        }

        if (ctx.ExtraUniforms is { Count: > 0 } extra)
        {
            uniforms.AddRange(extra);
        }

        return uniforms.ToArray();
    }

    // Draws every submesh in the scene with its material + the supplied
    // shared uniforms. Must be called inside an open RenderPassBuilder
    // whose target is the HDR scene surface (or compatible). No clear
    // happens here -- the demo's RenderPassDescription configures that.
    public void DrawScene(
        RenderPassBuilder pass,
        GltfSceneInstance scene,
        IReadOnlyList<ShaderUniform> sharedUniforms)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sharedUniforms);

        var uniformArray = sharedUniforms as ShaderUniform[] ?? sharedUniforms.ToArray();
        foreach (var sub in scene.Submeshes)
        {
            pass.DrawMesh(sub.Mesh, sub.Material,
                perDrawUniforms: uniformArray, perDrawTextures: null);
        }
    }

    // Draws every submesh as a shadow caster into the cascade's depth
    // surface. Caller has already opened the pass with the cascade's
    // depth-only RenderPassDescription.
    public void DrawCascadeShadow(
        RenderPassBuilder pass,
        GltfSceneInstance scene,
        Matrix4x4 cascadeLightVP)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(scene);

        var uniforms = new ShaderUniform[]
        {
            new("uLightViewProjection", new Matrix4x4Uniform(cascadeLightVP)),
            new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity)),
        };
        foreach (var sub in scene.Submeshes)
        {
            pass.DrawMesh(sub.Mesh, shadowMaterial,
                perDrawUniforms: uniforms, perDrawTextures: null);
        }
    }

    // Draws every submesh as a cube-shadow caster for one cube face. Caller
    // has opened the pass with the appropriate DepthCubeFace attachment.
    public void DrawCubeShadowFace(
        RenderPassBuilder pass,
        GltfSceneInstance scene,
        Matrix4x4 cubeFaceVP,
        Vector3 lightPosition,
        float lightRange)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(scene);

        var uniforms = new ShaderUniform[]
        {
            new("uLightViewProjection", new Matrix4x4Uniform(cubeFaceVP)),
            new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity)),
            new("uPointLightPosition", new Vector3Uniform(lightPosition)),
            new("uPointLightFarPlane", new FloatUniform(lightRange)),
        };
        foreach (var sub in scene.Submeshes)
        {
            pass.DrawMesh(sub.Mesh, cubeShadowMaterial,
                perDrawUniforms: uniforms, perDrawTextures: null);
        }
    }
}
