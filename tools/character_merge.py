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
#   * LINEAR interpolation ONLY. Blender writes CONSTANT keys as glTF STEP and BEZIER as
#     CUBICSPLINE, both of which the importer refuses, so every keyframe is flattened to
#     LINEAR before export. See flatten_interpolation.

import argparse
import os
import sys

import bpy


def argv_after_dashes():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def parse_args():
    p = argparse.ArgumentParser(description="Merge a rig and its animation clips into one .glb")
    p.add_argument("--base", required=True, help="the rigged character (.fbx or .glb)")
    p.add_argument(
        "--clips",
        default="",
        help="folder of single-animation files to carry onto the rig. Optional: with no --clips the "
             "character is joined and re-exported with whatever animations it already carries, which is "
             "what a modular Quaternius character needs (several skinned meshes, clips already on board).")
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
        "--wear",
        default="",
        help="comma-separated character files whose MESHES are kept and re-bound to the base rig — "
             "outfits, hairstyles, beards. This is how a modular character is assembled: a base body "
             "supplies the head and the skin, and each outfit piece is skinned to the same skeleton. "
             "Unlike --clips, whose files contribute animations and whose meshes are discarded.")
    p.add_argument(
        "--keep-all-maps",
        action="store_true",
        help="keep normal, roughness and occlusion textures. Off by default: the game samples base colour "
             "and nothing else, so the rest is tens of megabytes decoded at load and discarded — and "
             "decoding four 4K maps it will never read is what made the first assembled villager fail to "
             "load at all.")
    p.add_argument(
        "--drop",
        default="",
        help="comma-separated mesh or material names to leave out. For detail nobody can see at this "
             "camera distance and which therefore only adds noise — eyeballs read as spectacles at eighty "
             "metres, because two dark discs is all that survives the resolution.")
    p.add_argument(
        "--head-only",
        action="store_true",
        help="trim the BASE character to its head, keeping vertices weighted to the head and neck. For a "
             "clothed modular character: the outfit is the whole silhouette and the base supplies only the "
             "face, so the base body's own proportions stop mattering — which is how a broad 'superhero' "
             "base can wear clothing cut for an ordinary one without bulging through it.")
    p.add_argument(
        "--bone-map",
        default="",
        help="comma-separated From=To bone renames, applied to the BASE rig so clips authored "
             "against another naming scheme drive it. See --preset for the ones already worked out.")
    p.add_argument(
        "--preset",
        default="",
        choices=["", "basechar-to-rigify", "quaternius-2022-to-base"],
        help="a worked-out --bone-map. 'basechar-to-rigify' renames an outfit-compatible base character "
             "(Root/Hips/Torso/UpperLeg, 62 joints) to the DEF- deform names the 53-bone animation library "
             "uses, so those clips drive a dressed body. 'quaternius-2022-to-base' is the older 2022 rig.")
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
# ============================================================================================
# WARNING, PAID FOR FROM THE CHAIR: A NAME MAP IS NOT A RETARGET.
#
# `basechar-to-rigify` below binds perfectly — 186 channels, all 62 joints driven, the asset loads
# and every clip plays — and the result was reported as "scary, weird broken animations". Renaming
# bones makes another rig's clips ADDRESS this one; it does nothing about the two rigs disagreeing
# on rest pose and bone roll. A rotation authored for a bone whose rest orientation differs applies
# that rotation to a different starting frame, and limbs twist.
#
# So these presets are only safe between rigs that share a rest pose — which, in practice, means
# rigs from the same generation of the same pack, where a rename is fixing a spelling change and
# nothing else. Between generations, use each character's OWN clips (omit --clips and --preset,
# which joins and re-exports what it came with), or do a real retarget: constrain each target bone
# to its source in world space and bake, which corrects the rest-pose difference instead of
# ignoring it. That is a job this script does not yet do.
# ============================================================================================
#
# <b>Base-character names to the Rigify deform names.</b> Quaternius has three skeletons in circulation and
# they do not interoperate by accident:
#
#   HumanArmature   31 joints  the 2022 packs (Animated Men)
#   Root            62 joints  base characters, modular men, the fantasy OUTFITS, and current library exports
#   DEF-*           53 joints  the "Animated Base Character" and the 45 clips that ship on it
#
# The outfits are built for the second. So dressing a villager means a body on that rig — and then the
# 53-bone library's clips need renaming to drive it. Every bone a gait needs corresponds exactly; only the
# spelling differs, which is why this is a name map and not a retarget.
#
# Applied to the BASE rig, because the script renames the body to match the clips. Read the warning
# above before reaching for either of them.
PRESETS = {
    "basechar-to-rigify": {
        "Root": "root",
        "Hips": "DEF-hips",
        "Abdomen": "DEF-spine.001",
        "Torso": "DEF-spine.002",
        "Chest": "DEF-spine.003",
        "Neck": "DEF-neck",
        "Head": "DEF-head",
        "Shoulder.L": "DEF-shoulder.L",
        "Shoulder.R": "DEF-shoulder.R",
        "UpperArm.L": "DEF-upper_arm.L",
        "UpperArm.R": "DEF-upper_arm.R",
        "LowerArm.L": "DEF-forearm.L",
        "LowerArm.R": "DEF-forearm.R",
        "Wrist.L": "DEF-hand.L",
        "Wrist.R": "DEF-hand.R",
        "UpperLeg.L": "DEF-thigh.L",
        "UpperLeg.R": "DEF-thigh.R",
        "LowerLeg.L": "DEF-shin.L",
        "LowerLeg.R": "DEF-shin.R",
        "Foot.L": "DEF-foot.L",
        "Foot.R": "DEF-foot.R",
    },
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


# What the game's fragment stage actually reads: base colour. Everything else in a PBR material is
# decoded at load and thrown away.
UNUSED_INPUTS = ("Normal", "Roughness", "Metallic", "Specular", "Specular IOR Level", "IOR",
                 "Alpha", "Emission Color", "Emission Strength", "Coat Weight", "Sheen Weight")


def strip_unused_maps():
    """Disconnect every texture the renderer will not sample, so the exporter omits it.

    <b>Not an optimisation — a fix.</b> An assembled villager carried ten images, four of them 4096x4096
    normal and occlusion maps. A 4K RGBA decode is sixty-four megabytes, and the importer decoded all of
    them before the first frame; the load failed inside the image decoder with an unprintable message.
    The game's skinned stage samples base colour and nothing else, so every one of those maps was work
    done to be discarded.
    """
    unlinked = 0
    for material in bpy.data.materials:
        if not material.use_nodes or material.node_tree is None:
            continue
        for node in material.node_tree.nodes:
            if node.type != "BSDF_PRINCIPLED":
                continue
            for name in UNUSED_INPUTS:
                socket = node.inputs.get(name)
                if socket is None:
                    continue
                for link in list(socket.links):
                    material.node_tree.links.remove(link)
                    unlinked += 1

    return unlinked


def flatten_interpolation():
    """Force every keyframe to LINEAR, because that is the only mode the importer reads.

    src/Blix/GltfImporter.cs rejects anything else outright — BuildVector3Curve and
    BuildQuaternionCurve both throw on a sampler whose InterpolationMode is not LINEAR. Blender maps
    CONSTANT keys to glTF STEP and BEZIER keys to CUBICSPLINE, so an author's perfectly ordinary choice
    in the animation package becomes an asset this engine will not load. Found the hard way: a library
    export failed on 'Crouch_Fwd_Loop' translation with mode STEP, and the character fell back to the
    unrigged prop.

    Flattening is safe for skeletal motion at this camera distance — the difference between linear and
    Bezier between two keys a frame or two apart is not visible on a body a metre and a half tall — and it
    is far better than the alternative, which is an asset that loads on some clips and not others.
    """
    changed = 0
    for action in bpy.data.actions:
        for curve in action_fcurves(action):
            for key in curve.keyframe_points:
                if key.interpolation != "LINEAR":
                    key.interpolation = "LINEAR"
                    changed += 1
    return changed


def action_fcurves(action):
    """Every f-curve in an action, across both Blender's action APIs.

    <b>Blender 4.4 made actions slotted and 5.x removed the flat accessor.</b> `action.fcurves` used to be
    the whole story; now the curves live under layers → strips → channelbags, and reaching for the old
    attribute on 5.2 raises AttributeError halfway through a cook. Both spellings are handled because this
    script has to survive whichever Blender somebody has installed, and the failure mode of guessing is a
    pipeline that works on one machine.
    """
    flat = getattr(action, "fcurves", None)
    if flat is not None:
        yield from flat
        return

    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for bag in getattr(strip, "channelbags", ()):
                yield from getattr(bag, "fcurves", ())


def stack_on_armature(armature):
    """Give the rig an NLA track per action, which is what makes the exporter write them out.

    <b>Found by reading the file back, not by reading the log.</b> The exporter's ACTIONS mode writes the
    actions it can associate with the object being exported. Actions imported from another file arrive
    attached to THAT file's armature, and deleting it leaves them with a slot that no longer resolves — so
    forty-three clips were flattened, counted, reported, and silently not written. The output had no
    `animations` key at all while the log said 43.

    An NLA track per action is the documented way to hand the exporter a list of clips on one rig, and
    NLA_TRACKS mode names each animation after its track. Belt and braces: verify_export below reads the
    result back rather than trusting this.
    """
    if armature.animation_data is None:
        armature.animation_data_create()

    # A leftover active action would be exported a second time under its own name.
    armature.animation_data.action = None
    for track in list(armature.animation_data.nla_tracks):
        armature.animation_data.nla_tracks.remove(track)

    laid = 0
    for action in sorted(bpy.data.actions, key=lambda a: a.name):
        track = armature.animation_data.nla_tracks.new()
        track.name = action.name
        start = int(action.frame_range[0]) if hasattr(action, "frame_range") else 0
        strip = track.strips.new(action.name, start, action)
        strip.name = action.name
        # Muted tracks are skipped by the exporter.
        track.mute = False
        laid += 1

    return laid


def verify_export(path, expected):
    """Read the written .glb back and say what is actually in it.

    The whole reason this exists: a pipeline that reports what it *meant* to write is a pipeline that
    lies. Every number below comes from the file.
    """
    import json as _json
    import struct as _struct

    with open(path, "rb") as handle:
        data = handle.read()

    offset, doc = 12, None
    while offset < len(data):
        length, kind = _struct.unpack_from("<II", data, offset)
        if kind == 0x4E4F534A:
            doc = _json.loads(data[offset + 8:offset + 8 + length].decode("utf-8"))
            break
        offset += 8 + length + ((4 - length % 4) % 4 if length % 4 else 0)

    if doc is None:
        raise SystemExit(f"{path}: no JSON chunk — the export did not produce a glTF")

    animations = doc.get("animations", [])
    skins = doc.get("skins", [])
    modes = set()
    for animation in animations:
        for sampler in animation.get("samplers", []):
            modes.add(sampler.get("interpolation", "LINEAR"))

    print(f"[merge] VERIFIED in the file: {len(skins)} skin(s), {len(animations)} animation(s), "
          f"interpolation {sorted(modes) or ['none']}")

    problems = []
    if len(skins) != 1:
        problems.append(f"{len(skins)} skins; the importer reads one and ignores the rest")
    if not animations:
        problems.append("no animations were written")
    elif len(animations) < expected:
        problems.append(f"only {len(animations)} of {expected} clips were written")
    if modes - {"LINEAR"}:
        problems.append(f"interpolation {sorted(modes - {'LINEAR'})}; the importer accepts LINEAR only")

    if problems:
        raise SystemExit("[merge] EXPORT IS NOT USABLE: " + "; ".join(problems))


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


# Bones whose vertices are the head. Everything else on the base body is covered by clothing.
HEAD_BONES = ("head", "neck")


def trim_to_head(armature):
    """Delete the base body's vertices below the neck, keeping the face.

    <b>Why this is the fix for a proportion mismatch rather than a hack.</b> A modular character's outfit
    covers arms, body, legs and feet; the only thing the base supplies that the clothing does not is the
    head. Keep just that and the base body's own build stops being visible at all — which matters because
    the free tier of these base characters ships "superhero" proportions only, and clothing cut for an
    ordinary build stretched over it reads exactly as what it is. Reported from the chair before this
    existed: "the superhero proportions is definitely happening".
    """
    import bmesh

    removed = 0
    for obj in list(bpy.context.scene.objects):
        if obj.type != "MESH" or obj.parent is not armature:
            continue

        # Which of this mesh's groups are head groups, by the rig's own naming.
        head_groups = {
            group.index for group in obj.vertex_groups
            if any(w in group.name.lower() for w in HEAD_BONES)
        }
        if not head_groups:
            # A mesh with no head weights at all is not the body — hair, eyes, a hat. Left alone.
            continue

        mesh = bmesh.new()
        mesh.from_mesh(obj.data)
        layer = mesh.verts.layers.deform.active
        if layer is None:
            mesh.free()
            continue

        doomed = []
        for vert in mesh.verts:
            weights = vert[layer]
            if not weights:
                continue
            # Dominant group decides, so a vertex straddling the collar goes with whichever owns it.
            best = max(weights.items(), key=lambda pair: pair[1])[0]
            if best not in head_groups:
                doomed.append(vert)

        if doomed:
            bmesh.ops.delete(mesh, geom=doomed, context="VERTS")
            removed += len(doomed)
            mesh.to_mesh(obj.data)
        mesh.free()
        obj.data.update()

    return removed


def drop_meshes(spec):
    """Remove meshes whose name or material matches any of these, before the join."""
    wanted = [x.strip().lower() for x in spec.split(",") if x.strip()]
    if not wanted:
        return 0

    removed = 0
    for obj in list(bpy.context.scene.objects):
        if obj.type != "MESH":
            continue
        names = [obj.name.lower()]
        names += [slot.material.name.lower() for slot in obj.material_slots if slot.material]
        if any(w in n for w in wanted for n in names):
            bpy.data.objects.remove(obj, do_unlink=True)
            removed += 1

    return removed


def wear_onto(armature, path):
    """Import a file and re-bind its skinned meshes to this rig, discarding its own skeleton.

    <b>This is what a modular character actually is.</b> Quaternius's outfit pack ships clothing and nothing
    else — Male_Peasant is arms, body, feet and legs, with no head, because the head belongs to the base
    character it is worn over. Found the hard way: villagers with no heads, and the file genuinely has none.

    Both files carry the same skeleton (same names, same rest pose, same generation), so re-binding is
    honest here: the vertex groups already name the base rig's bones, and pointing the armature modifier at
    it is all that is needed. This is emphatically NOT the bone-rename retarget the warning above rejects —
    nothing is renamed and no rest pose is assumed away.
    """
    before = {o.name for o in bpy.context.scene.objects}
    load(path)
    fresh = [o for o in bpy.context.scene.objects if o.name not in before]

    kept = 0
    for obj in fresh:
        if obj.type != "MESH":
            continue
        rebound = False
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE":
                modifier.object = armature
                rebound = True
        if not rebound:
            # An unskinned piece (a prop, a stray sphere) is not clothing; leave it out rather than
            # freezing it to the body in whatever pose the file happened to be saved in.
            continue

        matrix = obj.matrix_world.copy()
        obj.parent = armature
        obj.matrix_world = matrix
        kept += 1

    # The worn file's own skeleton and anything else it brought.
    for obj in fresh:
        if obj.type == "MESH" and obj.parent is armature:
            continue
        bpy.data.objects.remove(obj, do_unlink=True)

    return kept


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
        # <b>Renaming bones orphans the body's own animations.</b> An action addresses bones by name, so
        # every clip the character shipped with is now pointing at bones that no longer exist — and it
        # would still export, as a file full of animations that move nothing. If the rig has been renamed
        # to accept somebody else's clips, its own are gone by that act; say so and drop them.
        own = list(bpy.data.actions)
        if own and args.clips:
            for action in own:
                bpy.data.actions.remove(action)
            print(f"[merge] dropped the character's own {len(own)} clip(s): renaming its bones orphaned "
                  f"them, and the incoming clips are what the new names are for")
        elif own:
            raise SystemExit(
                "bones were renamed but no --clips were given, which would export a character whose own "
                "animations address bones that no longer exist. Supply the clips the rename is for.")
    if args.head_only:
        trimmed = trim_to_head(base_armature)
        print(f"[merge] trimmed the base to its head: {trimmed} vertices removed below the neck")

    for piece in (x.strip() for x in args.wear.split(",") if x.strip()):
        worn = wear_onto(base_armature, piece)
        print(f"[merge] wearing '{os.path.basename(piece)}': {worn} mesh(es) re-bound to the base rig")

    dropped = drop_meshes(args.drop)
    if dropped:
        print(f"[merge] dropped {dropped} mesh(es) as asked")

    if not args.no_join:
        joined = join_skinned_meshes(base_armature)
        print(f"[merge] joined {joined} skinned mesh(es) into one — the importer takes one skin per file")
    print(f"[merge] base '{os.path.basename(args.base)}': "
          f"armature '{base_armature.name}', {len(base_armature.data.bones)} bones, "
          f"{stripped} prefix(es) stripped")

    # Every action in the file lives in bpy.data.actions and survives its armature being
    # deleted, so the merge is: import each clip file, take its action, drop its rig.
    kept = []
    if args.clips and not os.path.isdir(args.clips):
        raise SystemExit(f"--clips '{args.clips}' is not a folder")

    if not args.clips:
        existing = sorted(a.name for a in bpy.data.actions)
        for action in bpy.data.actions:
            action.use_fake_user = True
        print(f"[merge] no --clips given; keeping the {len(existing)} clip(s) already on the character")
        kept = existing

    for entry in sorted(os.listdir(args.clips)) if args.clips else []:
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

        # <b>Every action in the file, not the first.</b> Mixamo gives one animation per file, so the first
        # draft took fresh[0] — but a library export is the other shape entirely: one file carrying fifty
        # clips on one rig. Taking the first would have silently imported a fiftieth of the content.
        stem = os.path.splitext(entry)[0]
        for action in fresh:
            # One action in the file: the file name is the clip name, which is the Mixamo convention.
            # Several: each keeps its own name, because the file name cannot describe fifty of them.
            name = renames.get(stem, stem) if len(fresh) == 1 else renames.get(action.name, action.name)
            action.name = name
            # Without a fake user an unassigned action is dropped on save, and the glTF exporter would
            # then write a file with nothing in it.
            action.use_fake_user = True
            kept.append(name)

        print(f"[merge] {entry}: {len(fresh)} action(s) — "
              f"{', '.join(a.name for a in fresh[:6])}{' …' if len(fresh) > 6 else ''}")

    if not kept and args.clips:
        raise SystemExit(f"no animations found under '{args.clips}'")

    if not args.keep_all_maps:
        unlinked = strip_unused_maps()
        print(f"[merge] unlinked {unlinked} texture(s) the game does not sample (normal/roughness/AO)")

    flattened = flatten_interpolation()
    print(f"[merge] set {flattened} keyframe(s) to LINEAR — the only mode the importer reads")

    tracks = stack_on_armature(base_armature)
    print(f"[merge] laid {tracks} action(s) onto the rig as NLA tracks")

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    # export_animation_mode NLA_TRACKS is what writes every action as its own glTF
    # animation; the default writes only the active one.
    bpy.ops.export_scene.gltf(
        filepath=args.out,
        export_format="GLB",
        export_animations=True,
        # NLA_TRACKS, because stack_on_armature has laid one track per clip and this mode writes one
        # animation per track. ACTIONS mode silently wrote none for actions imported from another file.
        export_animation_mode="NLA_TRACKS",
        export_skins=True,
        export_apply=False,          # see the note at the top: do NOT bake transforms
        export_yup=True,
        # <b>The one flag that decides whether this asset loads at all.</b> Left at its default of True,
        # the exporter collapses any channel whose value never changes down to a single keyframe — and a
        # single keyframe needs no interpolation, so it is written as glTF STEP. The importer accepts only
        # LINEAR and refuses the whole file. Every keyframe in the source is already LINEAR; the STEP was
        # manufactured at export time by an optimisation, which is why looking at the keys found nothing.
        # Costs a larger file, and the alternative is an asset the game will not open.
        export_optimize_animation_size=False,
        # Already the defaults, stated because they are load-bearing here rather than incidental.
        export_force_sampling=True,
        export_sampling_interpolation_fallback="LINEAR",
    )
    print(f"[merge] wrote {args.out}, intending {len(kept)} clip(s)")
    verify_export(args.out, len(kept))
    print("[merge] verify with: dotnet run --project src/Blix.Tools.Cook -- inspect " + args.out)


if __name__ == "__main__":
    main()
