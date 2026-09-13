# Character sources

Raw, multi-skin sources. **Not loaded by the game** — they are cooked into
`src/RTSGame/Assets/models/` by `tools/cook-character.sh`, because
`src/Blix/GltfImporter.cs` takes one skin per file and silently ignores the rest, and a
modular Quaternius character is four or five skinned meshes in one file.

All CC0, all Quaternius, all re-fetchable from poly.pizza.

| file | what | skins | clips | rig |
|---|---|---|---|---|
| `adventurer.glb` | dressed adventurer — https://poly.pizza/m/5EGWBMpuXq | 5 | 24 | `Root` (62 joints) |
| `animation-library.glb` | grey mannequin — https://poly.pizza/m/cwYvO5UauX | 1 | 45 | `DEF-*` (53 joints) |

## The three rigs, because they do not interoperate by accident

| rig | joints | who uses it |
|---|---|---|
| `HumanArmature` | 31 | the 2022 packs (Animated Men) — gone from here |
| `Root` | 62 | base characters, modular men, **the fantasy outfits**, current library exports |
| `DEF-*` | 53 | the Animated Base Character above, and the 45 clips riding on it |

The **outfits are built for `Root`**, so a dressed villager has to be on that rig. The
45-clip library is on `DEF-*`, and every bone a gait needs corresponds exactly — only the
spelling differs (`Hips`/`DEF-hips`, `Abdomen`/`DEF-spine.001`, `UpperLeg.L`/`DEF-thigh.L`).
That is what `--preset basechar-to-rigify` encodes, so those clips can drive a dressed body
without a real retarget.
