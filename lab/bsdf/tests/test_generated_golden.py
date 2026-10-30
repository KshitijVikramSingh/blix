"""The engine's EON golden file is generated; the committed one must be what the generator makes now."""
import importlib.util
from pathlib import Path

LAB = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("generate_eon_golden", LAB / "tools" / "generate_eon_golden.py")
GEN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GEN)


def test_committed_golden_is_current():
    assert GEN.OUT.exists() and GEN.OUT.read_text() == GEN.render(), "stale: run lab/bsdf/tools/generate_eon_golden.py"
