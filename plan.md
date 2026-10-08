# Blix — the current plan

What is open, and nothing else. Finished work is not recorded here: its reasoning lives where it
governs behaviour — code comments, [`docs/`](docs/), and the suites that pin it (conventions §9) — and
its history in git.

---

## The arc now: geometry and lighting for huge, dynamic worlds

**Why.** Not speed, as first thought: timed warm on the orbit (`--ab <term>`, 1500 frames), Bistro's frame
is ~17 ms and Sponza's ~19, all lighting together is 8.3 and 9.5 ms of it, and the terms the arc began
with are small (sun-bounce injection 0.35 / 3.0 ms, probe terms 0.4 / 0.4, GTAO ~0 / 0.7, shadows ~0 /
1.0); most of it is the lit pass's material shading. The 18 ms that started this was the cold regime.
The approach is the limit for what the constraints below ask: a regular probe grid sized to the scene's
bounds (cost and resolution follow world size), rays that march a cook-baked voxel grid rather than
geometry (so nothing that moves occludes or bounces light), and four unrelated structures (cascades, Hi-Z,
SH volume, occupancy) for one scene.

**The constraints, decided.** Design for dynamic geometry; assume huge worlds; ray tracing behind one
interface whose backends are software (compute traversal; this Mac's MoltenVK exposes no ray query) and
hardware (`VK_KHR_ray_query`) where a device has it.

**How moving things take part.** Receiving: the GI lives in space (probes), so anything samples it where it
stands. Occluding: characters through capsule or sphere proxies, not skinned triangles in a BVH. Large
dynamic geometry: per-mesh cooked BVHs placed as instances in a runtime top-level structure; a changed
instance dirties the probes near it, which retrace first, and that runtime trace overrides the static bake
exactly where the world has changed.

**Stages, in order.**
0. *Open from the instruments stage:* `./blix run` was seen dropping an app's flags (`--viz`, `--cam`,
   `--frames`, `--win`) with argv intact, and did not reproduce in six attempts (isolated, traced, beside a
   second instance). The unread warning now says which failure it was when it fires (nothing asked for the
   flag, or asked and not taken); the next occurrence is diagnosable. The scale scene is `blix city`
   (`tools/city/setup.sh`, `--scene city`): one seed is one city at any size.
1. *Cooked clusters and per-mesh BVH*: the format and the cook (meshoptimizer's meshlets; the cook already
   simplifies).
2. *GPU-driven culling, LOD and a visibility buffer* over those clusters, measured on Bistro and the
   generator. Bindless materials as its prerequisite. Where it starts, measured on the city (seed 1): the
   host keeps each unique primitive once (82,576 vertices at every size) and draws its placements
   instanced, so memory is flat to 64x64 blocks (123,422 placements, ~0.45 GB), and culling and LOD per
   placement run on the GPU (`scene_cull.comp`; `--cpu-cull` is the A/B): building a frame costs the CPU
   0.06-0.09 ms at every size, against 0.9 ms at 8x8 and 44 ms at 64x64 on the CPU path, for the same
   picture. Two-phase occlusion culling over placements follows it (`scene_occlusion.comp`; `--no-occlusion`
   is the A/B, `--occlusion-cut` makes every frame a camera cut): city 64x64 29.0 -> 5.4 ms, Bistro flat
   (not triangle-bound), Sponza ~1% dearer, every view and orbit the same picture. Cluster grain is
   measured and NOT built: the census (`WriteClusterCensus`) says it keeps 9.4% where placement occlusion
   keeps 13.5% (city), 75.4% against 91.5% (Sponza), 36.2% against 41.9% (Bistro); frustum and normal-cone
   tests alone are worth 2-12%. Priced at what triangles cost each scene, that is ~1 ms of the city's 5.4
   and ~0.8 ms of Sponza's ~25 (warm), nothing on Bistro, for a new draw path (no drawIndirectCount, no
   mesh shaders: meshlet vertex lists, GPU index compaction, vertex pulling in four vertex shaders). The
   visibility buffer is not the lever either: `--lit-flat` (flat.frag in the lit pass, all else equal)
   leaves Sponza's full-LOD cost where it was (8.7-9.6 ms against 7.4-8.0 with materials), so that cost
   is geometric, not shading on small triangles. The cull keeps its counts on the GPU, so caster and
   triangle counts read back only under `--cpu-cull`.
3. *The ray-query interface*: software backend first, hardware when a device can prove it. 3a is the CPU
   half (`Blix.Geometry`: `BvhBuilder`, `TriangleBvh`, `RayQueryScene`, Test.Graphics BV): binned-SAH
   hierarchies in 32-byte GPU-ready nodes, the watertight triangle test, Ize's widened box exits, hits
   in `rayQuery`'s terms (facing is the mesh's own). Built at load (`--ray-scene`), Release: Sponza
   12.8M triangles in 2.2 s and 353 MB of nodes, Bistro 2.8M in 0.87 s, the city's 123k placements in
   0.29 s; 0.07-0.37M CPU rays/s per core. 3b-i put them on the GPU (`RayQueryGpuData` packs five
   blocks, `Blix.Shaders/ray_query.glsl` traverses them, `ReadGpuBuffer` reads answers back): with
   MoltenVK's fast math off (`MVK_CONFIG_FAST_MATH_ENABLED=0`) the GPU equals the CPU oracle bit for bit
   on 65,536 rays per scene (`--ray-check`). With it on (the default) 1-3 grazing edge hits per ~50k
   disagree: division is not correctly rounded there; fused multiply-adds are forbidden by `precise`, which
   is what keeps the edges watertight. `--ray-probe` runs one ray against one triangle on both. 3b-ii:
   `--ray-bench <mixed|probe|camera>` counts each ray's work (nodes, placements entered, triangles; exact,
   unlike GPU timings here, which moved up to 50% between batches of unchanged code). Placements of meshes
   used once now merge into regions, cells of triangles in world space (`RayQueryScene`, 262k triangles
   each, owners per triangle): camera rays enter 2.3 cells on Bistro where they entered 12.1 placements,
   8.8 on Sponza where 18.2, nodes per ray 118 -> 79 and 157 -> 110; still bit-exact; Release build 1.7 s
   and 4.3 s. A coarser cell budget no longer changes Sponza's nodes per ray (105-118 from 65k to 4M), so
   the cost left is the traversal itself. `--ray-lod-error M` traces each mesh's coarsest cooked level
   within M metres: memory, not speed. Sponza packs to 781 / 475 / 234 MB at 0 / 2 cm / 20 cm and Bistro
   to 197 / 67 / 58 MB, while nodes per probe ray stay 42-46 and 24-27 (depth is the log of the triangle
   count) and the GPU stays bit-exact. The city does the same ~40 nodes per ray 3-4x faster from 1.3 MB of
   nodes, so traversal is bound by memory traffic: the structural lever is compressed wide nodes (eight
   quantised children in ~80 bytes), worth building once a consumer's ray budget says it is needed. 3c:
   `--ray-view` traces the camera's view (half resolution, through each pixel's depth sample) beside the
   raster depth: within 1% on 94-96% of pixels, the rest alpha-tested foliage (5.4% Sponza, 0.65% Bistro:
   rays have no alpha test yet), depth edges and LOD; with the raster at full detail too, Sponza leaves
   48 pixels unexplained in 518,400. Hits now carry a world-space geometric normal (checked against the
   CPU's). Far-from-origin rounding (the city at
   2 km moves an entry point 0.1 mm) is the huge-worlds origin question, not a traversal one. The cook
   takes the hierarchies once the layout stops moving.
4. *Runtime GI*: camera-relative clipmap probes traced through 3, relocated out of walls, rays amortised
   over frames, dirty-region updates, capsule occluders. In steps: 4a surfaces at hits (done: below);
   4b the sky/bounce injection traces rays into the atlas the lit pass already reads, A/B against the
   occupancy march on the same grid; 4c a camera-relative clipmap in place of the bounds-sized grid; 4d
   relocation, dirty regions, capsules. 4c holds what the lit pass already consumes, both traced: bounce
   (irradiance atlas) and directional sky visibility (the cosine-weighted share of a probe's rays that
   escape, in the depth atlas's fourth channel), so nothing baked is needed (no occupancy, sky SH or
   albedo grid). 4 levels of 32x16x32, spacing doubling, camera-centred and snapped, slots addressed
   toroidally with each slot remembering its world cell (a reused slot restarts its history); lookup at
   the finest level holding the point, blended at borders, Chebyshev only; buried = mostly back-face
   hits. Steps: 4c-i addressing (`probe_clipmap.glsl` and its C# twin, deviceless tests); 4c-ii the traced
   injection (budget, new slabs first, staleness, buried); 4c-iii the incident pass and fog read it under
   `--gi-clipmap`, A/B against today's field (pictures, the triangle reference at the same points); 4c-iv
   the city lit with no bake, and the clipmap the default if it holds. 4c-i and 4c-ii are in: the GPU's
   slots equal `ProbeClipmap` on all 65,536, all four levels solve within 300 frames (512 probes a frame,
   ~2.9 ms), the incident field can read it (`incident_clipmap.frag`). Two new arbiters (`--probe-
   reference`): sky visibility against triangles at baked probes, and the SURFACE reference, which judges
   what is shaded at camera-visible points (so it sees leaks). At Sponza's surfaces the clipmap's sky
   visibility is unbiased (median 0.99, error 0.008) where the bake is low (0.64, 0.014); sun-only bounce
   is over at the p90 in both (bounds 2.8x, clipmap 3.1x: leaks). The clipmap is the DEFAULT and the only diffuse
   GI since the frame audit (stage 3, below): the bounds-sized atlas, its injection and the cooked volumes are gone. The surface reference now
   judges sky-carried bounce too, with the sky's radiance on the CPU (`SkyRadiance`: the cooked
   environment with the sun disc replaced as the cook does, checked against the cooked irradiance to
   0.97-1.03 for every normal). Both fields carry about 1.5x too much (bounds median 1.52, clipmap 1.45):
   the shared model, sky irradiance times the share of sky visible, credits a courtyard surface with the
   sky's average brightness when it sees the dim zenith. The lit pass's direct sky has the same bias even
   with the TRUE visibility (Sponza median 1.35, Bistro 0.80 with p90 2.07), so under `--gi-clipmap` the
   probes now hold sky radiance (escaped rays read the disc-free prefiltered sky; a hit takes the field's
   whole irradiance, no separate sky term) and the lit pass takes ALL indirect diffuse from the field
   (`uIncident.w`; sky visibility kept for specular only). Judged on total indirect diffuse (sun bounce +
   sky bounce + direct sky) at 1500 frames after load: Bistro bounds median 0.94, |err| 0.105 -> clipmap
   1.00, |err| 0.063; Sponza 1.52 (p90 5.45) -> 1.33 (p90 4.19), |err| 0.0130 -> 0.0127, but the mean
   rose (0.0332 -> 0.0350 against 0.0256). Sun off, Sponza's sky part is still 1.5x by mean (clipmap
   0.0141, bounds 0.0145, reference 0.0093; median 1.61 against 3.03). What still over-lights Sponza,
   found with a CPU twin of the clipmap's sampling (`SponzaLoop.ClipmapTwin.cs`; matches the GPU field,
   Bistro median 1.00, p10-p90 0.99-1.01) inside the surface reference: NOT leaks (17% of each answer
   comes from probes the surface cannot see, but visible probes alone read 1.31 against 1.29), NOT the
   sky cube (the prefiltered mips integrate to the cooked irradiance within 0.93-1.04, sun-facing
   included), NOT probe density (spacing 0.25/0.5/1 m: median 1.27/1.29/1.37). As shaded (field x
   GTAO) Sponza is median 1.15 with the excess in a quarter of the pixels (2.5x, left arcade and
   curtains), and there the truth at the strongest probe, 0.5 m out, is 2.66x the truth at the
   surface; the probes' own values are only 1.11x (1.19x there). So the field lacks light variation
   below a probe cell (concavities darker than the air beside them), GTAO (0.8 m, screen space) covers
   0.77 of what needs ~0.4, and it grows with distance as cells reach 2-4 m (the user's "far end lights
   flat"; Bistro's p90 rises 1.21/1.84/2.14 over levels 0/2/3). A multiplied traced AO ranged to the
   spacing is the wrong model (worst group 2.52 -> 1.80 at best, over-darkens elsewhere, worse in
   Bistro): occluded near directions carry nearby bounce, not black. The CPU gather prototype (inside
   the surface reference; each ray also traced to the truth): SHORT rays (R = 1-2 spacings, misses
   read the field's irradiance at the ray's end facing along it) make it WORSE (worst group 2.53 ->
   3.20): even with the TRUE irradiance there, that proxy is 1.9x the light actually arriving along the
   ray (0.0171 against 0.0090); the field's own error adds 11%. Irradiance is not radiance where light
   comes through openings beyond R. FULL-LENGTH rays (field irradiance only at the surface a ray finally
   hits, the sky where it escapes) are unbiased: Sponza mean 0.0283 against 0.0272, median 0.99; worst
   group median 1.19 (was 2.50); Bistro median 1.00. But noisy at 48 rays (p10 0.35, p90 2.27, mean
   |error| 0.0111 against field x GTAO's 0.0095), so it needs accumulation. Cost: the GPU traces ~20 M
   closest rays/s here, so a ray per pixel at 1080p is ~100 ms. Screen probes (`--gi-screen-probes`,
   with the clipmap; `screen_probe.glsl` holds the rules): a probe per 16x16 tile on the surface, 8
   full-length rays a frame (sun by shadow ray, clipmap irradiance at hits, sky at escapes), RADIANCE
   in L2 SH (world probes keep irradiance; one-way), tile headers over a probe pool (one probe a tile
   today), history only on the same surface (placement identity in the pre-pass normal's alpha,
   normal, plane, within one tile's footprint, still visible: disocclusion), and pixels blend toward
   the clipmap by how settled their probes are (foliage never settles under TAA jitter). Sponza as
   shaded median 0.97, |err| 0.0058 (field x GTAO was 1.14, 0.0095; p90 1.54 from 3.18); Bistro 0.95,
   0.031 (0.92, 0.049). Default look bit-identical. Identity is the MATERIAL (set 3 binding 2, a table
   by transform row; placement identity broke at the cook's chunk seams). History per frame: still
   91.4%; walking pace (`--orbit-frames 1200`, 0.3 deg/frame) Sponza 85.9%, Bistro 87.4% (the default
   orbit, 3 deg/frame, is a whirl: 65.7%, nothing lives past ~30 frames on screen). Bistro's one mass
   restart on that path is the camera leaving a building (correct). Cost, attributed by
   `--screen-probe-ablate` (Sponza, 600 frames, paired repeats): closest rays ~14.5 ms, shadow rays
   ~6.5, clipmap at hits ~0, everything else 0.26 of 21.5. The sun at a hit now comes from the
   cascades where they cover it (a shadow ray beyond): 24.4 -> 15.5 ms. Half the tile ROWS trace a
   frame (whole workgroups skip; a checkerboard would idle lanes in busy SIMD groups): 15.0 -> 8.6-10.1
   ms. At 1500 frames: Sponza 8.9 ms, as shaded median 1.00, |err| 0.0060 (0.97, 0.0058 before the
   cuts); Bistro 4.9 ms (from 18.9), 0.96, 0.031. Open: the half that does not trace still costs ~4
   ms on Sponza (history search: up to 9 candidates, a depth fetch for visibility each).
   NOT SETTLING (the user, twice): `--stability K` reads the incident light and the final HDR every
   frame for K still frames; with `--no-taa` (jitter otherwise counts every edge) the default field
   varies 0.08% (median pixel), the clipmap 0.01% (p99 6.8%: its own re-solving, 0.00% frozen),
   screen probes 2.04% with 51% of pixels over 2%. Not the clipmap (frozen: still 2.02%); the probes'
   own running average at a 64-frame cap (256: 0.51%), and only 1.4x from a spatial filter alone.
   Now: history 256 + a 3x3 same-surface filter (`screen_probe_filter.comp`, 0.17 ms, read-only:
   accumulation continues unfiltered): Sponza 0.37%, 1.8% of pixels over 2% (clipmap 8.8%, default
   2.3%); Bistro 0.15%, 0.2% (clipmap 2.9%). Accuracy unchanged (Sponza 1.01, 0.0058; Bistro 0.95,
   0.030). Price: ~4 s for a lighting change to settle in; adaptive history is the fix when a scene
   changes its light. MoltenVK 1.4.1 here has no VK_EXT_mesh_shader (descriptor indexing: yes).
   Normal-map detail: with the clipmap's field ALL indirect diffuse comes from the incident pass, which
   evaluates at the pre-pass's geometric normal, so normal maps stopped shaping it (the user: switching
   the misnamed "Half-res incident field" toggle off, i.e. the inline baked path, looked more detailed;
   the inline path at the geometric normal moves 15% of Sponza's pixels). The incident pass now writes
   a second target, the light's luminance gradient with the normal (exact from screen probes' SH; the
   clipmap contributes none), resolved alongside, and the lit pass carries the light to the normal-
   mapped normal to first order. The gradient now comes from the CLIPMAP (irradiance at the geometric
   normal and two ~20-degree tilts, same probes and weights: deterministic; the screen probes' SH
   gradient shimmered, 3.40% -> 5.70% presented). With TAA off it adds nothing to the variation (0.89%
   either way); with TAA on, applied at the full normal map it doubled the shimmer (6.69%): the detail
   below a pixel is what the jitter samples differently each frame. So the lit pass reads the normal
   map 2 mips down for it and clamps the factor to [0.5, 1.5] (`--incident-normal-bias`,
   `--incident-gradient-clamp`): bias 0/1/2/3 move 13.0/5.3/1.7/0.56% of pixels at 6.69/4.78/3.67/3.44%
   presented (none 3.40%). `--stability` reads the TAA-resolved image too; the ~3% floor in EVERY arm
   (default 3.16%) was the TAA itself. TAA, measured with `--stability` (presented variation, and error
   against the K jittered frames' average = the supersample; `--taa-count-rejection`): the old resolve
   was 3.16% / 4.20%, its error barely under ONE raw jittered frame (4.95%), and only 4.4% of pixels
   clipped, so the clamp was not it. (1) It reprojected the jittered point, resampling history at the
   jitter offset every frame: motion reprojection 1.89% / 2.31%. (2) Heavier history got steadier but
   wronger (0.9: 1.12% / 4.68%, 4.51% of it BIAS): R11G11B10F history rounds to ~1.6% steps and stops
   converging once a frame's step is below half one; Rgba16F at 0.9: 0.83% / 0.94% (Bistro 1.43% /
   2.40% -> 0.34% / 0.49%). Variance clipping (5 taps) measured worse than the min/max box; linear vs
   tonemapped blend made no real difference. Defaults now: motion, Rgba16F, 0.9 (`--taa-reproject
   old`, `--taa-history11`, `--taa X`). Normal-map bias back to 1 (screen probes 1.24% presented).
   Then the user, looking: ivy/foliage never settles, and "tiny spots of light and dark across walls".
   `--stability` now writes a heat map of the presented image's variation. The wall spots were the
   normal-map gradient at bias 1 (weave- and grain-scale speckle with it, none without): back to 2.
   Seeding fresh screen probes from the clipmap and an outlier clamp on probe updates measured nothing
   and were dropped. Foliage was white in EVERY arm, the default included, and hashed alpha off changed
   nothing: sub-pixel leaves are leaf or background per jittered frame, and their pixels were clipped
   (so reset) every frame. TAA now accumulates a per-pixel count in the history's alpha (blend 1/count
   up to 32 for a still pixel, 10 by a pixel of motion, i.e. the old 0.9) and relaxes the clamp box x3
   where a pixel does not move: Sponza 0.83% -> 0.22% median, pixels over 2% 23.9% -> 7.8%, foliage
   settles; Bistro 0.34% -> 0.10%; screen probes 0.97% -> 0.34%. Open: that arm reads ~0.65 points
   more bias against the supersample (1.19% vs 1.05% total), not precision (a dithered write), not
   tonemapping (linear blend), not window lag (K 256). Not measured: ghosting under motion.
   Then the user: bounce light "grainy/spotty, shimmers a second or two, settles into the same spotted
   pattern" (incident field off removes it). Reproduced at the user's F12 camera with
   `--screen-probe-reset-at F` (drops every probe's past, as a move does) and the raw indirect light
   (`--viz 10`): 30/120/600 frames after a reset it differs from converged by 10.0/5.7/3.6%, and two
   converged runs with different seeds (`--screen-probe-seed-offset`) still differ by 2.48%: the
   probes' few rays, in light that arrives through a few bright openings. Fresh probes now start from
   the clipmap's answer (as radiance, worth 16 frames), trace every frame while under 32 frames, and
   filter wider while young (r3 under 16, r2 under 64): smooth at 30 frames, but the blotch phase only
   moves (120: 5.9%). Fibonacci stratification: nothing. Wider filter r2 / every row every frame / both:
   4.5 / 4.7 / 3.7% at 120 (the last at 10.7 ms). Tiles are a budget: 32 px x 32 rays, the same rays as
   16 x 8, halves the frozen noise (2.48 -> 1.19%; 120 frames 4.4%) at ~the same cost, but Sponza's
   accuracy tail goes back up (as shaded p90 1.56 -> 1.97, |err| 0.0058 -> 0.0071; Bistro 0.030 ->
   0.033). Now 32 x 32. Next for noise without losing density: importance sampling toward each probe's
   bright directions. Found on the way and fixed: the
   probe cook integrated diffuse irradiance with 64 samples a texel, which a bright aureole turned into
   +-25% texel noise (straight up, every floor's lookup, read 1.47x high); 4096 now (probe recipe 2). 4a: a bake at load (`ray_surface_bake.comp`, one dispatch per
   material, 16 area-stratified samples per triangle at a footprint-matched mip) gives every triangle a
   word in `BlixRaySurfaces`: albedo (sRGB) and coverage. Traversal meets a partly covered triangle by an
   integer hash of the ray's seed, the entry and its leaf position (`RayTests.Covered`), so the CPU oracle
   flips the same coins and stays bit-exact; hits carry linear albedo. Sponza: 1.35M of 12.8M triangles
   partly covered; the traced view's disagreement at foliage halves (5.45% -> 2.76%). 4b (traced rays into the
   bounds-sized atlas, A/B against the occupancy march) found the grid under-lit Sponza's arcades by a quarter
   (march median 0.73 against the triangle reference, traced 0.98); both the atlas and the march are retired now.
   Open: coloured transmission through leaves (the march had it), and the sky term has no reference yet.
4e. *Surface identity and motion, below every temporal effect* -- DONE (dcab204..fb8c1ce, 2026-10-06). It
   left four separate questions, each with its own answer, where there used to be one heuristic pile:
     OWNERSHIP       the SurfaceKey: source primitive (`PrimitiveSource`, format v20, survives the cook's
                     split) x placement instance, a provenance class -- NOT connectivity (Sponza 35/454 keys,
                     Bistro 43/1,591 span disconnected places, up to 30.9 m). A key mismatch rejects absolutely.
                     If a hostile case needs finer identity, the refinement is better identity (e.g. the cook
                     splitting by connected component), never plane/normal tests again.
     CORRESPONDENCE  where the point was: screen velocity (Rg16F) finds the tile, world motion (Rgba16F, now
                     minus then, from previous transforms) gives the place; rigid or deforming alike, so the key
                     never stands in for a transform. Surface check: velocity 0.002-0.005 px of 0.5-1.5 px on
                     the mover, world motion 0.02-0.05 mm of 1-2 cm, keys 100%.
     SUPPORT         the local radiance-reuse radius (one tile's footprint, measured at where the point was).
                     Locality, not identity.
     STALENESS       has the lighting cached there changed? Unanswered: stage 4f.
   Screen probes went from material + normal + plane + footprint + visibility to key + support (walking,
   against the surface reference: Sponza mean |err| 0.00037 vs 0.00045 with all tests, Bistro 0.173 vs
   0.172, history kept 92-94% vs 81-87%; key alone loses Bistro's mean, so support stays). TAA's surface
   variant (default at one sample) reprojects by velocity, tests the key over a 3x3 (skipped for
   alpha-tested keys), and keeps its count in an R32Uint target.
   Instruments: `--surface-check` (targets against CPU rays, the mover posed as the targets' frame drew it);
   `--mover NAME --mover-pick K --mover-angle D --mover-period S [--mover-hold F]` (frame-time swing,
   deterministic; held = the still answer at a pose); the shot's `.resolved/.incident.f32` + `.mover.pgm`,
   compared mover / 12 px ring / rest against a held run (floor: held vs held, other seed, bias within
   +-1.4%, median ~3.5%). The test case: curtain_03 (`--mover curtain --mover-pick 6`) between identical
   curtains, `--cam 2.1,1.8,2.5,0,0`.
   Bounded dynamics (engine): `RayQueryScene.Instance.Reach` -- a dynamic placement declares the world box
   it may occupy, the top level is built for it, `Move` changes the matrix alone and refuses to leave the
   reach (BV.5b: ray for ray equal to a rebuild). Honest for bounded motion; NOT the answer for unbounded
   motion (something crossing a kilometre): refit, cell migration or rebuild are 4d's to decide.
   Placement: provenance is engine (asset truth); the key table, targets and consumers live in the Sponza
   renderer until a second renderer wants them.
   Declared approximations (bounded, recorded, not to be polished): the incident field's normal-map gradient
   (normal map 2 mips down, factor in [0.5, 1.5]; a real fix needs bindless materials, a visibility buffer, or
   the lit pass reading the probes' SH); TAA's relaxed clamp (x3 on still pixels: ~0.65 points more bias
   against the supersample); under MSAA, TAA keeps camera-only reprojection and its count in the colour's
   alpha (the surface targets are single-sample; no integer-resolve story invented for symmetry).
4f. *Cached lighting and what it depends on* (opened 2026-10-06 by the mover). The sample is unquestionably
   mine; is what it remembers still true? Under the mover (12 deg / 3 s) correspondence holds (95.8% kept,
   key 3.1%, support 0.8%) and the lighting is still badly wrong, incident bias against the held pose
   (curtain | ring): history 256 -35.9 | +59.2%, 32 -31.2 | +37.5%, 1 -0.4 | +5.3% (but median 12%: noise).
   Two stalenesses at once, both with impeccable keys: a probe ON the curtain carries light gathered at
   other poses; a probe NEAR it carries light gathered when the curtain blocked or exposed other paths.
   Rules:
   - Surface motion is not lighting invalidation. A surface can move metres through uniform light and keep
     excellent history; a still one can lose all of it when something two metres away moves. Displacement
     must not become the invalidation rule (it would be the temporal normal/plane test); the displacement
     stat stays a diagnostic.
   - A uniform history cap is not the answer (32 barely helps; only ~1 removes the bias, at 2-3x the noise).
   - Dependency is necessary, not sufficient: a path crossing the reach may not have changed. Its price
     is noise where it fires; measure it, do not hide it.
   4f-i DONE (narrow; the mover is the oracle): a probe ray is DEPENDENT when its gather segment, or its hit's
     way to the sun (cascades or shadow ray alike), crosses the reach of what moved this frame (the mover's
     swept box, passed only while it moves: held or absent, nothing is dependent). Each probe keeps TWO SH
     accumulators -- static paths on the long history, dependent paths on `--dynamic-history N` (default 4)
     -- valid because a still probe's directions through a fixed box are a fixed set, so the parts add to
     the whole; the filter writes the sum. Arms (`--dependency-reset`: a dependent probe drops its whole
     past; `--no-dependency`: as before). Incident bias vs held, curtain | ring | rest (bias / median):
       no split   -36 | +59 | +2.9 / 3.2%       split N=1  +11 | +39 | +6.1 / 6.5%
       split N=4   +3 | +43 | +4.2 / 3.9%       split N=16 -15 | +63 | +5.6 / 3.8%
       whole-probe reset  +83 | +59 | +16 / 21% (the wrong shape: noise everywhere a ray crossed the box)
     So the split fixes the curtain's own light at N=4 for little noise; 46% of traced probes had a path
     through the box (a 4 x 5 x 2 m reach in an arcade is a big dependency). The ring did NOT move, and it is
     not the probes' inputs: clipmap off at probe hits +41%, no clipmap prior for fresh probes +42%. It is
     where the incident pass takes the clipmap's own answer (no same-surface probe covers the recess behind
     the curtain: its tiles' probes sit on the curtain), and the clipmap is a stale cache too: clipmap only,
     no screen probes, ring +26%.
     Harness: runs are not bit-reproducible (the same held run twice: median 1-4%, bias within +-1.5%), so
     controls hold at that floor, not to the bit (old vs new held: within it).
   4f-ii DONE (the clipmap's half): the same split for world probes -- per-texel static and dynamic irradiance in
     two private atlases, both normalised over all of a texel's rays, their sum published to the one every
     reader samples. A probe's dependence is a COUNT (its rays through the reach at its last solve, state bits
     16-23), not a flag: with any ray counting, 6,506 probes were dependent against a budget of 512 (the coarse
     levels see the curtain's box from everywhere). Probes with >= `--clipmap-dependent-rays` (32) are
     re-solved first, after unsolved ones and capped at `--clipmap-dependent-share` (half) of the budget;
     dynamic blend `--clipmap-dynamic-converge` 0.5. Found and fixed on the way: priority sharing the unsolved
     queues starved them (300 frames: level 0 half solved, levels 1-3 never); the round-robin started each
     frame at frame x budget and so skipped whatever the queues displaced (now a persistent cursor).
     The instrument had to change too: at 300 frames the clipmap is unconverged and two held runs differed by
     up to -7.7% (ring); the 300-frame clipmap verdicts above (incl. "clipmap off at hits", "no prior") were
     inside that noise. At 1200 frames the floors are 1-2% (clipmap only) and 0.3-0.5% (full). Converged:
       clipmap only: no dependency ring +26% (real); K=32 ring +4.4%, but rest median 1.6 -> 13.7% (dynamic
       parts re-solved from 64 rays at blend 0.5: the price of dependence is noise, and it is large here)
       full stack, curtain | ring | rest median:   none -32 | +53 | 0.9%    screen split -2.4 | +34 | 2.3%
       + clipmap K=32 -2.5 | +32 | 4.1%   + no clipmap prior for fresh probes -5.4 | +21 | 3.5%
       + no young-probe filter widening -6.7 | +16 | 3.8%
     Controls at 1200: every path dependent (all probes on history 4) still leaves the ring +37%, and no
     clipmap at probe hits +28%: the ring's residual is NOT stale cached light. It is how FRESH probes start
     where the curtain's edge sweeps (each switch is a key change): from the clipmap's coarse answer (prior)
     and with a filter widened across a lighting edge (young widening, 3 tiles) -- support reaching too far.
     The curtain's own probes now match the held pose (mean radiance 0.0081 vs 0.0079).
   4f-ii' DONE fresh probes: a probe whose tile held no probe or one under `--fresh-frames` (4) last frame traces
     `--fresh-passes` (4) passes of 32 rays, counted as that many frames, instead of borrowing: the clipmap prior
     (`--screen-probe-seed`, was 16, now 0) and the young filter widening (`--young-filter`, now off) are the old
     arm. Borrowing was the bias, rays alone change nothing (4 passes with both: ring +32%). Mover, 1200 frames,
     curtain | ring | rest median: default -2.5 | +32 | 4.1%; 4 passes, no prior, no widening -5.8 | +12 | 4.5%;
     8 passes -6.1 | +14 | 4.4% (no gain). After a reset (held, converged) the old arm was biased everywhere: 30
     frames on, rest median 9.0% bias +8.1% p90 83% against 5.0% / -0.3% / 22%; 120 on 5.0 / +4.6 / 32 against
     3.8 / -0.1 / 14. Walking against the surface reference, as shaded: Sponza mean |err| 0.00036 -> 0.00027
     (ratio median 1.59 -> 1.24, p90 5.58 -> 3.65), Bistro 0.173 -> 0.136 (0.88 -> 0.91). Still camera unchanged
     (presented variation median 0.28% both). Cost not resolved: this laptop's screen-probe timings scatter 8.8-15
     ms between comparable arms (8 passes clearly dearer, 24 ms); needs paired A/B.
   4f-ii'' the clipmap's dependent scatter -- measured, kept, not solvable at this budget. Clipmap only, the
     rest of the frame stays at median 12-14% and bias -4..-7% whatever the lever (blend 0.25, share 0.75,
     budget 1024, budget 1024 + blend 0.25), while the ring's bias flips sign arm to arm (+11, -9, -12, -5%):
     LAG, not sampling noise -- ~650 strongly dependent world probes share 256 priority solves a frame and
     each answer is blended over several, i.e. 10+ frames behind lighting that changes ~5x in a 3 s swing.
     The clipmap alone cannot follow this mover at this cost; more rays or slower blends trade lag for noise.
     Full stack, new defaults (floor 0.3%), curtain | ring | rest bias / median:
       no dependency -30.5 | +46.5 | +2.2 / 0.7%     screen split only -5.2 | +18.6 | +0.9 / 2.0%
       + clipmap K=32 (default) -6.8 | +14.5 | -0.4 / 3.7%    K=48 -6.1 | +14.9 | -0.4 / 3.8%
     Kept on: 4 points off the ring and the rest's bias to zero, paid in scatter (median 2.0 -> 3.7%).
   4f-iv DONE: a second probe per tile and rays where light is changing.
     Two layers: a tile's slot 1 takes a second surface (of a 4 x 4 grid, points on another key or off layer
     0's plane by 2% of the depth; the most shared of them), headers name the placed slots, the filter runs over
     slots (`--probe-layers 1` for the A/B). Placed in ~1,300 of 2,040 tiles a frame (keys change across most
     tiles, not only at depth edges). Same build, paired: ring +16.0 -> +13.4% (p90 45 -> 38%), curtain median
     17 -> 15%, cost not measurable (one layer 24.6 / 25.0 ms, two 24.1 / 22.2). Still camera and walking
     reference unchanged (Sponza 0.00030 vs 0.00027 |err| but ratio median 1.24 -> 1.14; Bistro 0.137 vs
     0.136). So the recess falling back to the clipmap was NOT the ring's main cause.
     The ring's residual was the dynamic part's LAG: dynamic history 1 with every row tracing took it +16 ->
     +3.6% (at 26% median scatter). Now a probe whose last trace crossed a moving reach (identity.z) traces
     every frame, not every other row; `--dependent-passes N` adds passes (not worth it: 2 passes 57 ms, 4
     passes 97 ms, noisier at the shorter histories they were paired with). vs held, curtain | ring | rest:
       before -3.3 | +13.4 | +1.0 / 3.4%     every frame, dynamic history 4 -1.4 | +7.9 (median 9.6%) | +0.9 / 3.5%
       history 2 +1.2 | +5.2 | +0.2 / 4.1%  (default stays 4: lower scatter)
     Cost while something moves: probe pass ~24 -> ~37 ms on this laptop (46% of probes dependent, now full
     rate); nothing while nothing moves.
   Where 4f stands: the curtain's own light within ~1-2%, the ring +7.9% (from +53%), the rest unbiased; the
     remaining ring error is the dynamic part's 4-frame history at 32 rays a frame -- a noise/lag trade at a
     ray budget, no longer a missing mechanism.
   4f-iii DONE TAA, isolated: runs without the clipmap or screen probes are deterministic (held vs held 0.00%),
     and the shot now also dumps the image before TAA, so TAA's own share is resolved minus scene. (Not GI-free:
     the default traced bounce volume has its own history, -12% incident on the curtain at 30 deg / 1 s.)
     The key test earns its keep at disocclusions: fast swing ring bias -12.6 -> -6.0%, median 10.0 -> 6.6%,
     p90 43 -> 34%; slow ring p90 11.8 -> 10.8%. Kept. What TAA still adds, with the key test:
       ring, slow: p90 4.3 -> 10.8% (a ghosting tail at the edge the clamp leaves); fast: +0.5 pt bias
       the moving curtain itself: slow -3.4 -> -2.4% bias, fast +0.5 -> +8.3% -- TAA's own staleness: history
       correctly owned by a surface whose shading changes as it turns (n.l, its own shadow).
     TAA's 4f, DONE: a pixel depends on the mover when its key carries DynamicSurface (0x40000000, set at load
     for placements declared dynamic -- the key states that its own pose changes its shading, nothing inferred
     from displacement) or its way to the sun crosses the moving reach (a shadow receiver); it accumulates up to
     `--taa-dynamic-count` (4; `--taa-no-dependency` the A/B). Deterministic (held runs bit-identical before
     and after). Resolved vs held, curtain median / bias | ring median / bias | rest p90:
       slow  off 6.0 / -2.4 | 1.44 / -1.2 | 1.28%     count 4 5.0 / -3.4 (= before TAA) | 1.30 / -0.3 | 1.69%
       fast  off 28.2 / +8.3 | 6.6 / -6.0 | 4.3%      count 4 27.9 / +7.7 | 7.8 / -5.6 | 6.2%   count 2 +5.0 bias
     Slow: TAA adds no bias on the curtain any more, for a noisier tail where shadow receivers run short. Fast:
     even 2 frames leave +4.5 points -- at ~3 deg a frame one frame of history is a different orientation's
     shading; only knowing HOW the shading changed (not that it did) would remove it. Not pursued.
   Only after 4f-i proves the shape: representations that scale past one mover (change epochs per reach,
   coarse spatial dirty fields, dependency hashes, reach IDs).
F. *The frame audit* (opened 2026-10-07: "a lot of pure performance on the table"). Structural first, then the
   instrument, then hot spots. Isolated per-pass GPU ms (`--gpu-isolate`; totals across sessions are NOT comparable
   on this laptop -- the same lit pass isolated 15.2 ms one morning, 21.4 the next afternoon).
   Baseline (2026-10-07): default sky-inject 22.2, lit 15.2, depth-prepass 8.9, late 4.0, incident 3.8, resolve
   1.4, gtao + denoise 2.4, 12 pyramid passes ~5; clipmap + screen probes: screen-probes 27.0, sky-inject 26.7
   (feeding nothing with fog off), lit 17.3, incident 8.8, late 5.5, clipmap 3.3.
   F2 DONE (5bc3224) dead work: the bounce atlas only while read; the incident field fixed at full resolution
     (scale, resolve pass and targets gone; the resolve was NOT an identity at scale 1 -- its rounding mixed
     neighbours across edges on ~14% of pixels); world motion only when read; GTAO writes visibility alone to
     R16F (engine format; its bent normal shaded nothing). Default config bit-identical apart from the resolve.
   F3 DONE GI on the clipmap: the fog reads it; the bounds-sized atlas, sky_inject (march + traced), probe_usage,
     the cooked sky-visibility / occupancy / albedo volumes, incident.frag, the probe display, the bounds
     references and censuses are deleted (29 files, -3,033 lines); scene bounds come from placements (2% pad,
     with the mover's reach). Walking vs the surface reference, total indirect |err|: Bistro bounds 0.405 ->
     clipmap 0.153, Sponza 0.0111 -> 0.0011. As shaded: Sponza clipmap 1.71 ratio / 0.00040 (+ screen probes
     1.13 / 0.00030), Bistro 0.88 / 0.181 (0.91 / 0.137). Glass reflections are unoccluded now (no baked sky
     visibility), diffuse transmission's back side takes the clipmap's. Engine gap found: the Vulkan->engine
     format map lacked R16F/R32Uint/Rg16F, so readbacks of R16F read garbage (test BX.1 now holds every colour
     format to the round trip). Cost: the 22-27 ms injection is gone; the clipmap's incident pass is now 11.6-12.4
     ms (three clipmap samples a pixel for the gradient, up to 8 screen probes) -- a step 4/5 target.
     Left on the asset side, unused by this demo: SkyVisibilityBaker, BlixSkyVolume, .blixsky in the cook script.
   F4 DONE duplicated work: the clipmap's injection takes the sun from the cascades where they cover a hit (its
     pass now after them), a shadow ray only beyond (`--clipmap-shadow-rays` the A/B). Paired, same session:
     probe-clipmap 5.1 / 6.1 -> 3.8 / 3.8 ms; total indirect |err| Bistro 0.1521 -> 0.1519, Sponza 0.00113 ->
     0.00122 (+8%, floor unmeasured). Looked at and moved to F5 (need the instrument, or are not duplication):
     the two Hi-Z pyramids read DIFFERENT depths (early for occlusion, final for GTAO) -- what is wasted is
     both channels in each (each reader wants one) and 12 passes of mostly fixed cost (0.37-0.5 ms each
     isolated, whatever the level): a single-dispatch downsample is the win; the late pre-pass loads and stores
     five attachments even when its list is empty (needs the count on the GPU, or a cheaper attachment set);
     six previous-frame cameras (hygiene, no measurable cost).
   F1 DONE the instrument: the pass breakdown is the window since load (the device's totals are cumulative by
     design; the demo never took the snapshot, so it printed lifetime means with ~1,000 loading frames in them),
     with per-run, per-frame and runs-per-frame columns and a TOTAL per frame; the engine's timestamp slot holds
     128 passes (was 32: a 33-pass frame lost its tail in silence) and says once if a frame runs out; the frame
     period after a --stability readback (a GPU wait) is left out of the frame-time statistics. Not fixed,
     because it never fires: a NotReady timestamp result surviving into the next reset (0 of 878 run logs).
     Normal (non-isolated) per-pass times are only meaningful for compute passes on MoltenVK (render passes
     bracket encoders); --gpu-isolate remains the per-pass truth for everything.
     First reading with it: screen probes cost 41 ms a frame while the mover swings (46% of probes dependent and
     tracing every frame, plus fresh-probe passes), 27 ms isolated when still.
   F5 (in progress). Decomposition first (isolated, screen probes on, still): screen probes ~36 ms of which
     ~32 is traversal (no tracing at all: 4.0; no sun at hits: no change, the cascades answer; no clipmap at hits:
     -3.5; one layer: -10); the incident pass ~11 ms of which the gradient is ~1 -- the rest is the clipmap's
     per-pixel sample (8 probes x 5 lookups x a manual 4-tap bilinear, sometimes over two levels).
     Ray length (rejected as a default; `--probe-ray-length M` kept, 0 = the whole way): a ray stopped at M
     and took the clipmap's light at its end looking onward. 2 / 4 / 8 m: probe pass 33.5 -> 22.2 / 26.3 / 31.2
     ms, but Bistro walking |err| 0.137 -> 0.233 / 0.267 / 0.312 (WORSE with length: the end lands in the
     clipmap's coarse, leaky levels) and the user saw lighting jump at distance in Sponza's courtyard even at
     8 m. The far field must be fit to hand off to first: radiance (not irradiance) in the world probes, and
     coarse levels that do not leak.
     DONE the second layer opens only at a depth discontinuity (off layer 0's plane by 2% of the depth; a
     different key alone no longer counts -- `--probe-layer-keys` the A/B), its candidates scored in shared
     memory, one per lane (they were 16-element arrays in every lane): probe pass 36.8 -> 22.1 ms, Sponza
     |err| 0.00031 -> 0.00030, Bistro 0.137 -> 0.134, mover ring +6.5 -> +2.8%.
     DONE filtered clipmap fetches: readers holding a sampler (incident, screen probes, fog) take each tile
     lookup as one hardware-bilinear fetch (the border ring makes it safe), the injection keeps the manual
     path for its storage images (probe_clipmap.glsl's optional BLIX_CLIPMAP_*_FILTERED). Interleaved A/B
     builds: incident 12.6 / 12.2 -> 9.8 / 9.3 ms; accuracy and stability unchanged. (Timing drifts a lot
     within a session -- the probe pass read 22 then 38-40 ms an hour apart -- so only interleaved arms count.)
   Traversal, T0 (GPU ray bench, closest hit, with counting): grid points in open air, Sponza 14.7 M rays/s, 45
     nodes / 3.5 entries / 5 triangles a ray; a NEW `--ray-bench surface` batch (camera-visible surface points,
     lifted as screen probes lift them, 32 hemisphere directions): 5.4 M rays/s, 106 nodes / 7.0 entries / 12.6
     triangles (Bistro 6.5 M, 100 / 1.7 / 40.7). Screen probes trace near the SURFACE rate (~77k rays in ~18 ms of
     traversal): the kernel was never the problem. Split anyway (place / trace / integrate, three passes, one body
     screen_probe_kernel.glsl): interleaved, fused 23.9 / 26.7 ms vs split 26.5 / 22.1 -- no gain, kept for the
     per-stage timing that found this and the reduction no longer in one lane. Coarser ray geometry (T2 as a plain
     LOD, `--ray-lod-error`): 8.0 -> 6.5 -> 4.6 M triangles at 2 / 5 / 10 cm changed nothing (5.6 / 5.3 / 5.1 M
     rays/s, ~105 nodes) and moved the image (rest median 1.0 / 1.8%). The cost is the hierarchies a surface ray
     enters (7 overlapping regions, each descended from its root) and overlap inside each (Bistro's long thin
     triangles). T1 proposed: regions that do not overlap (triangles clipped at the cells, referenced from both),
     then spatial splits (SBVH) in each hierarchy; first the coverage hash must key on a stable triangle id, not a
     leaf position, or a duplicated alpha triangle flips its coin twice.
   T1 DONE (d87f5ce, b19f939, this): (1) a partly covered triangle's coin is keyed on the triangle (placement,
     mesh index), not where a hierarchy lists it; (2) region cells hold references -- a straddling triangle in
     both halves, each copy's box clipped -- so regions stop overlapping: Sponza surface rays 5.4 -> 7.3 M/s,
     entries 7.0 -> 2.2, nodes 106 -> 73, +1.3% references; (3) spatial splits in each hierarchy (simplified
     SBVH: cost by box-against-slab binning, the exact polygon clip for the references a cut splits; only where
     the object split's children overlap by > 1e-3 of the root's area; references capped at +30%, a cut taken
     only if the budget covers every reference it crosses): Bistro surface rays 6.2 -> 10.2-10.8 M/s, nodes 87
     -> 57, triangles 35 -> 20; Sponza nodes 73 -> 64, rate within its scatter. Bistro's screen-probe trace,
     interleaved: 26.5 / 18.2 -> 17.5 / 14.8 ms (the clipmap 7.0 / 5.0 -> 5.2 / 4.4). Cost: the ray scene's
     build at load, Sponza 7.4 -> 12-19 s (exact clipping in the binning was 60 s): the case for building the
     hierarchies once, in the cook. GPU against CPU unchanged: the same 3 / 6 rays disagree, as before T1
     (long-distance and edge-midpoint hits; to examine). Tests BV.6 restated, BV.8 new.
   Next in F5 after that: traversal layout (a wider BVH, compressed nodes, ray order: engine work in Blix.Geometry's
     packing and ray_query.glsl, every ray consumer gains), then the raster side (lit 15-22, pre-pass 9-12,
     late 4-6 ms), the pyramids' passes, barriers around buffer dispatches, CPU allocations. Old list: the clipmap's incident pass (11.6-12.4 ms: three clipmap
     samples a pixel for the gradient, up to 8 screen probes), screen-probe register pressure (the second
     layer's 16-point candidate arrays in every lane) and lane-0 SH reduction, the pyramids, barriers around
     buffer dispatches, CPU allocations.
5. *PRT baked by the cook*: per-region probe transfer, relit by the sun at runtime: the static base and
   warm start of 4.

Known instrument gap: a Sponza `--shot` raises 9 validation errors on `main` as well (a storage image
copied to a buffer without transfer-source usage, in the shot's readback). Not this arc's, and not fixed.
And the froxel fog animates on wall-clock time (`uFogParams.w = time.Total`), so two arms with different
frame rates reach a `--shot-frames` frame with different fog: 0.2 mean against a 0.04 floor at city 64x64,
gone with `--no-fog`. Compare arms that differ in speed with the fog off.
The first ~250 frames after load run slow and then settle: Bistro 25-34 ms, then 15-18 ms, with or
without the sky bounce (cause unproven; the GPU clock ramping under sustained load is the suspect). A
120-frame shot measures the cold regime, which is how an `--ab lod` on Bistro read 4.9 ms and then sign-
flipped. Time at `--shot-frames=1500` (each arm keeps its last 600 frames) and compare the quartiles.
`BLIX_CONFIG=Release ./blix run` ran a binary older than the Release build beside it, without saying so
(found by a node count that did not move); measure Release by exec'ing the apphost.
The scene host also ignores its `[Tune]` fields on the command line (`--lod-error-pixels` is reported
unread): three field names collide across its settings objects, so applying them is a naming decision.
The Bistro orbit at `--shot-frames=120` has two outcomes in either arm (3,931 pixels apart, max 74), so a
Bistro orbit A/B needs several runs per arm before a difference is one.

Huge also means camera-relative rendering and streaming by region (cluster geometry and BLAS per region,
the top level over what is loaded); both land where the stage that needs them does.

---

## Open decisions, and the arcs after them

**Every format through the cook, as glTF now is (decided; one format at a time).** The engine reads no
raw asset: each format gets a standard cook pipeline, and its source reader becomes the cook's. Ergonomics
of cooking may get better later; the split (a runtime source path beside the cooked one) does not come
back. Still read raw today: OBJ (`ObjImporter`, which nothing outside the tests uses, and `WavefrontParts`,
which the Check tool uses; no recipe cooks an OBJ), fonts (`FontImporter` rasterizes the TTF when no
`.blixfont` exists), materials (`MaterialImporter`), audio (`WavImporter`), images (`TextureImporter`, and
`CookedMaterials` decoding a source image when a cooked mesh's `.blixtex` is missing). These source readers
still throw `FileNotFoundException`, unlike the cooked readers' `AssetImportException`; each format's arc
closes that gap for it.

**Later, as their own arcs, when something asks:** morph deformation (targets plus a `weights` clip
channel; today refused where they take effect, `MeshRecipe.OpenSource`), then `KHR_animation_pointer` (not
forced into skeletal animation).

---

## Structure: three findings, open

**1. The getting-started game renders through a tool, and cooks at launch.** `hello-blix-3d` draws with
`Blix.Tools.Studio.StudioRenderer`, which `renderer.md` calls an optional reference for model and rig inspection
while saying there is no engine renderer. Studio is the de facto default: hello-3d, View, Shot and Studio all
stand on it. Through it the game's runtime reaches `Blix.Recipes` (SharpGLTF, BCnEncoder, the meshopt, MikkTSpace
and BC7 natives), ships a raw `Rogue.glb`, and cooks it on launch, so "the engine reads no raw asset" holds for
the engine and not for the first program a newcomer runs. Section BT does not see it: the example references the
engine as `$(BlixSourceRoot)/…`, the scan resolves no such path, and its closure comes back empty. The open
decision: promote a renderer out of the tools (four consumers is the evidence; it reverses "no engine renderer"),
or have the example own its pipeline and cook at build time as the demos do. BT's MSBuild-property blind spot is
a fix either way.

**2. Reading cooked formats is spread over four assemblies, and cook work sits in the runtime.** `Blix.Cooked`
holds the preamble, stamp and refusal type; `Blix.Assets` the `.blixmesh` and `.blixfont` readers beside the
source importers (StbTrueType, WAV, OBJ, materials) and `AssetDatabase`; `Blix.Graphics.Images` the `.blixtex`
and `.blixprobe` readers beside Stb image decode, environment conversion and probe baking; `Blix` the model
reader (`ModelData`, `CookedMaterials`, `CookedVertices`, `CookedSamplers`). The format arc above ends naturally
in one runtime layer of cooked readers, with every decoder and baker behind the cook, and BT's rule extended from
SharpGLTF to StbImage, StbTrueType and the rest.

**3. `Blix` is one flat namespace of about sixty files.** Animation is about twenty-two of them and the largest
coherent subsystem; beside it are the kinematic physics hosts and collision worlds, cameras and controllers,
lights, transforms, the model reader and residency, and audio sources. Not a case for more assemblies, which would
be predicting; an animation namespace would help finding things.

---

## F — a body in the character room

The one stage not started. `src/Demos/Character/` holds a shared library — room,
motor, camera, renderer, resolver — with `room` (walk it), `room-shot` (capture it)
and a probe (87 assertions, headless) over it.

**The prerequisite nothing listed: there is no body.** The room draws a capsule and
has zero references to `ClipPlayer`, `AnimationClip` or `Pose`. Every consumer of
`BoneMask` and `RootMotion` in the tree is a tool, a test or the preview tool — none
is a character that moves. So "contact drives weights" is not two existing things
being wired together.

**It is a sibling executable**, `Blix.Demos.Character.Body`, over the same library.
That is what the collection's shape is for, and it keeps `room` answering the
question it already answers. The body takes the room's geometry and motor from the
library; what it adds is a rig on top of the capsule.

**First observable, and the whole of the first step:** a skinned Rogue standing in
the room, facing the right way, feet on the floor. Nothing blended, nothing masked.
The unknown is the rig fit against a 0.35 m radius, 1.8 m capsule, and it should
surface on its own rather than tangled in a blending design.

Then, and only once a body walks:

**Contact drives weights, and there are no states.** Speed and groundedness come out
of the resolver and drive blend weights directly — no states, no transitions, no
dwell timers. `BoneMask`'s first consumer that is not a tool: locomotion on the legs,
a one-shot on the upper body. The Rogue carries what this needs — `Walking_A/B/C`,
`Running_A/B`, `Idle`, `Unarmed_Melee_Attack_Punch_A/B`.

*Negative control:* the mask set to `All` must visibly break the legs, and set to
`None` must leave the walk bit-for-bit unchanged. A layer that changes nothing and a
layer that changes everything are a mask's two failures, and both are invisible
unless asked for.

**And then the clip drives the contact.** Root motion says how far, the room says
where you can go. Honest expectation: 4 travelling clips of 76, so this may end a
**decided-no**, and reaching that with the instrument built is the output.

**Acceptance is from the chair.** Walk the room: ramps, stairs, ledges, walls. Every
stage of this arc had at least one fault only a person watching could see, and four
of five were **legibility, not mechanism**.

---

## Acceptance steps that are access, not work

These keep this record open. None can be closed by writing anything.

**No clean Mac has run a published bundle.** Every stage-B measurement was taken on
the machine that built it. "Zero images mapped from `/opt/homebrew`" is the strongest
available proxy and is not the test. Take a `.app` to a Mac with no Homebrew, no
.NET, no Vulkan SDK and no OpenAL, and launch it.

**A `win-x64` publish ships no OpenAL native.** `vulkan-1.dll` is correctly absent —
the GPU driver installs it — but OpenAL Soft is neither a system library nor in a
NuGet runtime pack. Stage B's closure question, unanswered for Windows.

**No physical gamepad has been held.** The lifetime, the trigger range and
re-acquisition are proven against the state machine; the Silk seam is not.
Specifically unverified: that thumbstick index 0/1 is left/right on a real device,
that a resting trigger reads 0.00 rather than 1.00 — the direct observable of the
range fix — and that holding a button while alt-tabbing back does not fire it.
`blix run Chassis --frames 900` prints the lifetime lines; plug, unplug, and
switch away holding something.

**`blix.cmd` is only as verified as CI exercises it**, and the Windows branch of the
native build targets has only ever run on a GitHub runner.

---

## Known gaps that are not scheduled

**The `demos` gate is not in CI, and now cannot be as it stands.** Its headed legs run each demo
for 45 frames under `--validate`, which needs a GPU and the validation layers, and CI runners have
neither. The root gate is the deviceless one. A CI home for the `demos` gate would first need its
deviceless legs (the probe, `chassis-tune --headless`) separable from the headed ones, which is
what named tiers are for.

**Linux is absent from the CI matrix.** A second red job teaches nothing the first
has not; the shape of what Windows needed should be known before it is copied.

**A skin that mirrors only some of its joints within one primitive** draws with one front face for the
whole draw, chosen from the first weighted joint's palette. No asset or consumer has it, and there is
no single correct front face for such a draw.

**What each renderer draws of a material** is the table in [`docs/renderer.md`](docs/renderer.md)
("glTF materials: read versus drawn"): Studio ignores eleven `KHR_materials_*` extensions, `unlit`
among them, so an unlit material is drawn lit. Each row closes when something asks for it.

---

## Parked

**The RTS moves to this engine on its next pin bump**, and these break for it. From PR #42:
`vk.LastCpuFrameTiming` and the tuple `vk.GpuPassTotals` are gone (read `host.Timing`); submitted work is
literal (a zero-instance draw counts zero, a negative count throws); a `uniform sampler` with no
`//@sampler` on the line before is a build error; `blix test <word>` reads a leading word as a project;
`RenderGraph`, its handles and builders, `TextureView`, `ShaderInterface`, `ShaderReflection` and
`UniformBlockLayout` are in `Blix.Graphics` (take `IGraphicsDevice`; the casts compile but nothing needs
them). From PRs #44-#51: `ModelData`/`Model` (and `BlixMeshFile.Flat`), `.blixcook`, `SkeletonPlacement`,
a format v18 re-cook, `CreateMaterial(arrayLengths:)` in element counts (was bytes), `WithArrayLength`,
`DestroyIndirectBuffer`, `Transformed` refusing skinned or unknown layouts, `PropModel.Dispose` freeing its
meshes, and `OnUnload` teardowns (`--validate` fails on a leftover). From §J (#52): the GameObject family,
`IAnimated` and the clip animations are gone; `IAnimation.Advance(delta)` replaces `Sample(Time)`;
`IRenderHost.ResetFixedClock` (an implementer must add it), `IFixedGameLoop`, and `Game`'s fixed step owned
by the host with no step cap. From §K (#53, #54, #56): `Bone(Name, ParentIndex, Rest, Offset)` with `Rest`
required and no inverse bind; a palette is `SkinBinding.ComputePalette(BoneWorlds, …)`
(`Skeleton.ComputeBonePalette` is gone); `BonePaletteSet(SkinBinding, capacity)` with `Add(worlds, post)`;
`JointCount` for `BoneCount` on `BonePalette`, `BonePaletteSet` and `BoneBuffers`; `ModelData.Skin(Binding,
JointNodes, Placement)` and `Model.Skins` as bindings; `FindWeightedJoints`; attachments' `BoneName`/
`BoneIndex` and `CarrierNode`; masks validated against their skeleton; recipe 18 (a re-cook); and a
skinned load with `Colour` gets the 92-byte vertex, so its pipeline must declare that layout. From this
arc: `Blix.Import` is gone — `GltfImporter`, `GltfStaticImporter` and the `Gltf*` types they returned — so a
glTF opens by cooking (`CookCache.Resolve`, then `ModelData.Load`); `AssetImportContext` takes an id and a
path only.

---

## Deferred until something asks

Probe density, `BakeMerge`, clip-lookup-by-name, further `PropModel` adoption.

**Asset manifests** (finding assets by logical name, declared external asset roots). Every
in-repo program finds its assets through `AppFiles` and the importer's cooked-sibling lookup,
and Sponza's external pack set is one required variable. The hand-run preparation steps do not
fit either: EXR conversion needs Blender, and the sky bake is a scene-level driver by design.
Each waits for a consumer, which is conventions §5 and §8, and waiting does not keep
a plan record open. The probe-density measurements and the approaches already refuted
are in [`docs/renderer.md`](docs/renderer.md) so the same ground is not walked twice.
