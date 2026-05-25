using System.Numerics;
using Blix.Geometry;
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
    // When `cullFrustum` is non-null, submeshes whose WorldBounds is fully
    // outside that frustum are skipped. The frustum is typically built
    // from the camera's view-projection for the main pass; pass null to
    // disable culling (e.g. when a demo wants to keep today's
    // draw-everything behaviour). Returns the number of submeshes that
    // actually got submitted (useful for perf diagnostics).
    public int DrawScene(
        RenderPassBuilder pass,
        GltfSceneInstance scene,
        IReadOnlyList<ShaderUniform> sharedUniforms,
        Frustum? cullFrustum = null,
        float cullMargin = 0.0f,
        IOccluder? occluder = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sharedUniforms);

        var uniformArray = sharedUniforms as ShaderUniform[] ?? sharedUniforms.ToArray();
        int drawn = 0;
        for (var i = 0; i < scene.Submeshes.Count; i++)
        {
            var sub = scene.Submeshes[i];
            if (cullFrustum is { } f && !f.Intersects(sub.WorldBounds, cullMargin)) continue;

            // Stable identity per submesh — matches the convention used
            // by IDebugSelectable/IDebugInspectable so an occluder
            // implementation can share path state across diagnostic
            // surfaces if it wants.
            var entityPath = $"{scene.DebugName}/submesh-{i}";

            if (occluder is null || occluder.ShouldDraw(entityPath))
            {
                pass.DrawMesh(sub.Mesh, sub.Material,
                    perDrawUniforms: uniformArray, perDrawTextures: null);
                drawn++;
            }
            // Always issue the proxy query — the single source of
            // truth for "is this submesh visible?" Used to alternate
            // between "real-geometry-as-query" (when drawing) and
            // "proxy" (when skipping), but the two tests disagree for
            // loose AABBs (sparse foliage) and produced a visibility
            // strobe. Consistent proxy-only test = stable state.
            occluder?.RecordQuery(pass, entityPath, sub.WorldBounds);
        }
        return drawn;
    }

    // Draws every submesh as a shadow caster into the cascade's depth
    // surface. Caller has already opened the pass with the cascade's
    // depth-only RenderPassDescription.
    // Optional `cullFrustum` is the cascade's orthographic light frustum
    // -- callers should typically build it from cascadeLightVP itself to
    // skip submeshes outside the slice. This is the heaviest culling win
    // since each cascade only sees a slab of the scene. Returns the
    // number of submeshes submitted.
    public int DrawCascadeShadow(
        RenderPassBuilder pass,
        GltfSceneInstance scene,
        Matrix4x4 cascadeLightVP,
        Frustum? cullFrustum = null,
        float cullMargin = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(scene);

        int drawn = 0;
        foreach (var sub in scene.Submeshes)
        {
            if (cullFrustum is { } f && !f.Intersects(sub.WorldBounds, cullMargin)) continue;
            // Per-submesh uniforms: light VP + alpha-cutout state. Cutoff
            // is 0 for OPAQUE/BLEND so the shadow.frag discard is a no-op;
            // MASK foliage gets its alpha threshold so leaves cast leaf-
            // shape shadows instead of solid rectangles.
            var cutoff = sub.AlphaMode == Blix.GltfAlphaMode.Mask
                ? (sub.Source?.AlphaCutoff ?? 0.5f)
                : 0.0f;
            var baseFactor = sub.Source?.BaseColorFactor ?? new System.Numerics.Vector4(1.0f);
            var perDrawUniforms = new ShaderUniform[]
            {
                new("uLightViewProjection", new Matrix4x4Uniform(cascadeLightVP)),
                new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity)),
                new("uAlphaCutoff", new FloatUniform(cutoff)),
                new("uBaseColorFactor", new Vector4Uniform(baseFactor)),
            };
            var albedoBinding = FindAlbedoBinding(sub.Material);
            var perDrawTextures = albedoBinding is { } ab ? new[] { ab } : null;
            pass.DrawMesh(sub.Mesh, shadowMaterial,
                perDrawUniforms: perDrawUniforms, perDrawTextures: perDrawTextures);
            drawn++;
        }
        return drawn;
    }

    // Pull the lit material's uAlbedo binding out so the shadow + cube_shadow
    // shaders can do alpha-cutout discard on foliage / fabric. Returns null
    // when the material has no albedo texture (rare; light-bulb / glass).
    private static ShaderTextureBinding? FindAlbedoBinding(Material material)
    {
        for (var i = 0; i < material.Textures.Count; i++)
        {
            if (material.Textures[i].Name == "uAlbedo") return material.Textures[i];
        }
        return null;
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

        foreach (var sub in scene.Submeshes)
        {
            var cutoff = sub.AlphaMode == Blix.GltfAlphaMode.Mask
                ? (sub.Source?.AlphaCutoff ?? 0.5f)
                : 0.0f;
            var baseFactor = sub.Source?.BaseColorFactor ?? new System.Numerics.Vector4(1.0f);
            var perDrawUniforms = new ShaderUniform[]
            {
                new("uLightViewProjection", new Matrix4x4Uniform(cubeFaceVP)),
                new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity)),
                new("uPointLightPosition", new Vector3Uniform(lightPosition)),
                new("uPointLightFarPlane", new FloatUniform(lightRange)),
                new("uAlphaCutoff", new FloatUniform(cutoff)),
                new("uBaseColorFactor", new Vector4Uniform(baseFactor)),
            };
            var albedoBinding = FindAlbedoBinding(sub.Material);
            var perDrawTextures = albedoBinding is { } ab ? new[] { ab } : null;
            pass.DrawMesh(sub.Mesh, cubeShadowMaterial,
                perDrawUniforms: perDrawUniforms, perDrawTextures: perDrawTextures);
        }
    }
}
