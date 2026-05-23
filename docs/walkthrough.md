# Sponza Walkthrough

`Blix.Demos.Walkthrough` is the engine's flagship demo and the place to see how the renderer's pieces fit together at the level of a real scene. It loads the standard Sponza atrium, paints four hanging braziers with volumetric fire, lights it with one directional sun + four point lights, casts cascade shadows for the sun and cube shadows for each brazier, applies the full Karis split-sum IBL from an HDR sky probe, runs SSR off the marble floor, layers volumetric fog with sun god-rays and point-light scatter, blooms, and tonemaps through one of ACES / AgX / Reinhard / Neutral.

Every dial is exposed in the ImGui debug overlay. The demo is one C# file (`src/Blix.Demos.Walkthrough/Program.cs`) plus a `Shaders/` directory plus the engine shader library at `src/Blix.Shaders/`.

This doc walks the architecture; for renderer concepts in isolation (PBR, IBL, SSR, etc.), see [`renderer.md`](renderer.md).

## Frame pipeline

Per-frame pass order (names match `FrameDebugPacket` entries):

| # | Pass name | Target | Reads | What it does |
| --- | --- | --- | --- | --- |
| 1a | `walk.pshadow.l{i}.f{j}` | per-light cube depth, one face per pass | scene | **Conditional**: only re-baked when a light's tracked position drifts past 0.01m from its last bake. Six face passes per affected light. Static lights cost zero per frame. |
| 1b | `walk.shadow.cascade{c}` | cascade `c`'s 2048² depth | scene + sun VP for that cascade | Three cascades, each fitted via stable sphere bound + texel snap. View-space radius keeps the cascade from "swinging" as the sun rotates. |
| 2 | `walk.scene` | `hdrSceneSurface` MRT (Rgba16F color + Rgba8 material + depth) | env cubemap, BRDF LUT, irradiance + prefiltered specular cubes, all shadow maps | Opaque PBR pass + skybox fill + flame quads (alpha-blended) + volumetric fire boxes (alpha-blended ray march). Lit shader writes both attachments; other materials also write attachment 1 (matte for SSR purposes) — see [Material G-buffer](renderer.md#material-g-buffer-mrt). |
| 3 | `walk.fog` | `hdrSceneSurface` (additive) | scene depth, sun shadow cascade 0, point light positions | Full-screen ray march with HG phase + sun in-scatter (via shadow-gated per-step accumulation) + analytical point-light scatter halos. Additive into the HDR scene. |
| 4 | `walk.ssr` | `ssrSurface` (Rgba16F) | HDR scene color, material G-buffer, scene depth | NDC-space march with binary-search refinement + per-pixel hash jitter + HDR clamp. Gated by upward normal + below-camera + material-roughness < cutoff. |
| 5a | `walk.bloom.down{i}` × 4 | bloom mip `i+1` (half-res-per-level Rgba16F) | bloom mip `i` (or scene at i=0) | Dual-filter downsample chain. |
| 5b | `walk.bloom.up{i}` × 4 | bloom mip `i-1` (additive) | bloom mip `i` | Tent-filter upsample chain. |
| 6 | `walk.composite` | swapchain | HDR scene, bloom mip 0, SSR | Grade in linear HDR (temperature × saturation × contrast), tonemap (ACES / AgX / Reinhard / Neutral), gamma-encode. |
| 7 | `debug` | swapchain | — | Appended by the runtime when debug-draw commands were issued (cascade frusta, AABBs, axes). |
| 8 | `walk.hud` | swapchain | — | FPS, camera position, key hint text. SpriteBatch + Font. |

`walk.pshadow.*` passes are skipped entirely on frames where no light moved — `PointShadowsDirty(activeShadowCount)` returns false → bake loop short-circuits.

## Lighting layout

- **One directional sun**, direction auto-aligned to the HDR sky's brightest pixel at startup via `HdrSunFinder.FindSunDirection`. The "Sync sun to HDR" button under the Sun debug scope restores this alignment after manual slider edits.
- **Four point lights** placed on the brazier bowls at `(±4.95, 1.10, ±1.76 / ±1.15)`, warm amber tint, range ~6m. Each is wired to a cube shadow map (incremental re-bake) and contributes to fog scatter analytically.
- **HDR env probe** baked into 3 cubemaps: diffuse irradiance, GGX-prefiltered specular (9 mips), 2D BRDF LUT. Polyhaven HDRIs work; the `PbrIblBaker` clamps single-pixel suns at magnitude 50 to suppress firefly speckles on normal-mapped surfaces.

## Asset pipeline

| Asset | Source | Importer / loader |
| --- | --- | --- |
| Sponza atrium geometry + materials | `Assets/sponza/Sponza.gltf` | `Blix.Assets.GltfImporter` → `GltfModel`; materials resolved via the demo's material pipeline. |
| HDR sky probe | `Assets/textures/sky_hdr.hdr` (Polyhaven `kloppenheim_06_puresky_2k`) | `ImageLoader.LoadRgba32F` → equirect → `EquirectangularToCubemap.Convert` → IBL probes. |
| Volumetric fire | `Assets/textures/fire_volume.bvol` (JangaFX Small Campfire VDB → R8 atlas) | Custom `.bvol` loader; uploads to a 3D R8 texture sampled by `volume.frag`. |
| Fire sprite atlas | `Assets/textures/fire_atlas.tga` (Unity Labs Paris Flame02, 16×5 cells) | Image loader → `flame_atlas.frag` per-frame sampling. |
| Font for HUD | `Assets/fonts/Roboto-Regular.ttf` via `FontImporter` (json spec) | Multi-size bake; SpriteBatch text. |

A python helper at `tools/vdb_to_blix_volume.py` converts OpenVDB grids into the `.bvol` packed atlas format.

## Volumetric fire

Each brazier renders a unit cube at the lamp position; the cube's fragment shader (`volume.frag`) ray-marches density through a stacked field:

- **VDB term** (when `fire_volume.bvol` is present): trilinear sample of the R8 atlas at a time-driven slice index. Per-lamp phase offset puts each brazier at a different point in the 34-frame loop so they don't pulse in sync.
- **Procedural term**: teardrop SDF + advected 3D FBM noise via `blix_vnoise3` / `blix_fbm3` from `lib/noise.glsl`. Vertical gating keeps the procedural body strong in the upper region while the VDB carries the base.
- **HDR core boost** in the colour ramp peaks at `(3.5, 3.0, 1.5)` so bloom catches the flame tips.

The volume box is alpha-blended with `DepthState.LessEqualNoWrite` — it doesn't write depth, so SSR's depth-driven ray march sees through to the brazier geometry behind. The MRT material output writes 1.0 (matte) at attachment 1, so SSR rays hitting fire pixels are gated out.

## Material G-buffer + SSR gate

Detailed in [`renderer.md` → Material G-buffer](renderer.md#material-g-buffer-mrt). Summary: `hdrSceneSurface` has a second Rgba8 attachment carrying roughness; the lit shader writes the actual roughness, other passes (sky, volume, flame, fog) write values combined with their blend mode so the underlying material survives. SSR samples `uRoughnessMap` and smoothsteps against `uRoughnessCutoff` (default 0.4). Cloth, plaster, brick all sit above the cutoff → no reflection. Polished marble sits below → reflection.

## Debug overlay

Every knob the demo exposes lives under `Walkthrough/` scopes:

| Scope | Notable controls |
| --- | --- |
| `Floor` | Override marble metallic/roughness, bypass MR texture. |
| `Frame` | FPS / position / submesh count. |
| `IBL` | Env mip count read-out. |
| `Lights` | Point-light intensity, range, master toggles. |
| `Sun` | Yaw, pitch, strength, Rebake Sky, Sync sun to HDR. |
| `Shadow` | Ortho extent, sun distance, near/far, visualize CSM, disable cascades fallback. |
| `Tonemap` | Exposure, bloom strength, operator (ACES / AgX / Reinhard / Neutral), saturation, contrast, temperature R/G/B. |
| `Fog` | Density, sun scatter, point scatter, max distance, steps. |
| `SSR` | Enabled, show only, flip V, intensity, max distance, steps, hit thickness, roughness cutoff. |
| `Presets` | Cinematic + Night one-click lighting profiles. |

Reading these in code is the fastest way to understand what each piece does — `IDebuggable.Debug(DebugContext)` is the single function that lists everything.

## Known caveats

- **GLSL ASCII only** on macOS — even comments. Symbols like `∫` produce a useless "premature EOF on last line" error.
- **Don't shadow GLSL builtins**: `noise1..4` are reserved; the demo uses `vnoise3` etc.
- **SSR is screen-space**, so reflections of fire show the fire as the camera sees it, not as the floor would. The HDR clamp at 2.0 makes this plausible; a planar reflection probe is the principled fix.
- **Depth-deriv normals are too noisy** on 24-bit depth to use as the actual reflection normal — only the SSR gate uses them; the reflection vector itself is hardcoded `(0, 1, 0)`.
- **`stbi_set_flip_vertically_on_load` is a sticky global** — the HDR loader resets it explicitly. If you add a new CPU-sampled image-load path, do the same or rows come out upside-down.
