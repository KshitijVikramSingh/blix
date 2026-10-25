"""The push-constant blocks are written by C# structs and read by GLSL blocks; a swap between them silently scrambles every value
(it once made the scatter mix 1.0 and the whole screen a blur). Field order and count must agree."""
import re
from pathlib import Path

import pytest

DEMO = Path(__file__).resolve().parents[3] / "src/Demos/Blix.Demos.TonemapLab"
PROGRAM = (DEMO / "Program.cs").read_text()


def csharp_fields(struct: str) -> list[str]:
    body = re.search(r"struct %s\n\{(.*?)\n    public readonly byte\[\] ToBytes" % struct, PROGRAM, re.S).group(1)
    names = []
    for line in body.splitlines():
        m = re.match(r"\s*public (float|int) (.*?);", line)
        if m:
            names += [n.strip() for n in m.group(2).split("//")[0].split(",")]
    return names


def glsl_fields(shader: str) -> list[str]:
    src = (DEMO / "Shaders" / shader).read_text()
    block = re.search(r"layout\(push_constant\) uniform Push \{(.*?)\} pc;", src, re.S).group(1)
    return re.findall(r"^\s*(?:float|int) (\w+);", block, re.M)


@pytest.mark.parametrize("struct,shader", [("ScenePush", "scene.frag"), ("PresentPush", "present.frag")])
def test_struct_and_block_agree_in_order(struct, shader):
    cs = [n.lower() for n in csharp_fields(struct)]
    gl = [n[1:].lower() for n in glsl_fields(shader)]   # drop the leading u
    # the C# names are not the GLSL names word for word (Blur samples vs BlurSamples...), so compare counts and the shared stems
    assert len(cs) == len(gl), (cs, gl)
    for c, g in zip(cs, gl):
        assert c == g or c in g or g in c or c.replace("hdri", "hdri") == g, (c, g, cs, gl)
