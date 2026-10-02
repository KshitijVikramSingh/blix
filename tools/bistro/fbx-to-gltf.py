# Blender, headless: one ORCA Bistro FBX -> glTF 2.0 (separate .gltf/.bin/textures), for the Blix cook.
#
#   blender -b --python tools/bistro/fbx-to-gltf.py -- <in.fbx> <out.gltf>
#
# This is a FORMAT conversion and decides nothing a cook configuration can say instead: the normal-map
# convention, transmission, double-sidedness beyond what the file states are bistro.blixcook's. What it
# does fix is what Blender's FBX importer gets wrong about Bistro's own published texture convention
# (README.txt), because glTF has a place for each channel and the importer puts them in the wrong one:
#
#   BaseColor  RGB colour, A opacity       -> baseColor; alpha mode MASK where any texel is cut, else OPAQUE
#   Specular   R occlusion, G roughness,   -> metallicRoughness (G, B), and occlusion (R) only where R carries
#              B metalness                    anything: glTF's own packing, one image referenced twice
#   Normal     DirectX                     -> normalTexture, as authored (the convention is the cook's)
#   Emissive   RGB                         -> emissive, as the importer wires it
#
# Two more the importer invents: every material double-sided (it leaves backface culling off), where
# Bistro states double-sidedness in the material NAME (".DoubleSided", changelog v3a: the foliage); and a
# specular tint taken from the FBX's legacy specular colour, which exports as KHR_materials_specular on a
# file that authors none. Both are put back to what the file says.
#
# <b>Occlusion from the data, not the README.</b> Every Specular map in the pack has R = 0 (they are 16x16
# constants), which Falcor, the renderer the pack was made for, never reads as occlusion. glTF reads an
# occlusion of 0 as "receives no indirect light", so wiring it by the README blacked out everything the sun
# missed. An image whose R never rises above zero is not authored occlusion, and is not exported as one.
#
# The importer wires Specular into Principled's "Specular IOR Level" and links every BaseColor alpha, so
# without this every material exports alpha-BLENDED with no roughness or metalness.
import bpy, sys, time, numpy as np

args = sys.argv[sys.argv.index("--") + 1:]
src, out = args[0], args[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
t = time.time()
bpy.ops.import_scene.fbx(filepath=src)
print(f"[bistro] imported {src} in {time.time() - t:.1f} s")

def gltf_output_group():
    # The exporter reads occlusion from a node group of exactly this name with an "Occlusion" input.
    g = bpy.data.node_groups.get("glTF Material Output")
    if g is None:
        g = bpy.data.node_groups.new("glTF Material Output", "ShaderNodeTree")
        g.interface.new_socket("Occlusion", in_out="INPUT", socket_type="NodeSocketFloat")
    return g

occlusion_cache = {}
def has_occlusion(image):
    # Any texel of R above zero: the channel says something. Decoded once per image.
    if image.name not in occlusion_cache:
        px = np.empty(len(image.pixels), dtype=np.float32)
        image.pixels.foreach_get(px)
        occlusion_cache[image.name] = bool((px[0::image.channels] > 0.5 / 255).any())
    return occlusion_cache[image.name]

alpha_cache = {}
def has_cutout(image):
    # Any texel meaningfully below opaque. Decoded once per image.
    if image.name not in alpha_cache:
        px = np.empty(len(image.pixels), dtype=np.float32)
        image.pixels.foreach_get(px)
        alpha_cache[image.name] = bool((px[3::4] < 0.99).any()) if image.channels == 4 else False
    return alpha_cache[image.name]

counts = {"orm": 0, "occlusion": 0, "mask": 0, "opaque": 0, "no-principled": 0}
for m in bpy.data.materials:
    nt = m.node_tree
    if nt is None:
        continue
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        counts["no-principled"] += 1
        continue
    links = nt.links
    m.use_backface_culling = "DoubleSided" not in m.name
    if "Specular Tint" in bsdf.inputs:
        bsdf.inputs["Specular Tint"].default_value = (1.0, 1.0, 1.0, 1.0)
    bsdf.inputs["Specular IOR Level"].default_value = 0.5

    # ORM: Specular -> Separate Color; G roughness, B metallic, R occlusion.
    spec = next((l for l in links if l.to_node == bsdf and l.to_socket.name == "Specular IOR Level"
                 and l.from_node.type == "TEX_IMAGE"), None)
    if spec is not None:
        image_node = spec.from_node
        links.remove(spec)
        bsdf.inputs["Specular IOR Level"].default_value = 0.5
        image_node.image.colorspace_settings.name = "Non-Color"
        sep = nt.nodes.new("ShaderNodeSeparateColor")
        links.new(image_node.outputs["Color"], sep.inputs["Color"])
        links.new(sep.outputs["Green"], bsdf.inputs["Roughness"])
        links.new(sep.outputs["Blue"], bsdf.inputs["Metallic"])
        if has_occlusion(image_node.image):
            group = nt.nodes.new("ShaderNodeGroup")
            group.node_tree = gltf_output_group()
            links.new(sep.outputs["Red"], group.inputs["Occlusion"])
            counts["occlusion"] += 1
        counts["orm"] += 1

    # Alpha: read from the BaseColor texture itself (the importer loads it a second time for alpha).
    alpha = next((l for l in links if l.to_node == bsdf and l.to_socket.name == "Alpha"), None)
    base = next((l.from_node for l in links if l.to_node == bsdf and l.to_socket.name == "Base Color"
                 and l.from_node.type == "TEX_IMAGE"), None)
    if alpha is not None:
        duplicate = alpha.from_node
        links.remove(alpha)
        if duplicate.type == "TEX_IMAGE" and duplicate != base:
            nt.nodes.remove(duplicate)
    if base is not None and has_cutout(base.image):
        # The exporter writes MASK, cutoff 0.5, for alpha through a Round.
        rnd = nt.nodes.new("ShaderNodeMath")
        rnd.operation = "ROUND"
        links.new(base.outputs["Alpha"], rnd.inputs[0])
        links.new(rnd.outputs[0], bsdf.inputs["Alpha"])
        counts["mask"] += 1
    else:
        bsdf.inputs["Alpha"].default_value = 1.0
        counts["opaque"] += 1

# Images the alpha rewiring orphaned would otherwise be written as copies.
for image in [i for i in bpy.data.images if i.users == 0]:
    bpy.data.images.remove(image)
print(f"[bistro] materials: {counts}")

t = time.time()
bpy.ops.export_scene.gltf(
    filepath=out, export_format="GLTF_SEPARATE", export_texture_dir="textures",
    export_image_format="AUTO", export_yup=True,
    export_lights=True, export_cameras=True, export_animations=True)
print(f"[bistro] exported {out} in {time.time() - t:.1f} s")
