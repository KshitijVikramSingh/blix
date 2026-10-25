"""The GT7 numbers the engine ships are generated; the committed files must be what the generator makes now."""
import importlib.util
from pathlib import Path

LAB = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("generate_gt7_constants", LAB / "tools" / "generate_gt7_constants.py")
GEN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GEN)


def test_committed_files_are_what_the_generator_produces():
    stale = [p.name for p, text in GEN.render().items() if not p.exists() or p.read_text() != text]
    assert not stale, f"stale generated files {stale}: run lab/tonemap/tools/generate_gt7_constants.py"


def test_the_tuned_set_is_the_chosen_hdr_fit():
    import json
    chosen = json.loads((LAB / "results/hdr_tuned.json").read_text())["params"]
    assert GEN.presets()["Tuned"].alpha == chosen["alpha"]
    assert GEN.presets()["Published"].alpha == 0.25 and GEN.presets()["Published"].shoulder_tail == 0.0
