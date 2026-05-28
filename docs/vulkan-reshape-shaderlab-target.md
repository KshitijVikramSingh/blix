# Vulkan reshape — target API via ShaderLab call sites

Use-case-first design. This doc is what we WANT the new engine API to
look like when ShaderLab is ported to it. Code blocks are illustrative
C# — they don't compile, and that's intentional. The goal is to feel
where the abstractions belong before any implementation.

Each section ends with **what this reveals** notes that map back to
friction-vector decisions or surface new design questions.

ShaderLab was chosen as the target because it stresses **shape diversity**
— many distinct shaders (lit, glass, fur, hologram, skybox, sprite,
text, post-process chain), multiple light types, skinned + static meshes,
multi-pass dependencies (glass reads scene copy, bloom chains across
levels), and picking. Sponza is mostly one shader × N materials — that
volume test comes later, after the shape is settled.

---

## Survey: what does ShaderLab need from the engine?

Distilled from `src/Blix.Demos.ShaderLab/Program.cs`. Each entry is a
binding shape the new API has to handle without contortion.

**Render passes (in execution order):**

| Pass | Targets | Reads | Topology | Notes |
|---|---|---|---|---|
| `shadow` | sun depth (2048²) | — | tris (skin + static) | back-face cull, alpha-cutout |
| `shadow.spot.i` × 4 | spot depth (1024²) | — | tris | per-caster, only if shadow-casting |
| `shadow.point.i.face.j` × 6×2 | point cube depth (512²) | — | tris | per-face per-caster, incremental rebake |
| `scene` | HDR + lum + normals (MRT), depth | sun/spot/point shadows, env cube | tris | skybox + opaque lit + skinned lit |
| `fur` | HDR scene buffer | (writes only) | tris × N shells | additive shells; 1 draw per shell per object |
| `hologram` | HDR scene buffer | (writes only) | tris | rim + scanlines blend |
| `scene.copy` | HDR snapshot | HDR scene buffer | fullscreen | snapshot for glass refraction |
| `glass` | HDR scene buffer | scene.copy, env cube | tris | refraction + reflection |
| `bloom.bright[i]` × 3 | bright mip i | scene buffer | fullscreen | half-res per level |
| `bloom.blurH[i]` × 3 | bright mip i (write) | bright mip i (read) | fullscreen | separable Gaussian H |
| `bloom.blurV[i]` × 3 | bright mip i | bright mip i | fullscreen | separable Gaussian V (ping-pong) |
| `present` | swapchain | scene buffer, bloom mips | fullscreen | tonemap + composite + gamma |
| `debug` | swapchain | — | lines | DebugDraw overlay |
| `hud` | swapchain | font atlas | tris (sprite) | text + UI |

**Shader programs (~15 distinct):**

`lit`, `skin.lit`, `shadow`, `skin.shadow`, `skin.white_fallback`,
`skybox`, `bright`, `blur`, `bloom.composite`, `copy`, `glass`, `fur`,
`hologram`, `present`, `present.depth`, `present.shadowmap`.

**Vertex layouts:**

- `VertexPosition3NormalTexture` — static lit (cubes, primitives)
- `VertexPosition3NormalTextureSkin4Tangent` — skinned glTF
- `VertexPosition3TextureColor` — sprite + text (HUD)
- `VertexPosition3Color` — debug lines

**Lifetime-classified binding inventory** for the lit shader (the worst
offender today; ~17 uniforms + ~10 textures bound at draw time):

- **Per-frame:** `viewProjection`, sun direction, sun intensity, env mip count, ambient boost, camera position
- **Per-pass:** sun shadow VP, sun shadow map, env cube, BRDF LUT (split-sum), spot shadow VPs[4], spot shadow maps[4], point shadow VPs[2], point shadow cubes[2]
- **Per-material:** albedo + normal + MR maps, base-color factor, metallic factor, roughness factor, normal-map scale
- **Per-draw:** `uModel`, `uNormalMatrix`, optionally bone palette (skinned-only)

Bone palette is currently `Matrix4x4ArrayUniform(rig.Palette.Matrices)` —
40+ matrices = 40 × 16 bytes × std140 stride padding overhead. Pushing
this into an SSBO is the F-009 disposition.

---

## Section 1 — ShaderInterface declarations

Each shader program ships with a typed interface declaring its descriptor
sets, bindings, and push-constant layout. Cooked offline from SPIR-V
reflection (or hand-declared; both produce the same data).

```csharp
// Lit (static) — the full PBR shader with shadows, env, MR maps.
public static class LitShader
{
    public static readonly ShaderInterface Interface = new(
        Slots: [
            // set 0: per-frame
            new(Set: 0, Binding: 0, Type: UniformBuffer, Stages: Vertex|Fragment, BlockLayout: new(
                Size: 96,
                Members: [
                    new("uViewProjection", Offset: 0,  Size: 64),
                    new("uSunDirection",   Offset: 64, Size: 12),
                    new("uSunIntensity",   Offset: 76, Size: 4),
                    new("uCameraPosition", Offset: 80, Size: 12),
                    new("uAmbientBoost",   Offset: 92, Size: 4),
                ])),

            // set 1: per-pass — sun shadow + env + spot + point arrays
            new(Set: 1, Binding: 0, Type: UniformBuffer, Stages: Vertex|Fragment, BlockLayout: new(
                Size: 256,
                Members: [
                    new("uSunShadowVP",    Offset: 0,   Size: 64),
                    new("uEnvMipCount",    Offset: 64,  Size: 4),
                    new("uSpotVPs",        Offset: 80,  Size: 64 * 4, ElementStride: 64),
                    new("uPointFarPlanes", Offset: 80 + 256, Size: 4 * 2, ElementStride: 16),
                ])),
            new(Set: 1, Binding: 1, Type: SampledImage, Stages: Fragment), // uShadowMap (sun)
            new(Set: 1, Binding: 2, Type: SampledImage, Stages: Fragment), // uEnvMap (cube)
            new(Set: 1, Binding: 3, Type: SampledImage, Stages: Fragment), // uBrdfLut
            new(Set: 1, Binding: 4, Type: SampledImage, Stages: Fragment, Count: 4),  // uSpotShadowMaps[4]
            new(Set: 1, Binding: 5, Type: SampledImage, Stages: Fragment, Count: 2),  // uPointShadowCubes[2]

            // set 2: per-material
            new(Set: 2, Binding: 0, Type: UniformBuffer, Stages: Fragment, BlockLayout: new(
                Size: 32,
                Members: [
                    new("uBaseColorFactor", Offset: 0,  Size: 16),
                    new("uMetallicFactor",  Offset: 16, Size: 4),
                    new("uRoughnessFactor", Offset: 20, Size: 4),
                    new("uNormalScale",     Offset: 24, Size: 4),
                    new("uAlphaCutoff",     Offset: 28, Size: 4),
                ])),
            new(Set: 2, Binding: 1, Type: SampledImage, Stages: Fragment), // uAlbedoMap
            new(Set: 2, Binding: 2, Type: SampledImage, Stages: Fragment), // uNormalMap
            new(Set: 2, Binding: 3, Type: SampledImage, Stages: Fragment), // uMetallicRoughnessMap

            // set 3: per-draw  → push constants (no descriptor set)
        ],
        PushConstants: [
            new(Stages: Vertex, Offset: 0,  Size: 64),  // uModel
            new(Stages: Vertex, Offset: 64, Size: 64),  // uNormalMatrix
        ],
        VertexInput: VertexPosition3NormalTexture.Layout);
}
```

**What this reveals:**
- **F-002 disposition holds:** every binding is `(set, binding)` explicit. Names live inside `UniformBlockLayout` for member access, never for descriptor lookup.
- **F-007 disposition (descriptor half resolved):** the per-draw transient descriptor pool (`VulkanGraphicsDevice.TransientDescriptors.cs`) gives every draw its own descriptor set, so the same program can be safely reused across draws with different texture bindings (bloom blur H+V share one program; the duplicated `bloomBlurProgramV` is gone). Model + normal matrix continue to ride push constants. UBO-byte storage is still per-program-per-frame and remains the next escalation.
- **F-009 stress test:** the `uSpotVPs` array is in-UBO with `ElementStride: 64` — sizeof(mat4). That's clean. Bone palette would NOT fit (50+ mat4 = 3200 bytes > UBO max), so skin.lit declares it as an SSBO (see Section 2).
- **NEW question Q-001:** how granular is the per-material set? Today materials carry their own UBO + 3 textures. Some shaders (skybox, hologram) have no textures — does set 2 just stay empty in their interface? **Tentative answer:** yes, empty per-material set is fine; the runtime skips bind when count is 0.

```csharp
// Skinned lit — same shader interface as Lit, except set 3 carries a bone-palette SSBO.
public static class SkinLitShader
{
    public static readonly ShaderInterface Interface = LitShader.Interface with {
        Slots: [..LitShader.Interface.Slots,
            // set 3: per-draw bone palette (too large for push constants).
            new(Set: 3, Binding: 0, Type: StorageBuffer, Stages: Vertex, BlockLayout: new(
                Size: SkeletonRig.MaxBones * 64,
                Members: [ new("uBones", Offset: 0, Size: SkeletonRig.MaxBones * 64, ElementStride: 64) ])),
        ],
        // Push constants stay the same — uModel + uNormalMatrix.
        VertexInput: VertexPosition3NormalTextureSkin4Tangent.Layout);
}
```

**What this reveals:**
- **F-009 disposition firms up:** bone palettes go to SSBO. The descriptor type changes (StorageBuffer not UniformBuffer); the binding shape itself is the same. The shader does `layout(set=3, binding=0) readonly buffer Bones { mat4 m[]; } bones;`.
- **NEW question Q-002:** SSBOs aren't required to be supported at all stages on every device. We need to require `VK_PHYSICAL_DEVICE_FEATURE_SHADER_STORAGE_BUFFER_*` at device-create time. On MoltenVK this is fine; on minimal Vulkan profiles it may not be. Worth detecting + erroring early.
- **Q-003 — SETTLED.** Set 3 is a *lifetime tier* (per-draw), not a *resource type*. Push constants serve the ≤256B case; for larger payloads (bone palette SSBO) the program declares a slot at set 3 and the demo allocates a `MaterialBindings` with `framesInFlight = MaxFramesInFlight`. The bone palette already rides this path in the lit demo. Per-draw transient descriptor allocation for set 0/1 is also live (see F-007 resolution); set 3's MaterialBindings stays per-material-instance because the payload is large enough that recycling sets across draws is the right call.

```csharp
// Glass — no shadows, no MR maps, just env + scene-copy + a few floats.
public static class GlassShader
{
    public static readonly ShaderInterface Interface = new(
        Slots: [
            new(Set: 0, Binding: 0, Type: UniformBuffer, Stages: Vertex|Fragment, BlockLayout: new(
                Size: 80,
                Members: [
                    new("uViewProjection",  Offset: 0,  Size: 64),
                    new("uCameraPosition",  Offset: 64, Size: 12),
                    new("uTime",            Offset: 76, Size: 4),
                ])),
            new(Set: 1, Binding: 1, Type: SampledImage, Stages: Fragment), // uSceneCopy
            new(Set: 1, Binding: 2, Type: SampledImage, Stages: Fragment), // uEnvMap
            new(Set: 2, Binding: 0, Type: UniformBuffer, Stages: Fragment, BlockLayout: new(
                Size: 32,
                Members: [
                    new("uTint",       Offset: 0,  Size: 12),
                    new("uF0",         Offset: 16, Size: 4),
                    new("uThickness",  Offset: 20, Size: 4),
                    new("uRoughness",  Offset: 24, Size: 4),
                ])),
        ],
        PushConstants: [
            new(Stages: Vertex, Offset: 0, Size: 64),  // uModel
        ],
        VertexInput: VertexPosition3NormalTexture.Layout);
}
```

**What this reveals:**
- Glass demonstrates a sparser interface — set 1 reuses bindings 1 + 2 from Lit (env map slot) but skips bindings 0 (no big per-pass UBO needed) and 3/4/5 (no shadows). That's fine: missing binding indices in a shader interface just means "this shader doesn't sample that slot."
- **NEW question Q-004:** if Lit and Glass run in the SAME pass and Glass's set 1 layout differs from Lit's, the runtime needs to rebind set 1 between them. Do we share a single per-pass descriptor set across all draws (and require all shaders in the pass to be compatible), or rebind per pipeline? **Tentative answer:** rebind per pipeline. The runtime caches the per-pass set per pipeline-layout shape and rebinds on pipeline change.

---

## Section 2 — Materials (set 2 carrier only)

`Material` becomes a thin wrapper around set 2's UBO + samplers. No
names, no slots — explicit binding indices.

```csharp
// Loaded from MaterialImporter (cooked JSON → MaterialData → Material).
var marbleMaterial = new Material(LitShader.Interface)
    .SetUniform(binding: 0, new MarbleUniforms {
        BaseColorFactor = Vector4.One,
        MetallicFactor = 0.05f,
        RoughnessFactor = 0.32f,
        NormalScale = 1.0f,
        AlphaCutoff = 0.0f,
    })
    .SetTexture(binding: 1, marbleAlbedo, sampler: LinearWrap)
    .SetTexture(binding: 2, marbleNormal, sampler: LinearWrap)
    .SetTexture(binding: 3, marbleMr,     sampler: LinearWrap);

// Skinned variant — same set 2 shape, different shader interface.
var cesiumManMaterial = new Material(SkinLitShader.Interface)
    .SetUniform(binding: 0, /* ...factors... */)
    .SetTexture(binding: 1, cesiumAlbedo)
    .SetTexture(binding: 2, cesiumNormal)
    .SetTexture(binding: 3, cesiumMr);

// Glass — different shader, different set 2 shape.
var glassMaterial = new Material(GlassShader.Interface)
    .SetUniform(binding: 0, new GlassUniforms {
        Tint = new Vector3(0.85f, 0.95f, 1.0f),
        F0 = 0.04f,
        Thickness = 0.3f,
        Roughness = 0.02f,
    });
```

**What this reveals:**
- `Material.SetUniform(binding, T)` is **typed by struct**, not by name. A per-material UBO carries a single struct value — caller passes the whole thing. No partial-update API: re-`SetUniform` replaces the value.
- **NEW question Q-005:** the runtime needs to know how to marshal `T` to std140 layout. Two options: (a) require `T` to have a `[StructLayout(LayoutKind.Sequential, Pack = ...)]` matching std140, validate at runtime; (b) provide an explicit `BlockLayout` (offset + size per field) and copy field-by-field. The Lit interface above declared explicit layouts in the `ShaderInterface`; reusing those means option (b) wins. Probably cleanest: caller passes either an explicit field dictionary `{ ["uBaseColorFactor"] = Vector4.One, ... }` or a typed struct with known layout. Settle in implementation.
- **F-002 disposition holds:** material binding indices are the SHADER's binding indices. JSON material descriptors at cook time map `"albedo": "marble_albedo.png"` → `(binding 1)` via the shader interface's known shape. Runtime never does name-keyed lookups.

---

## Section 3 — Render graph declaration (passes + I/O)

Built once at engine init. Backend computes barriers, framebuffer
compatibility, and pass execution order from declared edges. No
per-frame graph rebuild.

```csharp
var graph = new RenderGraph(device);

// Resources — persistent for the graph's lifetime. Sized to swapchain extent
// where appropriate; recreated on resize automatically.
var sunShadow      = graph.DepthTarget("sun-shadow",        size: new(2048, 2048));
var spotShadows    = Enumerable.Range(0, 4).Select(i =>
                       graph.DepthTarget($"spot-shadow-{i}", size: new(1024, 1024))).ToArray();
var pointShadows   = Enumerable.Range(0, 2).Select(i =>
                       graph.DepthCube($"point-shadow-{i}", faceSize: 512)).ToArray();
var hdrScene       = graph.ColorTarget("hdr-scene",        format: Rgba16F, matchSwapchain: true);
var hdrLuminance   = graph.ColorTarget("hdr-luminance",    format: Rgba8,   matchSwapchain: true);
var hdrNormals     = graph.ColorTarget("hdr-normals",      format: Rgba8,   matchSwapchain: true);
var sceneDepth     = graph.DepthTarget("scene-depth",      matchSwapchain: true);
var sceneCopy      = graph.ColorTarget("scene-copy",       format: Rgba16F, matchSwapchain: true);
var bloomBright    = Enumerable.Range(0, 3).Select(i =>
                       graph.ColorTarget($"bloom-bright-{i}", format: Rgba16F, scale: 0.5f / (1 << i))).ToArray();

// Passes — declared with their I/O shape. Reads = dependencies; backend
// inserts barriers / picks render-pass compatibility automatically.

graph.GraphicsPass("sun-shadow")
     .Target(sunShadow, LoadOp.Clear, StoreOp.Store)
     .Shader(ShadowShader.Interface, SkinShadowShader.Interface);  // multiple-shader pass

foreach (var (spot, i) in spotShadows.Select((s, i) => (s, i)))
{
    graph.GraphicsPass($"spot-shadow-{i}")
         .Target(spot, LoadOp.Clear, StoreOp.Store)
         .Shader(ShadowShader.Interface, SkinShadowShader.Interface)
         .Conditional(() => spotLights[i].CastsShadows);
}

foreach (var (cube, i) in pointShadows.Select((c, i) => (c, i)))
{
    for (var face = 0; face < 6; face++)
    {
        graph.GraphicsPass($"point-shadow-{i}-{face}")
             .Target(cube.Face(face), LoadOp.Clear, StoreOp.Store)
             .Shader(ShadowShader.Interface, SkinShadowShader.Interface)
             .Conditional(() => pointLights[i].CastsShadows && pointLights[i].DirtyThisFrame);
    }
}

graph.GraphicsPass("scene")
     .Target(hdrScene,     LoadOp.Clear, StoreOp.Store)
     .Target(hdrLuminance, LoadOp.Clear, StoreOp.Store)
     .Target(hdrNormals,   LoadOp.Clear, StoreOp.Store)
     .Depth (sceneDepth,   LoadOp.Clear, StoreOp.Store)
     .Read(sunShadow)
     .Read(spotShadows[0]).Read(spotShadows[1]).Read(spotShadows[2]).Read(spotShadows[3])
     .Read(pointShadows[0]).Read(pointShadows[1])
     .Read(envCubemap)
     .Read(brdfLut)
     .Shader(LitShader.Interface, SkinLitShader.Interface, SkyboxShader.Interface);

graph.GraphicsPass("fur")
     .Target(hdrScene, LoadOp.Load, StoreOp.Store)
     .Depth (sceneDepth, LoadOp.Load, StoreOp.Store)
     .Shader(FurShader.Interface);

graph.GraphicsPass("hologram")
     .Target(hdrScene, LoadOp.Load, StoreOp.Store)
     .Depth (sceneDepth, LoadOp.Load, StoreOp.Store)
     .Shader(HologramShader.Interface);

graph.GraphicsPass("scene-copy")
     .Target(sceneCopy, LoadOp.DontCare, StoreOp.Store)
     .Read(hdrScene)
     .Shader(CopyShader.Interface);

graph.GraphicsPass("glass")
     .Target(hdrScene, LoadOp.Load, StoreOp.Store)
     .Depth (sceneDepth, LoadOp.Load, StoreOp.Store)
     .Read(sceneCopy)
     .Read(envCubemap)
     .Shader(GlassShader.Interface);

for (var i = 0; i < 3; i++)
{
    var src = i == 0 ? hdrScene : bloomBright[i - 1];
    graph.GraphicsPass($"bloom-bright-{i}")
         .Target(bloomBright[i], LoadOp.DontCare, StoreOp.Store)
         .Read(src)
         .Shader(BrightShader.Interface);
    // (blur H + V passes elided for brevity — same pattern)
}

graph.GraphicsPass("present")
     .Target(graph.Swapchain, LoadOp.Clear, StoreOp.Store)
     .Read(hdrScene)
     .Read(bloomBright[0]).Read(bloomBright[1]).Read(bloomBright[2])
     .Shader(PresentShader.Interface);

graph.GraphicsPass("debug")
     .Target(graph.Swapchain, LoadOp.Load, StoreOp.Store)
     .Shader(DebugLineShader.Interface);

graph.GraphicsPass("hud")
     .Target(graph.Swapchain, LoadOp.Load, StoreOp.Store)
     .Read(fontAtlas)
     .Shader(SpriteShader.Interface);

graph.Compile();
```

**What this reveals:**
- The render-graph DSL falls out cleanly. Every pass declares targets + reads + supported shaders.
- **F-005 disposition holds:** `graph.ComputePass(...)` (not used by ShaderLab but the slot exists for froxel fog later) would have the same shape minus `.Target(...)` for color/depth, plus `.Write(image)` for compute writes.
- **F-006 disposition holds:** sync is entirely derived from declared `Read` edges and the implicit pass ordering. The runtime never asks the demo to insert barriers.
- **F-012 disposition holds:** `LoadOp`/`StoreOp` live on `.Target(...)` at registration time. The per-frame `Execute` call (Section 4) doesn't touch them.
- **F-015 disposition holds:** the backend internally groups consecutive passes that target the same framebuffer-compatible attachment set into a single `VkRenderPass`. ShaderLab's `scene` → `fur` → `hologram` → `glass` chain all target `(hdrScene, hdrLuminance, hdrNormals, sceneDepth)` so they collapse into one VkRenderPass with four subpasses. The user doesn't think about it.
- **NEW question Q-006:** how do per-face cube shadow passes interact with the graph? They share a single resource (the cube) but render to different *attachments* (one face per pass). The `cube.Face(face)` accessor needs to be a graph-level concept — possibly resources have "views" that the backend can target individually.
- **NEW question Q-007:** `Conditional(() => ...)` for incremental rebake. Runtime checks the predicate each frame; if false, the pass is skipped and downstream consumers see the resource's previous contents. Does this break sync? **Tentative answer:** no, because we hold resource layouts stable across "skipped" passes — the previous frame's writes are still valid.
- **NEW question Q-008:** `.Shader(...)` lists supported shader interfaces. Can a draw use a shader NOT listed in the pass? **Tentative answer:** no — declaring shaders up front lets the backend bake the pipeline-layout descriptor set bindings into the pass at compile time. Adding a shader at runtime would invalidate that.

---

## Section 4 — Per-frame execution

Per-frame work is just supplying parameters and triggering execute. No
pass declarations, no LoadOp choices, no descriptor-set construction at
this layer.

```csharp
public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
{
    // Per-frame UBO data — shared across the whole frame.
    var perFrame = new PerFrameUniforms {
        ViewProjection = camera.ViewProjection,
        SunDirection = sun.Direction,
        SunIntensity = sun.Intensity,
        CameraPosition = camera.Position,
        AmbientBoost = ambientBoost,
    };
    graph.SetPerFrame(LitShader.Interface, perFrame);
    graph.SetPerFrame(SkinLitShader.Interface, perFrame);   // same struct, multiple shaders
    graph.SetPerFrame(GlassShader.Interface, new PerFrameGlass {
        ViewProjection = camera.ViewProjection,
        CameraPosition = camera.Position,
        Time = (float)time.Total,
    });

    // Per-pass bindings — shadow maps + env + shadow VPs.
    // (Most passes' set 1 was inferred from declared graph reads — the runtime
    // auto-builds the descriptor set from those. The demo only needs to push
    // values that aren't graph resources, like shadow VPs.)
    graph.SetPerPass("scene", new PerPassScene {
        SunShadowVP = sun.ShadowVp,
        EnvMipCount = envCubemap.MipCount,
        SpotVPs = spotLights.Select(s => s.ShadowVp).ToArray(),
        PointFarPlanes = pointLights.Select(p => p.Range).ToArray(),
    });
    graph.SetPerPass("sun-shadow", new PerPassShadow { LightVP = sun.ShadowVp });
    for (var i = 0; i < spotLights.Length; i++)
        graph.SetPerPass($"spot-shadow-{i}", new PerPassShadow { LightVP = spotLights[i].ShadowVp });

    // The actual draws. These go inside pass scopes; backend dispatches to the right pass.
    graph.Pass("scene", scope =>
    {
        // Skybox first (depth: LessEqualNoWrite).
        scope.Draw(skyboxMesh, skyboxMaterial);

        // Opaque static objects.
        foreach (var obj in opaqueObjects)
        {
            scope.Draw(obj.Mesh, obj.Material, pushConstants: new LitPushConstants {
                Model = obj.Transform.ToMatrix(),
                NormalMatrix = GraphicsMatrices.CreateNormalMatrix(obj.Transform.ToMatrix()),
            });
        }

        // Skinned objects — bone palette is an SSBO bound from the rig.
        foreach (var obj in skinnedObjects)
        {
            scope.Draw(obj.Mesh, obj.Material,
                pushConstants: new LitPushConstants { Model = obj.Model, NormalMatrix = obj.NormalMatrix },
                perDrawStorage: obj.Rig.PaletteBuffer);  // SSBO, set 3 binding 0
        }
    });

    graph.Pass("sun-shadow", scope =>
    {
        foreach (var obj in opaqueObjects)
            scope.Draw(obj.Mesh, shadowMaterial, pushConstants: new ShadowPushConstants { Model = obj.Model });
        foreach (var obj in skinnedObjects)
            scope.Draw(obj.Mesh, skinShadowMaterial,
                pushConstants: new ShadowPushConstants { Model = obj.Model },
                perDrawStorage: obj.Rig.PaletteBuffer);
    });

    // ... fur, hologram, glass, bloom, present, debug, hud — same shape ...

    graph.Execute(commandList);
}
```

**What this reveals:**
- Per-frame and per-pass uniform updates are **bulk struct uploads**, not name-keyed individual writes. One `graph.SetPerFrame(interface, struct)` call replaces the current ~6 individual `ShaderUniform("uViewProjection", ...)`-style calls.
- The draw site has 3 inputs: mesh, material, optional push constants / per-draw storage. Compare to today's ~17-parameter `ShaderUniform[]` per draw — order-of-magnitude smaller call site.
- **NEW question Q-009:** `graph.SetPerFrame(interface, struct)` is keyed by ShaderInterface — but multiple shaders share the same per-frame layout (Lit + SkinLit + Fur all want `viewProjection` etc). Setting per-frame separately for each is repetitive. **Tentative answer:** add a `PerFrameGroup` concept — bundle compatible interfaces and set per-frame uniforms by group. Or, simpler: per-frame UBOs are managed per-shader and the runtime de-dupes uploads automatically when the underlying buffer contents match.
- **NEW question Q-010:** `graph.SetPerPass(passName, struct)` uses string keys. Brittle. **Tentative answer:** `graph.GraphicsPass(...)` returns a typed handle; capture and reuse it for `SetPerPass(handle, struct)`. Same for `graph.Pass(handle, scope => {...})`.
- **NEW question Q-011:** how do `LoadOp.Load` chained passes (scene → fur → hologram → glass) coordinate their draws? Today they're issued in order in the demo's `OnRender`. Under the graph, they're declared as ordered passes in the graph, and the demo's `graph.Pass("fur", scope => {...})` is bounded to the fur pass scope. **Tentative answer:** the graph compiles to a linear pass sequence (matching declaration order modulo dependencies); per-frame draws inside `graph.Pass(...)` scopes are recorded into the corresponding command-buffer region.

---

## Section 5 — Cross-cutting & special cases

**Sprite + text:**

```csharp
// Sprite shader interface — minimal; per-frame UBO + per-draw texture + push constants.
public static class SpriteShader { /* set 0 UBO, set 2 texture, no shadows */ }

// Demo emits sprites at hud-pass time.
graph.Pass("hud", scope =>
{
    spriteBatch.Begin(scope, sortMode: SpriteSortMode.Texture);
    spriteBatch.Draw(fpsTextureRect, position, color);
    spriteBatch.DrawText(hudFont, "FPS: 60", new Vector2(8, 8), white);
    spriteBatch.End();
});
```

**What this reveals:**
- SpriteBatch becomes a scope-bound helper. It no longer takes an `IGraphicsDevice` — it takes a `PassScope` so it knows which graph pass it's emitting into.
- **NEW question Q-012:** SpriteBatch's texture-driven batching needs material flexibility (sprites use any texture). Two options: SpriteBatch internally manages a per-frame transient material pool, or treats each unique texture as a separate "draw" with its own material. **Tentative answer:** transient material pool managed by SpriteBatch internally; the new Material API supports cheap construction + caching by `(shader, texture)` key.

**DebugDraw:**

```csharp
// Already works via the existing Blix.Diagnostics debug.Draw.* surface. Runtime
// emits a debug pass after the demo's draws — no demo-visible change.
debug.Draw.Line(a, b, color);
debug.Draw.Arrow(from, to, color);
debug.Draw.Obb(model, color);
debug.Draw.ViewProjection = camera.ViewProjection;
```

**What this reveals:**
- DebugDraw layer survives cleanly. The runtime's existing `Window.AppendDebugLinesPass` adapts to declare a debug-line graph pass + per-frame draw scope. No demo-side change.

**Picking:**

```csharp
public void OnMouseDown(MouseButton button)
{
    var ray = mainCamera.ScreenPointToRay(mouseX, mouseY);
    var selectables = debug.CollectSelectables();
    // ... ray-vs-AABB + select-smallest unchanged ...
}
```

**What this reveals:**
- Picking is a `Blix.Diagnostics` concern, not a backend concern. Survives the reshape unchanged. F-013's prediction holds.

**Materials cooked from JSON:**

```csharp
// At cook time (tools/blix-cook materials/marble):
{
    "shader": "lit",                          // → maps to LitShader.Interface at runtime
    "bindings": {
        "binding-0": {                        // set 2 binding 0 (the per-material UBO)
            "uBaseColorFactor": [1, 1, 1, 1],
            "uMetallicFactor": 0.05,
            "uRoughnessFactor": 0.32,
            "uNormalScale": 1.0,
            "uAlphaCutoff": 0.0
        },
        "binding-1": "textures/marble_albedo",
        "binding-2": "textures/marble_normal",
        "binding-3": "textures/marble_mr"
    }
}
```

**What this reveals:**
- Material JSON keeps friendly developer-facing names (`uBaseColorFactor`) but resolves to `(set, binding, offset)` at cook time via the shader's declared `BlockLayout`. Runtime parses straight to bytes + descriptor writes; no name lookups.

---

## Open design questions (consolidated)

| Q | Question | Resolution | Source |
|---|---|---|---|
| Q-001 | Empty per-material set when shader has no material textures? | Allow; runtime skips bind. | Settled. |
| Q-002 | SSBO feature-gate detection. | Require at device-create; error early. | Settled. |
| Q-003 | Set 3 = lifetime tier (not type); push constants OR descriptor sets. | Yes; shader interface declares. | Settled. |
| Q-004 | Per-pass set rebind across pipelines in same pass? | Rebind per pipeline change; runtime caches. | Settled. |
| Q-005 | Marshal `Material.SetUniform<T>` struct → std140 bytes. | **`BlockLayout` is the source of truth.** Canonical path: `block.Write("uName", value)`. Typed-struct helpers are layered on top and validate against the declared layout at construction. C# `[StructLayout]` reflection is NEVER the layout authority — too easy to silently desync from GLSL std140. | Settled. |
| Q-006 | Cube-face-as-pass-target. | **Resource views are first-class.** `TextureResource.Face(int)` / `.Mip(int)` / `.ArrayLayer(int)` returns a `TextureView`; graph `Target`/`Read` APIs accept either resources or views. Generalizes beyond cube faces — also lights up mip-chain bloom, env prefilter, render-to-array-layer for free. | Settled. |
| Q-007 | `Conditional` passes affect sync? | No — layout stays valid across skipped pass; previous writes still readable. | Settled. |
| Q-008 | Draws use shaders not declared in the pass? | No — pass shader list is closed. | Settled. |
| Q-009 | Per-frame uniform repetition across shaders. | Per-shader UBO; runtime de-dupes uploads when underlying buffer contents match. | Implementation detail. |
| Q-010 | String-keyed `SetPerPass` brittleness. | `GraphicsPass(...)` returns a typed handle; ALL runtime API takes the handle. Strings stay as labels for diagnostics + error messages only. | Settled. |
| Q-011 | Per-pass draw recording vs declaration order. | Linear compile from declaration order; `Pass(handle, scope)` records. | Settled. |
| Q-012 | SpriteBatch material flexibility. | **Don't fabricate per-texture materials.** SpriteBatch owns a single sprite shader + a transient descriptor cache keyed on `(texture, sampler, blend)`. Batches by texture page. No material objects for sprites. | Settled. |

All twelve resolved. No genuinely-open design questions remain blocking
Vector A's start.

---

## Vector mapping (does the draft cover the audit?)

- **Vector A** (binding layer): Sections 1 + 2 cover F-001/F-002/F-007/F-009/F-011/F-016. ✓
- **Vector B** (render graph): Section 3 covers F-005/F-006/F-012/F-015. ✓
- **Vector C** (resource shape): Implicit in Section 3 (resources via `graph.ColorTarget` / `DepthTarget` etc.) — F-003/F-004 don't get a dedicated draft section because they're not binding-shape-driven. Cleanup is mechanical.
- **Vector D** (helpers): Already shipped.

**No friction notes left unaddressed. All twelve design questions resolved.**

---

## Refined milestone sequence

After review of this draft + the friction-note audit, the implementation
sequence below replaces the simpler "Vector A → C → B" plan in the
friction-doc audit section. The key refinement: matrix-convention surgery
gets its own dedicated commit with tests *before* any binding-model work
starts, and the binding model lands *before* the render graph, with
two intermediate validation demos.

1. **F-016 matrix migration.** Engine adopts .NET row-vector convention.
   GL backend's `WriteColumnMajor` becomes `glUniformMatrix4(transpose: true)`
   with raw .NET bytes. `GraphicsMatrices.CreatePerspective` /
   `CreateLookAt` etc. rewritten to row-vector form. Vector D's
   `CreatePerspectiveVulkan` folds into the unified `CreatePerspective`.
   **Acceptance criteria:**
   - GL cube (existing demo) still renders correctly
   - Vulkan cube still renders correctly
   - `CreatePerspective` test (numerical)
   - `CreateLookAt` test
   - `CreateNormalMatrix` test
   - `Vector4.Transform` against the new convention
   - Matrix-upload golden-byte-layout test (memory contents match expected std140 bytes after upload)
   - **Backend-symmetry test:** identical `Matrix4x4` input → identical fragment output on GL and Vulkan (off-screen render, pixel compare)

2. **Vector A binding model — no graph yet.** Implement `ShaderInterface`,
   descriptor set lifetimes 0/1/2/3, explicit-binding `Material`, push
   constants for per-draw. Stays inside the existing per-frame
   `commandList.Pass(...)` surface. No render graph yet; the existing
   imperative pass declaration carries.

3. **Two-cube validation demo.** Same shader, same pipeline, two materials
   differing by per-material texture (or factor), two transforms via push
   constants. Single demo validates set-2 binding + per-draw push constants
   in one go. Replaces the current single-cube `VulkanHello`.

4. **Mini offscreen pass + present.** Two-pass demo: render a colored
   triangle to an offscreen `Rgba16F`, present pass samples it through
   a fullscreen-quad shader. Proves the binding model handles multi-pass
   resource dependencies before the render-graph machinery lands.

5. **Render graph (Vector B), no subpass collapse, no compute execution.**
   Declarative pass shape per Section 3. v1 compiles to a linear sequence
   of `VkRenderPass`-per-pass with derived barriers. Subpass collapse is
   a post-v1 measurement-driven optimization. `ComputePass` interface
   exists in the API but executing one throws `NotImplemented`.

6. **ShaderLab port — incremental.** Lit shader first (single-shader scene).
   Then add skybox, sun shadow, spot/point shadows, skinned glTF, fur,
   hologram, glass, bloom, present, debug, hud. Each adds at least one
   binding shape or pass topology the previous step didn't exercise.

7. **Sponza Modern port.** Validates the API under content scale + glTF
   variety. May surface volume-driven friction not predicted by ShaderLab.

8. **Visual rework — TAA + froxel fog.** Froxel fog forces the compute-pass
   implementation. TAA forces history-buffer + motion-vector workflow.

9. **Sunset OpenGL.** Delete `Blix.Graphics.OpenGL` + `Blix.Runtime.OpenTK`.
   Sponza Walkthrough + ShaderLab + SponzaModern demos either rewritten
   on the new engine or retired.

The big shift vs the earlier audit ordering: **steps 1–4 are all
pre-graph**. The graph (step 5) lands on top of a binding model that's
already been validated by two intermediate demos. Each step has its own
failure mode in isolation — if the cube renders wrong at step 1 it's
matrix; if two cubes share state at step 3 it's set-2; if the offscreen
pass tears at step 4 it's our manual sync; only at step 5 do we trust
the graph to derive sync correctly.
