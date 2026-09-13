# Asset credits

All models are **CC0 1.0** (public domain — no attribution required; credited here
as good practice). Sourced via https://poly.pizza .

## Characters

- `models/villager_universal.glb` — **Animated Base Character**, *Quaternius*, **CC0**.
  https://quaternius.com/ · https://poly.pizza/m/cwYvO5UauX

One skin, one mesh, two primitives, 8,546 vertices, 53 bones, **45 clips**. 1.83 authored units, normalised
to unit height and scaled by the placement matrix.

**Chosen for its skeleton, not its looks.** The rig is the Rigify deform skeleton — `DEF-hips`,
`DEF-spine.001..003`, `DEF-upper_arm.L`, `DEF-thigh.L`, `DEF-toe.L` — which is what Quaternius means by "a
universal humanoid rig, ready for retargeting", and it is what the **Universal Animation Library** and
**Universal Animation Library 2** are authored against. So those libraries (both CC0; UAL2 is where the
farming loop lives) drop onto this body with **no retargeting at all**, and neither needs the join step
below because this file already has one skin.

Every on-screen action binds to a real clip, with nothing standing in:

    Idle=Idle_Loop  Walk=Walk_Loop  Labour=Fixing_Kneeling  Strike=Sword_Attack  Fall=Death01

`Fixing_Kneeling` is a genuine work animation — kneeling and working at something — so it is a real clip for
`BodyAction.Labour` rather than a stand-in. UAL2's farming clips are listed ahead of it in
`RTSGame/Rendering/CharacterClips.cs`, so they take over the moment they are dropped in.

**It is a grey mannequin**, and that is the remaining honest gap: the body reads as a body and not yet as a
villager. Dress is a separate axis from motion — *Modular Character Outfits — Fantasy* is CC0 and built for
these base characters — and it was left for later deliberately, because a costume cannot answer whether
motion makes a job readable.

**A note on `_RM`.** The library ships clips in pairs, one travelling and one not. Prefer the plain
spelling: horizontal root translation is stripped anyway (position belongs to the simulation), so binding
the travelling twin means the renderer undoes the exporter's work every frame.

### Superseded

The 2022 **Animated Men Pack** was the first rigged body here and has been removed — git history holds it.
Two things it taught, both kept:

- Its rig predates January 2026's naming change, and measuring the two against each other gave the twenty
  bones that already agree (hips, abdomen, torso, neck, head, both arms, both legs, both feet) — every bone
  a gait needs at this distance. That is what `--preset quaternius-2022-to-base` in
  `tools/character_merge.py` encodes, and it still applies to any 2022-era Quaternius pack.
- It had no work clip at all, which is what forced `CharacterClips` to distinguish a real clip from a
  stand-in, and therefore what made the load report worth reading.

## Everything else

`models/Villager.obj` is the unrigged predecessor. Its own header reads
`# Blender v2.76 OBJ File: 'Animated Human.blend'` — the source was rigged and the OBJ
export discarded the skeleton, which is why it stands in an A-pose and slides.
