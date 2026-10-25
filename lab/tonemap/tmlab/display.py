"""What the lab knows about the display, from the display.

    uv run python -m tmlab.display [--peak 1600]

macOS reports an EDR headroom (how far above SDR white content may go) and the most it could report (at the lowest
brightness). The panel's PEAK in nits is not reported, only stated in its specification, so what follows is an inference and
is labelled as one:

    UI white (nits)  ~=  peak / current headroom
    lowest UI white  ~=  peak / potential headroom

On the author's Liquid Retina XDR (spec: 1600 nits peak, 1000 sustained): potential 16.0 gives a lowest UI white of 100 nits,
which is the figure macOS brightness bottoms out at on these panels, so the 1600 peak and the 16.0 agree; a current headroom
of 3.998 then puts the UI white at about 400 nits at the present brightness setting. Neither the peak nor the white is
measured: a photometer would. The relative behaviour of the demo (the shoulder ends where the display does) does not depend
on it; how literally a "nit" can be read does.
"""
from __future__ import annotations

import argparse
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path

PROBE = Path(__file__).resolve().parents[1] / "tools" / "edr_probe.swift"
_LINE = re.compile(r"screen (\d+) '(.*)': current ([0-9.]+) potential ([0-9.]+) scale ([0-9.]+)")


@dataclass(frozen=True)
class Screen:
    index: int
    name: str
    headroom: float
    potential: float
    scale: float

    def ui_white_nits(self, peak_nits: float) -> float:
        """The UI white the current headroom implies for a panel whose peak is `peak_nits` (an inference)."""
        return peak_nits / max(self.headroom, 1.0)

    def lowest_ui_white_nits(self, peak_nits: float) -> float:
        return peak_nits / max(self.potential, 1.0)


def parse(text: str) -> list[Screen]:
    return [Screen(int(m[1]), m[2], float(m[3]), float(m[4]), float(m[5])) for m in map(_LINE.match, text.splitlines()) if m]


def probe() -> list[Screen]:
    done = subprocess.run(["swift", str(PROBE)], capture_output=True, text=True, timeout=180)
    screens = parse(done.stdout)
    if not screens:
        raise RuntimeError(f"edr_probe printed no screens: {done.stdout[-300:]} {done.stderr[-300:]}")
    return screens


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--peak", type=float, default=1600.0, help="the panel's specified peak, nits")
    a = ap.parse_args()
    for s in probe():
        print(f"{s.name}: headroom {s.headroom:.3f} (potential {s.potential:.1f}); with a {a.peak:g}-nit peak, "
              f"UI white is about {s.ui_white_nits(a.peak):.0f} nits now and bottoms out at about {s.lowest_ui_white_nits(a.peak):.0f}")
        if s.headroom < 1.05:
            print("  (headroom reads about 1.0: no extended-range window is on screen yet; open the HDR demo and probe again)")


if __name__ == "__main__":
    main()
