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
