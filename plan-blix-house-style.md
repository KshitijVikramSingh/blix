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

## Stage 2 — IBL — NEXT

Scoped and smaller than it sounds, because **the bake is already engine-owned**:
`EnvironmentProfile`, `EnvironmentBaker.Bake` → `EnvironmentProbe` (env cube, diffuse irradiance,
prefiltered specular), `BakeBrdfLut`, `PbrIblBaker` underneath. And there are two sources —
`HdrEnvironmentSource` and **`ProceduralEnvironmentSource(SunDirection)`** — so Studio needs **no
environment asset**: the house style's own sun bakes its own sky, which is what a tool that must
open any model on any machine wants.

PBR is already there too: `studio_lit.frag` builds on `blix_cookTorranceBrdf` / `blix_sun_shadow` /
`blix_tonemap`. The delta is the IBL term.

**What actually has to move is the shader half.** `ibl.glsl` lives in
`src/Demos/Blix.Demos.VulkanLit/Shaders/`, not in `Blix.Shaders`. It is promoted to the vocabulary
layer and VulkanLit switches to the shared copy — the technique going where technique goes, with
Studio only composing it.

`AmbientStrength` changes meaning rather than disappearing: from "flat stand-in for image-based
lighting" to how much of the environment reaches shadow. Zero stays a legitimate inspection mode —
flattening the fill is how a silhouette becomes readable.

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
