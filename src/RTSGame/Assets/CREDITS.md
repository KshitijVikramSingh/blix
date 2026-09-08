# Asset credits

All models are **CC0 1.0** (public domain — no attribution required; credited here
as good practice). Sourced via https://poly.pizza .

## Characters

- `models/villager_animated.glb` — animated man, **Quaternius** ("Animated Men Pack").
  https://quaternius.com/ · https://poly.pizza/bundle/Animated-Men-Pack-DAC9SDgMQT
- `models/villager_animated_b.glb` — a second body from the same pack, same armature.

One skin, 31 joints (`HumanArmature`), and eleven clips: `Man_Idle`, `Man_Walk`,
`Man_Run`, `Man_Death`, `Man_Punch`, `Man_SwordSlash`, `Man_Clapping`, `Man_Jump`,
`Man_RunningJump`, `Man_Sitting`, `Man_Standing`.

**No labour clip exists in this pack**, and none exists in Quaternius's Universal
Animation Library either (which is CC0 and 120+ clips, but targets a different
universal rig we have no retargeter for). `Man_SwordSlash` stands in as the work
swing: at this camera distance a downward arc reads as an axe, a scythe or a pick.

**The dress is modern** — shirt, trousers, a tie on one of them — which does not match
the `SecondAge` building kit. Deliberate for now: this is here to answer whether motion
makes a body's job readable, and a costume cannot answer that. Replace once the clip set
is settled.

## Where the real animation is coming from

Both **CC0**, both from Quaternius, and between them they cover every action the game asks for:

- **Universal Animation Library** — 120+ clips: locomotion in eight directions, jog, sprint, push, crawl,
  swim, sit, deaths, combat.
- **Universal Animation Library 2** — 130+ clips, and the one that matters here: **farming**. `BodyAction.
  Labour` is the only action currently served by a stand-in, and this is what replaces it.

Take the **root-motion-disabled** export of each. Both ship with and without; the game strips horizontal
root translation anyway (position belongs to the simulation), so the disabled version simply means the
renderer is not undoing work the exporter did.

**The rig naming matters, and it is why the body should probably change too.** Both libraries moved to the
naming scheme of Quaternius's base characters and modular outfits in January 2026. The 2022 Animated Men
Pack above predates that. Measured against a current modular character, twenty bones already agree — Hips,
Abdomen, Torso, Neck, Head, both Shoulder/UpperArm/LowerArm, both UpperLeg/LowerLeg, both Foot, Body —
which is every bone a gait needs at this camera distance. What differs is hands, a `Chest` joint, and two
names (`Bone`→`Root`, `PoleTarget`→`PT`).

So there are two routes:

1. **A base character on the current naming** (and *Modular Character Outfits — Fantasy*, also CC0, which
   would fix the modern dress at the same time). Clips bind with no retargeting at all.
2. **Keep this body** and pass `--preset quaternius-2022-to-base` to `tools/cook-character.sh`. The twenty
   shared bones animate; hands and `Chest` keep their rest pose, which at eighty metres costs a slightly
   stiffer torso and fingers nobody can see.

One trap, verified rather than guessed: a **modular character is several skinned meshes**, and
`GltfImporter` takes one skin per file and ignores the rest without a word. Quaternius's modular men have
four skins in one file; three would vanish. The merge script joins them by default — do not pass
`--no-join` unless you have a reason.

## Everything else

`models/Villager.obj` is the unrigged predecessor. Its own header reads
`# Blender v2.76 OBJ File: 'Animated Human.blend'` — the source was rigged and the OBJ
export discarded the skeleton, which is why it stands in an A-pose and slides.
