# Merge a rigged character and a folder of single-animation FBX files into ONE .glb
# carrying every clip on one skeleton — the shape src/Blix/GltfImporter.cs requires.
#
# Run under Blender, headless:
#
#   blender --background --python tools/character_merge.py -- \
#       --base    art/villager/base.fbx \
#       --clips   art/villager/clips \
#       --out     src/RTSGame/Assets/models/villager_animated.glb \
#       --name-map "Walking=Walk,Chopping=Labour"
#
# Why Blender and not a lighter converter: two jobs, not one. FBX2glTF and friends
# convert a single file and cannot merge, and Mixamo exports one FBX per animation —
# so something has to import N rigs, keep one, and carry N actions onto it. Blender
# is the only tool on that list that also retargets, which is what a CC0 animation
# library on a different skeleton would need.
#
# The importer's rules this script exists to satisfy:
#   * ONE skin per file. Secondary skins are ignored, so every clip must ride the
#     armature that the mesh is actually weighted to.
#   * Animations are read by name. glTF export writes them as "<Armature>|<Action>",
#     and the game strips the prefix and matches the rest against
#     RTSGame/Rendering/CharacterClips.cs.
#   * Vertices stay mesh-local; the mesh node's transform is exposed separately. So do
#     NOT "apply all transforms" as a tidy-up step — it will move the skinning frame.

import argparse
import os
import sys

import bpy


def argv_after_dashes():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def parse_args():
    p = argparse.ArgumentParser(description="Merge a rig and its animation clips into one .glb")
    p.add_argument("--base", required=True, help="the rigged character (.fbx or .glb)")
    p.add_argument("--clips", required=True, help="folder of single-animation .fbx files")
    p.add_argument("--out", required=True, help="the .glb to write")
    p.add_argument(
        "--name-map",
        default="",
        help="comma-separated From=To renames applied to action names, e.g. Walking=Walk")
    p.add_argument(
        "--strip-prefix",
        default="mixamorig:",
        help="bone-name prefix to remove; Mixamo prefixes every bone with this")
    p.add_argument(
        "--bone-map",
        default="",
        help="comma-separated From=To bone renames, applied to the BASE rig so clips authored "
             "against another naming scheme drive it. See --preset for the ones already worked out.")
    p.add_argument(
        "--preset",
        default="",
        choices=["", "quaternius-2022-to-base"],
        help="a worked-out --bone-map. 'quaternius-2022-to-base' renames the 2022 Animated Men rig "
             "to the naming Quaternius's Universal Animation Library and base characters use.")
    p.add_argument(
        "--no-join",
        action="store_true",
        help="skip joining skinned meshes into one. Leave this OFF unless you know why: the importer "
             "takes ONE skin per file and silently ignores the rest, so a modular character exported "
             "as body + outfit pieces loses every piece but the first.")
    return p.parse_args(argv_after_dashes())


# <b>The 2022 rig against the current one.</b> Twenty bones already agree — Hips, Abdomen, Torso, Neck,
# Head, both Shoulder/UpperArm/LowerArm, both UpperLeg/LowerLeg, both Foot, Body — which is every bone a
# gait needs at this camera distance. What differs is hands, one extra spine joint and two names:
#
#   2022 Animated Men          current base characters / Universal Animation Library
#   -----------------          ---------------------------------------------------
#   Bone                       Root
#   Palm.L/R                   Wrist.L/R
#   MiddleHand.L/R, Fingers    Index1/Middle1/Ring1/Pinky1 …   (no equivalent; dropped)
#   Thumb2.L/R                 Thumb2.L/R via Thumb1 chain
#   PoleTarget.L/R             PT.L/R
#   (none)                     Chest                            (no equivalent; dropped)
#
# Dropped bones simply receive no animation and keep their rest pose. At eighty metres that costs a
# slightly stiffer torso and fingers nobody can see, which is why mapping is a real option rather than a
# compromise — but retargeting nothing at all is better, so prefer a body on the current naming.
PRESETS = {
    "quaternius-2022-to-base": {
        "Bone": "Root",
        "Palm.L": "Wrist.L",
        "Palm.R": "Wrist.R",
        "PoleTarget.L": "PT.L",
        "PoleTarget.R": "PT.R",
    },
}


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def load(path):
    ext = os.path.splitext(path)[1].lower()
    if ext == ".fbx":
        # automatic_bone_orientation keeps Mixamo's rest pose from arriving rotated.
        bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=True)
    elif ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    else:
        raise SystemExit(f"unsupported input '{path}': want .fbx, .glb or .gltf")


def sole_armature():
    found = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    if len(found) != 1:
        raise SystemExit(
            f"expected exactly one armature after import, found {len(found)}: "
            f"{[o.name for o in found]}")
    return found[0]


def strip_bone_prefix(armature, prefix):
    """Mixamo names every bone 'mixamorig:Hips'. Harmless, but it makes a rig unreadable
    beside a hand-authored one, and the game prints bone names when a rig is inspected."""
    if not prefix:
        return 0
    renamed = 0
    for bone in armature.data.bones:
        if bone.name.startswith(prefix):
            bone.name = bone.name[len(prefix):]
            renamed += 1
    return renamed


def rename_bones(armature, mapping):
    """Rename bones on the base rig so clips authored against another scheme bind by name."""
    if not mapping:
        return 0
    renamed = 0
    for bone in armature.data.bones:
        target = mapping.get(bone.name)
        if target and target != bone.name:
            bone.name = target
            renamed += 1
    return renamed


def join_skinned_meshes(armature):
    """Collapse every mesh weighted to this armature into ONE object, and therefore one skin.

    src/Blix/GltfImporter.cs takes the first node carrying both a mesh and a skin as primary and collects
    only the other nodes sharing THAT skin; anything on a second skin is ignored without a word. Modular
    characters are exactly that shape — body, head, outfit pieces, each its own skinned mesh — so a
    straight export loses all but one piece. Verified against Quaternius's modular men: four skins in the
    file, one imported.
    """
    meshes = [
        o for o in bpy.context.scene.objects
        if o.type == "MESH" and any(m.type == "ARMATURE" and m.object is armature for m in o.modifiers)
    ]
    if len(meshes) < 2:
        return len(meshes)

    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    return len(meshes)


def main():
    args = parse_args()
    renames = {}
    for pair in (x for x in args.name_map.split(",") if x.strip()):
        if "=" not in pair:
            raise SystemExit(f"--name-map wants From=To, got '{pair}'")
        source, target = pair.split("=", 1)
        renames[source.strip()] = target.strip()

    # <b>Bones and actions are different namespaces.</b> --name-map renames clips, --bone-map renames
    # bones, and merging the two (which the first draft did) would rename a bone because an action
    # happened to share its name.
    bone_renames = dict(PRESETS.get(args.preset, {}))
    for pair in (x for x in args.bone_map.split(",") if x.strip()):
        if "=" not in pair:
            raise SystemExit(f"--bone-map wants From=To, got '{pair}'")
        source, target = pair.split("=", 1)
        # Explicit renames win, so a preset can be corrected on the command line.
        bone_renames[source.strip()] = target.strip()

    clear_scene()
    load(args.base)
    base_armature = sole_armature()
    stripped = strip_bone_prefix(base_armature, args.strip_prefix)
    remapped = rename_bones(base_armature, bone_renames)
    if remapped:
        print(f"[merge] renamed {remapped} bone(s) on the base rig to match the clips")
    if not args.no_join:
        joined = join_skinned_meshes(base_armature)
        print(f"[merge] joined {joined} skinned mesh(es) into one — the importer takes one skin per file")
    print(f"[merge] base '{os.path.basename(args.base)}': "
          f"armature '{base_armature.name}', {len(base_armature.data.bones)} bones, "
          f"{stripped} prefix(es) stripped")

    # Every action in the file lives in bpy.data.actions and survives its armature being
    # deleted, so the merge is: import each clip file, take its action, drop its rig.
    kept = []
    if not os.path.isdir(args.clips):
        raise SystemExit(f"--clips '{args.clips}' is not a folder")

    for entry in sorted(os.listdir(args.clips)):
        if not entry.lower().endswith((".fbx", ".glb", ".gltf")):
            continue
        before = {a.name for a in bpy.data.actions}
        load(os.path.join(args.clips, entry))
        fresh = [a for a in bpy.data.actions if a.name not in before]
        if not fresh:
            print(f"[merge] {entry}: no action found, skipped")
            continue

        # The clip's own rig is not wanted — only its action, carried onto the base rig.
        for obj in list(bpy.context.scene.objects):
            if obj is not base_armature and obj.type in ("ARMATURE", "MESH"):
                if obj.name != base_armature.name and obj.parent is not base_armature:
                    bpy.data.objects.remove(obj, do_unlink=True)

        stem = os.path.splitext(entry)[0]
        name = renames.get(stem, stem)
        action = fresh[0]
        action.name = name
        # Without a fake user an unassigned action is dropped on save, and the glTF
        # exporter would then write a file with one animation in it.
        action.use_fake_user = True
        kept.append(name)
        print(f"[merge] {entry}: action '{name}', {len(action.fcurves)} curve(s)")

    if not kept:
        raise SystemExit(f"no animations found under '{args.clips}'")

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    # export_animation_mode NLA_TRACKS is what writes every action as its own glTF
    # animation; the default writes only the active one.
    bpy.ops.export_scene.gltf(
        filepath=args.out,
        export_format="GLB",
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_skins=True,
        export_apply=False,          # see the note at the top: do NOT bake transforms
        export_yup=True,
    )
    print(f"[merge] wrote {args.out} with {len(kept)} clip(s): {', '.join(kept)}")
    print("[merge] verify with: dotnet run --project src/Blix.Tools.Cook -- inspect " + args.out)


if __name__ == "__main__":
    main()
