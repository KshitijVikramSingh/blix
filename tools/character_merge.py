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
    return p.parse_args(argv_after_dashes())


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


def main():
    args = parse_args()
    renames = {}
    for pair in (x for x in args.name_map.split(",") if x.strip()):
        if "=" not in pair:
            raise SystemExit(f"--name-map wants From=To, got '{pair}'")
        source, target = pair.split("=", 1)
        renames[source.strip()] = target.strip()

    clear_scene()
    load(args.base)
    base_armature = sole_armature()
    stripped = strip_bone_prefix(base_armature, args.strip_prefix)
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
