# Blix's house style

The engine has no opinion about how anything looks, and that is correct. This arc gives the
opinions a **home** instead of leaving them scattered or absent.

## The idea

> The engine owns the mechanism. The caller declares the policy. — conventions §6

§6 only ever says where policy must **not** live. It pushes taste outward and abandons it, so every
consumer re-answers "how bright is the sun" from scratch. This arc gives it a destination:

```
Blix capabilities
    ↓
shared shader/math vocabulary (Blix.Shaders, Blix.Graphics.Images)
    ↓
StudioLook — the composition, and the numbers
    ↓
Blix's reference look, which `view` takes without being asked
```

rather than a `DefaultRenderer` in core that everyone then fights.

**Studio is where Blix gets to have taste.** Core says "here are buffers, graphs, images, rigs,
materials, reflection". Studio says "here is what we would do with them". A game takes none of it,
30% of it, or all of it. The word is **house style** or **reference look**, never "blessed" — the
latter implies deviation needs permission, and it does not.

**The technique never moves into Studio.** A capability belongs to the engine and its shading
vocabulary to `Blix.Shaders`; what Studio owns is *which of them are on, and at what settings*. The
NdotV fix going into shared `pbr.glsl` rather than into the studio shader is the precedent.

**Floor or ceiling — decided: ceiling.** The reference look absorbs IBL, cascades, MSAA and a depth
pre-pass from VulkanLit and Sponza. A reference out-rendered by every demo answers its own question
badly. Labs then extend it instead of rebuilding scaffolding, and genuinely forward-facing work
(transparency ordering, batching experiments) starts from a good picture rather than a bare one.

## Decisions taken up front

1. **Structural settings are construction-time**, not live. A sample count, a cascade count, whether
   a pre-pass exists: each is baked into pipelines and targets, and a slider on one changes nothing.
   `[Tune(Structural = true)]` says so — the flag still works on the command line, which runs before
   anything is built, and the panel shows it among the **values** rather than the controls.
2. **The renderer owns its look** and exposes it. The window for a structural setting is between
   constructing the renderer and the first frame — the same window rung four's `extend` hook uses.
3. **The environment probe is structural too.** Baked once, from the sun as it stood at startup. The
   sun slider stays live for direct light, so the two can disagree; that is accepted rather than
   solved, and made *visible* by printing the baked sun beside the live one.
4. **Deferred, deliberately:** a general mechanism to relaunch the app with the current values, which
   is the honest fix for (3) and for every structural knob. Noted so the absence is a decision.

## Stage 1 — `StudioLook`, and `Structural` — DONE

`StudioLook` holds the eight numbers that were already the house style and had no name:

```
SunAzimuth 52.125°   SunElevation 54.526°   SunIntensity 1.0   AmbientStrength 0.06
ShadowExtent 9m      Ground true            Exposure 1.0       TonemapMode ACES
```

The defaults of the type **are** the house style; `new StudioLook()` is what Blix thinks a model
should look like, and `Blix.Test.Studio` asserts a fresh one equals the stage's, so the renderer
cannot start adjusting it on the way past.

**A comment argued against this type, and I wrote it.** Its claim was that gathering these dissolves
once they are sorted by what READS them — sun and ambient to the lit pass, shadow extent to the
caster, exposure and tonemap to present. That is true and it answers a different question. Sorted by
who **decides** them they are plainly one artifact: one person answers all eight in one sitting
looking at one picture. `TuneAttribute.Group` carries the by-pass grouping into the panel, so the
by-author grouping can own the type and nothing is lost. RTSGame's `LookSettings` reached the same
shape independently, spanning the same passes, and did not dissolve either.

### Four things the verification found

- **The empty stage honoured no look flag at all.** `ApplyStageKnobs()` was called on the model path
  and the rig path, so with neither `--model` nor `--rig` every flag parsed correctly and reached a
  renderer nobody had asked to re-read it. Now called once, unconditionally. The stage has a look
  whether or not anything is standing on it.
- **`--tonemap-mode` never reached a capture.** The read-back path hardcodes ACES. Porting the other
  three curves to C# would be three more copies of a shader able to drift silently — the existing
  comment is already the standing warning about the first copy — so the tool now **says so** instead
  of writing a picture that quietly disagrees with the screen. The real fix belongs with the arc:
  the capture is what certifies the house style, so its tonemap must eventually be generated from,
  or tested against, `blix_tonemap`.
- **`Blix.Test.Studio` was red at HEAD**, 12/13, and had been. It is a FOURTH suite and the
  verification note listed three, so it was never run. The failure was a stale program pairing:
  the test paired `studio_skinned_shadow.vert` with `studio_shadow.frag` after the skinned caster
  got a fragment stage of its own, and a vertex and fragment stage declaring different push blocks
  reflect the SUM — 80 bytes against a 16-byte payload, reading as a renderer bug. The renderer was
  right the whole time. Now 15/15.
- **An unrecognised argument is silently ignored** rather than refused. Not fixed here; it is the
  reason a malformed flag is indistinguishable from a dead one, which is how the next item happened.

### And one false alarm, recorded because the instrument was the fault

I reported the look flags as dead, "pre-existing, confirmed at HEAD". **They were never dead.** The
test loop passed `$a` unquoted, and **zsh does not word-split unquoted parameter expansions** — so
`--exposure 2.5` arrived as ONE argument, was not recognised, and was skipped. The HEAD comparison
had the same defect, which is why it agreed. Hand-expanded, five of seven flags move the picture
immediately; `${=a}` fixes the loop.

Two lessons, both about the instrument rather than the engine: **in zsh, write `${=var}` or use an
array**, and **a hash comparison across runs that share one output path proves nothing** — the first
run of that loop wrote the file and every failing run after it left the file alone, so everything
matched.

### Verified

`Blix.Test.Graphics` 649/649 · `Blix.Test.Diagnostics` 235/235 · `Blix.Test.Physics2D` 43/43 ·
`Blix.Test.Studio` 15/15. **Lab baseline: 68 artifacts byte-for-byte identical** — this stage moves
no pixel, which is the whole claim it had to support.

## Stage 2 — IBL — DONE

`studio_lit.frag`'s flat ambient — `albedo * ambientStrength`, which lit every surface identically
whatever it faced — is now the split-sum image-based term, from a probe baked out of the house
style's **own sun**. No environment asset: `ProceduralEnvironmentSource(SunDirection)` means a tool
that must open any model on any machine carries its own sky.

### What actually moved, which was not what was planned

I said `ibl.glsl` would be promoted from the VulkanLit demo and VulkanLit switched to the shared
copy. Once read, both halves were wrong:

- **Its `fresnelSchlickRoughness` is already in `Blix.Shaders` as `blix_fresnelLazarov`** — better
  documented, with an Apple-driver fix, and identical in output for clamped `NdotV` (the clamp is
  the only textual difference). There were THREE copies: pbr.glsl's, VulkanLit's, and Sponza's own
  in `lit.frag`. The demo file was a duplicate.
- **Its only other content is `const float MAX_REFLECTION_LOD = 6.0`** — a GLSL constant that must
  equal (prefilter mip count − 1) from a C# bake. Sponza already passes that as a uniform. Promoting
  it would have promoted a landmine.
- **VulkanLit's lit path has a complete parallel local library** — `brdf.glsl`, `shadows.glsl`,
  `normal_mapping.glsl`, `debug_channels.glsl`. Swapping one of five leaves it half-converted, which
  is worse than either end state. Its conversion is its own job.

So: **`Blix.Shaders/ibl.glsl` is new vocabulary, not a promotion.** It carries `blix_iblAmbient`,
built on `blix_fresnelLazarov`, and takes the prefilter ceiling as a PARAMETER supplied by the bake.
VulkanLit and Sponza are untouched.

### Three faults, all mine, all found by verification rather than by reading

1. **The first structural setting shipped unable to be set.** Both tools called `renderer.Load` —
   which bakes — BEFORE applying the command line, so `--image-based-lighting false` parsed, assigned
   and changed nothing. The only symptom was byte-identical captures with the flag on and off.
   Fixed by applying the look's flags before `Load` in both tools, and `StudioLook.SealStructural()`
   now makes the next one loud: cascades, MSAA and a depth pre-pass are all structural, and each is
   another chance to make this mistake somewhere the picture looks plausible either way.
2. **A double free in teardown, intermittent, exit 139.** `EnvironmentBaker`'s procedural path
   returns ONE cube assigned to `EnvCubemap`, `DiffuseIrradiance` AND `PrefilteredSpecular` alike, so
   destroying "the irradiance" and "the prefiltered env" destroys the same texture twice. It crashed
   after the frame was captured and the PNG was on disk, so the run looked successful until the
   process died — caught only because the baseline records exit codes. The renderer now owns a
   deduplicated list rather than reasoning about which fields alias today.
3. **Every lit draw must bind every texture the shader declares**, and five views were assembling
   that array by hand as `{ draw.Textures[0], uAlbedo }` — correct at one texture, one short at four.
   `StudioDraw.WithAlbedo` now owns the assembly, so adding a texture to the stage is one edit.

### The BRDF LUT was 96% of the bake

Measured, because the engine's comment says procedural skies are "cheap enough to bake every frame"
and the first bake took 1.9 s. The comment is right about the probe and wrong about the total:

| | cost |
| --- | --- |
| procedural probe | **35–71 ms** |
| BRDF LUT @ 256 (engine default) | **1451 ms** |

The LUT is O(n²) — 32 → 25 ms, 64 → 84 ms, 128 → 407 ms, 256 → 1451 ms — and it depends on nothing
but its own size: it is the Karis split-sum integration over (NdotV, roughness). Recomputing a table
of constants at every launch of a tool you launch constantly is the wrong trade, so `BrdfLutSize` is
a declared structural value defaulting to **64**, and the choice was CHECKED rather than asserted:
against 256 it differs on 0.11% of pixels with a **maximum difference of 1/255**. Even 32 stays
within 2/255.

**The real fix is a cooked LUT, not a smaller one** — same numbers on every machine forever, and
`Blix.Tools.Cook` already writes a BRDF LUT into a `.blixprobe`. Engine work, not this arc's.

### One quality caveat, recorded rather than hidden

The procedural probe is **not a true GGX prefilter**: the baker assigns the same cube to all three
probe fields, so the specular half reads box-filtered mips of the env cube rather than roughness-
convolved ones. The engine's own field comment says as much ("fallback, when PrefilteredSpecular
isn't a real GGX bake"). Good enough for a stage whose job is legibility; worth knowing before the
house style claims to be a reference for material authoring.

### And a taste question the change raises

`AmbientStrength` defaults to **0.06**, tuned when the fill was flat and its only job was to stop
shadowed faces going black. With a directional fill at 0.06 the IBL is nearly invisible — on/off is
a seam you have to look for. At ~0.35 the shaded side of a face reads and the ground picks up sky.
That is a house-style decision and it is the user's, not a bug.

### Verified

`Blix.Test.Graphics` 649/649 · `Blix.Test.Diagnostics` 235/235 · `Blix.Test.Physics2D` 43/43 ·
`Blix.Test.Studio` 15/15. Validation clean. Baseline re-recorded deliberately: **46 of 68 artifacts
changed, all 23 modes still behaving** — negatives still refuse, the self-test still passes, the
lockstep control still reports one distinct pose — and a second run is byte-identical again.

## Stage 3 — cascaded shadows — DONE

Three cascades, and **not** the ones the textbook describes.

### What the engine gained

- `GraphicsMatrices.FrustumSliceCorners` / `FitCascadeViewProjection` / `CascadeSplits` — the
  frustum-slice fit (bounding sphere, ceiling, texel-snapped **on the light's own axes**) and the
  practical log/uniform split blend. Lifted from the algorithm RTSGame proved, which still carries
  its own copy: converting it is a job of its own and not one to do while it is working.
- `Blix.Shaders/shadow.glsl` gained `blix_sun_shadow_cascaded`, `blix_cascade_contains` and
  `blix_cascade_tint`. **Selection only** — the sampling is the existing `blix_sun_shadow_soft`.
  There are already three PCF implementations in this tree; the way to stop there being a fourth is
  for the cascade layer not to need one.

Three cascades, fixed. That is portability, not preference: indexing a `sampler2D` array at a
computed index needs `shaderSampledImageArrayDynamicIndexing`, which is not guaranteed, so every
implementation that works everywhere branches on a constant index. Making the count a `#define`
variant would make it the first axis of a permutation matrix, for a number nobody has wanted to
change.

### The textbook fit was measurably wrong here, and the numbers said so

Fitted to view-frustum slices — what every CSM tutorial does — the subject landed in a cascade
**coarser than the single box the cascades replaced**, at every split ratio tried:

| λ | cascade holding the subject | mm/texel | vs the old 18 m box (8.8) |
| --- | --- | --- | --- |
| 0 | 1 | 24.4 | 2.8x worse |
| 0.3 | 1 | 19.5 | 2.2x worse |
| 0.5 | 1 | 15.6 | 1.8x worse |
| 0.85 | 2 | 36.1 | 4.1x worse |

The cause is structural. A frustum-slice fit has to bound the **whole width of the frustum** at that
depth, and this stage's camera orbits its subject from eleven metres out, where the frustum is about
twenty metres wide. The old box was 18 m because it was fitted around the ORIGIN. RTSGame wrote this
down from the other direction — *"the cascades split this, not the frustum, and the difference is
two wasted cascades... it assumes a camera standing among the things it looks at"* — and I walked
into it one stage over.

**So the boxes are concentric on the stage's content and the shader selects by CONTAINMENT.** A
turntable knows where its content is; a fit that ignores that spends resolution on the space between
the camera and the thing.

| | box | mm/texel |
| --- | --- | --- |
| cascade 0 (the subject) | 10.2 m | **4.97** |
| cascade 1 | 17.7 m | 8.63 |
| cascade 2 | 30.0 m | 14.65 |

Against the single 18 m box at 8.8 mm/texel: the subject is **1.8x sharper** and shadow coverage
grows from an 18 m box to a 30 m radius. Cost: the casters are drawn three times. Affordable on a
stage that draws a handful of objects, and the first thing to look at if it ever gets a scene.

### One bug the measurement caught that the picture did not

The first fit clamped the far plane to `ShadowDistance` for the SPLITS but lerped toward frustum
corners at the camera's own 120 m far plane — so a cascade whose far bound was 2.1 m was fitted to
geometry 8 m out. Box 24 m, 11.7 mm/texel, worse than what it replaced. **Nothing in the picture
said so**: the shadows were all present and merely soft. That is the entire reason the
metres-per-texel line is printed into the capture log the baseline hashes.

### The instrument that says the cost is being paid for

`--show-cascades` paints each cascade flat — red, green, blue, magenta for "no cascade contained
it". It is the only thing that answers whether three passes are doing three cascades' work, because
a correct-looking shadow from the wrong cascade looks like a shadow. Measured on the default
framing: 43.7% cascade 0, 6.6% cascade 1, 2.4% cascade 2, **no magenta** — concentric rings, subject
wholly inside the sharp one.

### Left alone, deliberately

`ShadowExtent` and `StudioRenderer.SunViewProjection` are the single-box path this supersedes on this
stage. They still work and are still flagged. Whether they should survive is a conversation, not a
deletion — the standing rule.

### Verified

`Blix.Test.Graphics` 649/649 · `Blix.Test.Diagnostics` 235/235 · `Blix.Test.Physics2D` 43/43 ·
`Blix.Test.Studio` 15/15. Validation clean. Baseline re-recorded, second run byte-identical.
`AmbientStrength` also moved 0.06 → 0.30 in the same pass, now that the fill carries direction.

## Stage 4 — depth pre-pass — DONE, and OFF

Built, correct, and defaulted **off** because the benefit could not be measured and the cost could.

The caster shaders already do exactly this job — transform by a matrix, discard on a cutout — so the
pre-pass is those shaders with the CAMERA's view-projection where the light's goes, and to a view it
is just another `StudioPass.Shadow`. With and without, a capture is **byte-identical**, which is the
correctness bar.

**The instrument could not see the benefit, and that is the finding.** Frame time here is
vsync-locked with no present-mode switch, so it quantises to the refresh. Paired A/B runs at eight
bodies:

| run | pre-pass on | pre-pass off |
| --- | --- | --- |
| first | 7.93 ms | 16.34 ms |
| second | 16.17 ms | 8.02 ms |

The same pair, inverted — noise reading as a 2x result. The only number that stayed put was the CPU
cost of building the extra pass, about **+0.04 ms**. So: measurable cost, unmeasurable benefit, on a
stage that draws a handful of objects. It stays, structural and one flag away, for the case that
changes the answer — terrain or a crowd IS fragment-bound, and that is the case `IStudioView` was
shaped around.

It also cost two pipelines, 8 → **10**. That number is the thing to watch as the house style grows.

**One bug on the way**, caught by the device rather than by a picture: the ground went down the
pre-pass pushing the lit material block, 112 bytes into a caster pipeline that declares 80. The
binding model named both sizes. The ground now pushes a caster block there, and binds the albedo the
caster shader declares at slot 0.

## Stage 5 — MSAA — DONE, and it needed an engine change

The blocker was real and in the right place to fix: a multisampled attachment is not sampleable, and
this stage's present pass **samples the scene depth** to carry it to the swapchain so debug gizmos
depth-test against the scene. The render graph had `ResolveColor` and no `ResolveDepth`.

### `GraphicsPassBuilder.ResolveDepth`, and a second construction path

Depth resolve is a structure chained onto `VkSubpassDescription2`, with no equivalent in the original
call — so a pass that asks for it is built with **`vkCreateRenderPass2`**.

**An addition, not a migration.** Every pass that does not ask keeps the original path byte for byte.
The graph is shared by two games and a rewrite of pass creation is not a thing to do as the tail of
a feature, so `CreateGraphicsPassRenderPass2` is a deliberate near-duplicate: it reproduces every
layout and load-op judgement the original made, because those were learned the hard way and are what
makes the load variant work.

Resolve mode is **`SAMPLE_ZERO`**. Averaging depth across a silhouette produces a surface that is not
there — the mean of a near sample and a far one — and min/max are not portable. One real sample of
the real geometry is exactly what a depth-forward wants.

### The device limit, reported rather than discovered

Asking Metal for 8x on a device that does 4x is a **native assertion** —
*"MTLTextureDescriptor sampleCount (8) is not supported by device"* — which kills the process with no
managed exception and no Vulkan validation message. `VulkanGraphicsDevice.MaxMsaaSamples` now reports
the highest count colour **and** depth can both do (a render pass requires them to agree), and the
stage clamps to it and says so:

    msaa: 8x asked, 4x used — this device supports at most 4x for colour and depth together.

### Measured, both halves

**It antialiases.** Silhouette transitions against the background that are soft rather than hard:
**0.6% at 1x, 4.7% at 4x** — roughly eight times as many antialiased edge pixels — and visible at 3x
zoom on the head.

**And the thing the resolve exists for still works.** Near-white gizmo-line pixels *inside* the
model's silhouette — what you would see if the depth were undefined and the sun arrow drew straight
through the head — are **0 at 1x and 0 at 4x**. Gizmos depth-test against the scene under MSAA
exactly as they did without it.

Default **4x**. Unlike the depth pre-pass beside it — a performance trade that could not be measured
here — this is an image-quality change anyone can see in one frame, and a turntable is a place where
a silhouette turns against a background.

The panel's viewport stays at 1x deliberately: it is a fraction of the window and already renders the
scene a second time.

## Then

**Cascades** — the shadow is one 9 m orthographic box, the most visible quality gap on the thing you
actually look at. **MSAA and a depth pre-pass** last: both structural, both cheap, and they benefit
from the seam in (1) being settled first.

Each stage after this one changes the picture **by design**, so the baseline goes red on purpose.
It stops being an identity control for this arc and becomes a review surface: re-record deliberately
at each stage, and the question becomes *which of the 23 modes changed, and did each change the way
we intended*.

The house style will also be forced to answer something it currently dodges — the `nonormals`
baseline mode is annotated *"recorded rather than asserted"*, and glTF does answer it (generate flat
normals). That is a data question wearing a taste costume, and §7 governs it, not this.

## What this arc will not build

A material system · a shader permutation matrix · transparency ordering · a renderer in core · a
relaunch-with-values mechanism · Sponza's research path. Sponza stays bespoke on purpose: it is
renderer research, and the house style takes from it rather than absorbing it.
