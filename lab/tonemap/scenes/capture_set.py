"""Capture the lab's scene x lighting variants through Studio's `blix shot`, raw HDR beside each PNG.

    uv run python scenes/capture_set.py [--only NAME ...]

Run from lab/tonemap. Writes data/<variant>.{png,pfm,pfm.json} and data/captures.json (the flags behind each
file, so a capture can be reproduced). Studio's look flags are the engine's own [Tune] settings; nothing here
changes the renderer. Each capture takes seconds and opens a window briefly.

The variants span the HDR statistics a tonemapper has to cover: `dim` is Studio's authored light, `bright` is
the sun at the top of its range (about 9.6 in the sun tint's units, Sponza's order), `dusk` is a low, dim sky
and `grazing` is a low bright sun with long shadows and a hot horizon.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
DATA = LAB / "data"

LIGHTING = {
    "dim": [],
    "bright": ["--sun-intensity", "3", "--ambient-strength", "0.5"],
    "dusk": ["--sun-intensity", "0.15", "--horizon-brightness", "0.25", "--zenith-brightness", "0.25",
             "--ambient-strength", "0.3"],
    "grazing": ["--sun-intensity", "3", "--sun-elevation", "8", "--horizon-brightness", "2", "--halo-strength", "3",
                "--ambient-strength", "0.3"],
    # Night: the sun and sky nearly off, so a scene is lit by what is IN it. Added for night_lamps.
    "night": ["--sun-intensity", "0.03", "--horizon-brightness", "0.05", "--zenith-brightness", "0.03", "--halo-strength", "0",
              "--ambient-strength", "0.05"],
}
# Which lighting each scene is captured under. The night scene is only meaningful dark; the stage scenes keep theirs.
# Extra lightings used only by the metering set, for variety in how bright the sky and ground are against the objects.
METERING_LIGHTING = {
    "overcast": ["--sun-intensity", "0.3", "--horizon-brightness", "1.5", "--zenith-brightness", "1.5", "--halo-strength", "0",
                 "--ambient-strength", "0.5"],
    "hazy": ["--sun-intensity", "1.5", "--halo-strength", "3", "--horizon-brightness", "2", "--ambient-strength", "0.4"],
}
METERING_ZOOMS = (1.0, 1.7, 3.0)  # how much of the frame is sky and ground against the subject
SCENES = {"materials_grid": ("dim", "bright", "dusk", "grazing"), "emissive_ramp": ("dim", "bright", "dusk", "grazing"),
          "night_lamps": ("night", "dusk")}
COMMON = ["--zoom", "1.7", "--no-gizmos"]


def metering():
    """data/metering/<scene>__<lighting>__z<zoom>.pfm: a lighting x zoom grid of small (640x360) frames.

    The exposure fit (tmlab.exposure) learns from how a frame's luminance is distributed, and a distribution depends on
    the lighting and on how much of the frame the sky, ground and subject take, so both vary. Each frame is captured at
    full size, reduced by 4 (a box average, which keeps radiance honest) and the large file removed.
    """
    sys.path.insert(0, str(LAB))
    from tmlab import capture

    out = DATA / "metering"
    out.mkdir(parents=True, exist_ok=True)
    tmp = DATA / "_tmp"
    tmp.mkdir(exist_ok=True)
    lighting = {**LIGHTING, **METERING_LIGHTING}
    plan = []
    for scene, lights in SCENES.items():
        extra = tuple(METERING_LIGHTING) if scene != "night_lamps" else ("overcast",)
        for light in (*lights, *extra):
            for zoom in METERING_ZOOMS:
                plan.append((scene, light, zoom))
    done = 0
    for scene, light, zoom in plan:
        name = f"{scene}__{light}__z{zoom:g}"
        if (out / f"{name}.pfm").exists():
            continue
        png = tmp / f"{name}.png"
        flags = ["--zoom", str(zoom), "--no-gizmos", *lighting[light]]
        cmd = [str(REPO / "blix"), "shot", "--model", str(DATA / "scenes" / f"{scene}.glb"), "--out", str(png), *flags]
        env = {**os.environ, "BLIX_SHOT_HDR": "1", "BLIX_CONFIG": "Release"}
        r = subprocess.run(cmd, cwd=REPO, env=env, capture_output=True, text=True, timeout=300)
        if r.returncode != 0 or not png.with_suffix(".pfm").exists():
            print(r.stdout[-1200:], r.stderr[-1200:], file=sys.stderr)
            sys.exit(f"{name}: capture failed (exit {r.returncode})")
        cap = capture.load(png.with_suffix(".pfm")).downsampled(4)
        capture.write_pfm(out / f"{name}.pfm", cap.rgb, {**cap.meta, "scene": scene, "lighting": light, "zoom": zoom})
        for f in tmp.glob(f"{name}.*"):
            f.unlink()
        done += 1
        print(f"[{done}/{len(plan)}] {name}", flush=True)
    print(f"metering set: {len(plan)} frames in {out}")


def main():
    if "--metering" in sys.argv:
        return metering()
    only = set(sys.argv[sys.argv.index("--only") + 1:]) if "--only" in sys.argv else None
    scenes_dir = DATA / "scenes"
    for sc in SCENES:
        if not (scenes_dir / f"{sc}.glb").exists():
            sys.exit(f"{sc}.glb missing: run `uv run python scenes/make_scenes.py` first")

    manifest = {}
    for scene, lights in SCENES.items():
        for light in lights:
            flags = LIGHTING[light]
            name = f"{scene}__{light}"
            if only and name not in only:
                continue
            out = DATA / f"{name}.png"
            cmd = [str(REPO / "blix"), "shot", "--model", str(scenes_dir / f"{scene}.glb"), "--out", str(out),
                   *COMMON, *flags]
            env = {**os.environ, "BLIX_SHOT_HDR": "1", "BLIX_CONFIG": "Release"}
            print("capturing", name, flush=True)
            done = subprocess.run(cmd, cwd=REPO, env=env, capture_output=True, text=True, timeout=300)
            if done.returncode != 0 or not out.with_suffix(".pfm").exists():
                print(done.stdout[-1500:], done.stderr[-1500:], file=sys.stderr)
                sys.exit(f"{name}: capture failed (exit {done.returncode})")
            manifest[name] = {"scene": scene, "lighting": light, "flags": COMMON + flags}

    path = DATA / "captures.json"
    merged = json.loads(path.read_text()) if path.exists() else {}
    merged.update(manifest)
    path.write_text(json.dumps(merged, indent=1))
    print(f"wrote {len(manifest)} capture(s); manifest {path}")


if __name__ == "__main__":
    main()
