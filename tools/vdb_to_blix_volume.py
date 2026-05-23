#!/usr/bin/env python3
"""
VDB -> Blix volume texture converter.

Reads a directory of OpenVDB files (typically one per simulation frame),
resamples each frame's 'flames' grid (temperature/emission scalar) to a
fixed-size cube, and packs N frames stacked along Z into a single dense
3D byte array. The output is a raw binary blob with a small header that
Blix's demo loader can mmap directly into a R8 texture.

Coordinate convention
---------------------
EmberGen/Houdini convention: +Z is UP. Blix's lit shader convention:
+Y is UP. We transpose during sampling so the VDB's Z axis becomes the
output texture's Y axis -- i.e. the flame "rises" in texture-V as the
shader expects.

Output format
-------------
Header (32 bytes -- 4-byte magic plus seven uint32 fields):
  magic            char[4]   = b"BVOL"
  version          uint32    = 1
  width            uint32    voxels per slice X
  height           uint32    voxels per slice Y
  depth_per_frame  uint32    voxels per slice Z (one frame's Z extent)
  frame_count      uint32    number of frames packed
  channels         uint32    1 (R8) for now
  reserved         uint32    padding to 40 bytes
Body:
  width * height * (depth_per_frame * frame_count) * channels bytes
  Row-major within slice (X fastest), then Y, then Z. Frames stack
  contiguously along Z.

Usage
-----
  python3 vdb_to_blix_volume.py <src_vdb_dir> <out_path>
"""

import argparse
import struct
import sys
from pathlib import Path

import numpy as np
import openvdb

# Tunables. Picked so the final 3D texture stays under GL's typical
# GL_MAX_3D_TEXTURE_SIZE = 2048 in any single dimension.
RES = 48              # per-frame voxel resolution (cube; X=Y=Z)
FRAME_STRIDE = 6      # take every Nth frame
GRID_NAME = "flames"  # temperature/emission scalar; matches the shader's needs


def resample_grid_to_cube(grid, unified_bbox: tuple, res: int) -> np.ndarray:
    """Sample `grid` at a uniform RES x RES x RES lattice that covers the
    full unified bbox. Returns float32 array of shape (res, res, res) in
    VDB-native (X, Y, Z) order; Z is sim's up direction."""
    (mn, mx) = unified_bbox
    mn = np.asarray(mn, dtype=np.float64)
    mx = np.asarray(mx, dtype=np.float64)
    span = mx - mn

    # Fast path: copy the bbox region into a dense array, then resample
    # with numpy. This is much faster than per-voxel sampling.
    dim = (mx - mn + 1).astype(int)
    dense = np.zeros(tuple(dim), dtype=np.float32)
    grid.copyToArray(dense, ijk=tuple(int(v) for v in mn))

    # Trilinear resample to RES^3 via index gather + bilinear interp.
    # cheaper than scipy.ndimage.zoom and avoids the dep.
    idx_x = np.linspace(0, dim[0] - 1, res)
    idx_y = np.linspace(0, dim[1] - 1, res)
    idx_z = np.linspace(0, dim[2] - 1, res)

    fx = idx_x[:, None, None]
    fy = idx_y[None, :, None]
    fz = idx_z[None, None, :]

    ix = np.clip(fx.astype(int), 0, dim[0] - 2)
    iy = np.clip(fy.astype(int), 0, dim[1] - 2)
    iz = np.clip(fz.astype(int), 0, dim[2] - 2)
    tx = fx - ix
    ty = fy - iy
    tz = fz - iz

    def gather(dx, dy, dz):
        return dense[ix + dx, iy + dy, iz + dz]

    c000 = gather(0, 0, 0)
    c100 = gather(1, 0, 0)
    c010 = gather(0, 1, 0)
    c110 = gather(1, 1, 0)
    c001 = gather(0, 0, 1)
    c101 = gather(1, 0, 1)
    c011 = gather(0, 1, 1)
    c111 = gather(1, 1, 1)

    c00 = c000 * (1 - tx) + c100 * tx
    c10 = c010 * (1 - tx) + c110 * tx
    c01 = c001 * (1 - tx) + c101 * tx
    c11 = c011 * (1 - tx) + c111 * tx
    c0 = c00 * (1 - ty) + c10 * ty
    c1 = c01 * (1 - ty) + c11 * ty
    return c0 * (1 - tz) + c1 * tz


def main():
    parser = argparse.ArgumentParser(description="VDB sequence -> Blix volume blob")
    parser.add_argument("src_dir", help="directory containing *.vdb files")
    parser.add_argument("out_path", help="output .bvol path")
    args = parser.parse_args()

    src_dir = Path(args.src_dir)
    vdbs = sorted(src_dir.glob("*.vdb"))
    if not vdbs:
        print(f"no .vdb files in {src_dir}", file=sys.stderr)
        sys.exit(1)

    # Pass 1: find the unified bbox across the FRAMES WE'LL ACTUALLY USE.
    # This way the resample lattice covers the full extent the flame ever
    # reaches, so frame-to-frame motion stays consistent in texture space.
    picked = vdbs[::FRAME_STRIDE]
    print(f"{len(vdbs)} VDB frames found; using every {FRAME_STRIDE}th = {len(picked)} frames")

    print("Pass 1: scanning for unified bbox...")
    bbox_mn = np.array([np.iinfo(np.int32).max] * 3)
    bbox_mx = np.array([np.iinfo(np.int32).min] * 3)
    for f in picked:
        g = openvdb.read(str(f), GRID_NAME)
        bb = g.evalActiveVoxelBoundingBox()
        bbox_mn = np.minimum(bbox_mn, np.asarray(bb[0]))
        bbox_mx = np.maximum(bbox_mx, np.asarray(bb[1]))
    span = bbox_mx - bbox_mn + 1
    print(f"  unified bbox = {tuple(bbox_mn)} .. {tuple(bbox_mx)}  span = {tuple(span)}")

    # Pass 2: resample each frame into the unified lattice + accumulate.
    # We swap axes so VDB's Z-up becomes our Y-up: store as (z, y, x) which
    # numpy.tobytes() laid out row-major produces an array with X-fastest
    # in memory, exactly what glTexImage3D expects.
    print("Pass 2: resampling + packing...")
    bbox = (bbox_mn, bbox_mx)
    frames_packed = np.empty((len(picked), RES, RES, RES), dtype=np.float32)

    # Two-pass normalisation: first compute global max so the R8 quantisation
    # doesn't clip frames where the flame happens to peak harder than others.
    for i, f in enumerate(picked):
        g = openvdb.read(str(f), GRID_NAME)
        arr_xyz = resample_grid_to_cube(g, bbox, RES)
        # Swap: VDB (x, y, z) where z is up -> output (depth, height, width)
        # where height is up. transpose(2, 1, 0) yields (z, y, x); we want
        # (vdb_y, vdb_z, vdb_x) so the depth axis (vdb_y) gets stacked, and
        # the texture height axis comes from VDB's z (up).
        out = arr_xyz.transpose(1, 2, 0)  # -> (vdb_y, vdb_z, vdb_x)
        frames_packed[i] = out
        if i % 8 == 0:
            print(f"  frame {i}/{len(picked)}  max={arr_xyz.max():.3f}")

    global_max = frames_packed.max()
    print(f"  global max temperature = {global_max:.3f}")
    if global_max <= 0.0:
        print("ERROR: all frames are empty", file=sys.stderr)
        sys.exit(2)

    # Quantise to R8. Scale so global_max -> 255. The shader divides by the
    # implicit 255 normalisation when reading; the scale factor is recoverable
    # from the original normalisation if we want to push HDR back later.
    normalised = np.clip(frames_packed / global_max, 0.0, 1.0)
    bytes_array = (normalised * 255.0 + 0.5).astype(np.uint8)
    # bytes_array shape: (frames, depth_per_frame, height, width)
    # Stack frames along the depth axis: concat into (frames * depth, height, width)
    stacked = bytes_array.reshape(-1, RES, RES)
    print(f"  output dims: width={RES} height={RES} depth_per_frame={RES} frames={len(picked)}")
    print(f"  total: {stacked.shape}  ({stacked.nbytes} bytes)")

    # Write header + body
    out_path = Path(args.out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("wb") as fh:
        header = struct.pack(
            "<4sIIIIIII",
            b"BVOL",
            1,                 # version
            RES,               # width
            RES,               # height
            RES,               # depth_per_frame
            len(picked),       # frame_count
            1,                 # channels (R8)
            0,                 # reserved
        )
        fh.write(header)
        fh.write(stacked.tobytes())

    print(f"\nwrote {out_path}  ({out_path.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
