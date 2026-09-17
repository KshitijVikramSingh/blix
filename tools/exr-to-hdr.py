# Blender CLI: OpenEXR -> Radiance .hdr. The probe recipe reads .hdr (stb's RGBE
# decoder); nothing in this tree reads EXR, and adding an EXR decoder to cook one
# sky is a worse trade than converting the sky.
import bpy, sys, os
argv = sys.argv[sys.argv.index("--") + 1:]
src, dst = argv[0], argv[1]
img = bpy.data.images.load(src)
print(f"[convert] {os.path.basename(src)}: {img.size[0]}x{img.size[1]} "
      f"{img.depth}-bit, float={img.is_float}")
img.file_format = 'HDR'
img.filepath_raw = dst
img.save()
print(f"[convert] wrote {dst} ({os.path.getsize(dst)} bytes)")

# Run it as:
#   blender --background --factory-startup --python tools/exr-to-hdr.py -- in.exr out.hdr
#
# Here because Polyhaven and most HDRI sources hand out .exr by default, nothing in
# this tree reads EXR, and `blix cook probe` consumes .hdr. Converting one sky is a
# better trade than carrying an EXR decoder to cook it.
