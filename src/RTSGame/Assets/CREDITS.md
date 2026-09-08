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

## Everything else

`models/Villager.obj` is the unrigged predecessor. Its own header reads
`# Blender v2.76 OBJ File: 'Animated Human.blend'` — the source was rigged and the OBJ
export discarded the skeleton, which is why it stands in an A-pose and slides.
