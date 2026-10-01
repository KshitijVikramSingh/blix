#!/usr/bin/env bash
# Studio's regression control: a fixed set of captures, hashed, so a change that should
# alter nothing can be shown to have altered nothing.
#
#   tools/lab-baseline.sh record [dir]     # default .baseline/ (gitignored)
#   tools/lab-baseline.sh check  [dir]     # re-run and diff against what is recorded
#   tools/lab-baseline.sh list             # the modes, and why each is here
#
# ── What is hashed, and why it is not just the PNG ─────────────────────────────
# Each mode records TWO things: the image, and the tool's own filtered stdout. The stdout
# is the half that earns its place. The capture tool already prints a per-body pose
# fingerprint, a distinct-pose verdict and an integrated root travel — numbers that state
# what the picture only implies, and that differ when two bodies silently share a pose or
# a root delta lands on the wrong one.
#
# ── The blind spot this set was widened to close ───────────────────────────────
# The eleven modes this grew from were all SINGLE-BODY wherever motion was involved: every
# --drive-root mode ran one instance. So it was structurally blind to the whole multi-body
# class, and all three faults of that class during the inlet arc — root motion fanning out
# to every body, travel distance read from body 0, reset resetting only body 0 — were found
# by a person running the viewer and noticing. An instrument that cannot fail on a bug is
# not evidence about that bug. The inst3-travel / inst5-travel / inst3-travel-lockstep modes
# below exist for exactly that class, and 5 is there because an off-by-one can pass at 3.
#
# ── Modes that need the corpus ─────────────────────────────────────────────────
# Marked "corpus:". They are SKIPPED when third_party/gltf-corpus is absent — but `check`
# then FAILS rather than passing quietly, because a control that silently shrinks to the
# modes you happen to be able to run reports green for the wrong reason. Conventions §5:
# a green build is not evidence that a build step ran.
#     tools/fetch-gltf-corpus.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

ACTION="${1:-record}"
DIR="${2:-$REPO_ROOT/.baseline}"
# An array, not a string: the front door is a script plus a verb, and "$CAPTURE"
# as one word would look for a file literally named "blix shot".
CAPTURE=("$REPO_ROOT/blix" shot)
CORPUS="$REPO_ROOT/third_party/gltf-corpus"

ROGUE="src/Demos/Blix.Demos.Runner/Assets/models/Rogue.glb"
TANK="src/Demos/Blix.Demos.TankArena/Assets/models/tank.glb"
CRATE="src/Demos/Blix.Demos.TankArena/Assets/models/crate.glb"

# ── The modes ──────────────────────────────────────────────────────────────────
# One per line: <name> <TAB> <args> <TAB> <why>. A leading "corpus:" on the args means it
# needs the fetched corpus; a leading "judge:" means the mode asserts an EXIT CODE rather
# than an image — its expected code follows the colon.
modes() {
cat <<'MODES'
plain	--frames 10	The empty stage. Everything else is drawn on top of this, so it fails first when the stage changes.
static	--model $CRATE	One static model, one material. The smallest thing that can go wrong.
tank	--model $TANK	THREE skins and two static mesh nodes. Neither was read before the inlet arc: the importer took one skin and dropped anything not under a joint.
clip	--model $ROGUE --clip Walking_A --time 0.35 --xray	A named pose at a named instant — the smallest reproducible rig picture.
attach	--model $ROGUE --clip Walking_A --time 0.35 --attach 1H_Crossbow --xray	An attachment following a joint, which is a different thing from a static part.
mask	--model $ROGUE --clip Walking_A --mask-root spine --mask-falloff 2 --skeleton-only --xray --zoom 2	A layer mask as a picture of the mask, not of what it was used for.
advance	--model $ROGUE --clip Dodge_Forward --advance 2.0 --drive-root	Root motion integrated over five loop cycles, single body. The travel number in the log is the assertion.
inst3	--model $ROGUE --instances 3 --xray	Three bodies, three poses, one draw.
lockstep	--model $ROGUE --instances 3 --lockstep --xray	The negative control for inst3: one clip at one instant must yield ONE distinct pose. Without it "they differ" proves nothing.
viewport	--model $ROGUE --instances 3 --viewport --xray	Two cameras, two files. Writes viewport.scene.png too — if the pair shows one camera, something upstream is sharing view state.
inst3-travel	--model $ROGUE --clip Dodge_Forward --advance 2.0 --drive-root --instances 3 --xray	MULTI-BODY ROOT MOTION. The hole the old set had: root motion applied to one body and fanned to all three was invisible to every single-body mode.
inst3-travel-lockstep	--model $ROGUE --clip Dodge_Forward --advance 2.0 --drive-root --instances 3 --lockstep --xray	Its negative control: driven from one clip at one phase, the three must stay one distinct pose while still travelling.
inst5-travel	--model $ROGUE --clip Dodge_Forward --advance 2.0 --drive-root --instances 5 --xray	Five, because an index fault that reads body 0 or body N-1 can pass at three.
selftest	judge:0 --stage-selftest --frames 6	Rung four: a tool declares a pass of its own and the stage must record it. A hook nothing calls is a hook that rots.
vertexcolor	corpus: --model $CORPUS/sample-assets/BoxVertexColors/BoxVertexColors.glb	COLOR_0 as a base-colour multiplier. Rendered WHITE until I-A, and no asset in this tree could have shown it: all 33 nature-kit primitives use COLOR_0 as greyscale baked AO.
blend	corpus: --model $CORPUS/sample-assets/AlphaBlendModeTest/AlphaBlendModeTest.glb	OPAQUE/MASK/BLEND side by side with real alpha. Rendered fully opaque before I-B.
alphamask	corpus: --model $CORPUS/generator/Material_AlphaMask/Material_AlphaMask_05.gltf	The cutoff whose baseColorFactor.a of 0.7 makes its EFFECTIVE cutoff 0.571 — it looked identical to _01 while baseAlpha was hardcoded to 1.
doublesided	corpus: --model $CORPUS/generator/Material_DoubleSided/Material_DoubleSided_00.gltf	doubleSided, which is pipeline state in Vulkan and so cannot ride a push constant.
rigcutout	corpus: --model $CORPUS/sample-assets/RiggedSimple/RiggedSimple_cutout.gltf --attach attach_kept --attach attach_dropped	A rig's static parts and attachments honour alpha: each MASK pair keeps one quad and drops one, BLEND is translucent, and neither dropped nor blended casts. All five drew solid, and cast, before.
bones300	corpus: --model $CORPUS/sample-assets/RiggedSimple/RiggedSimple_bones300.gltf --instances 2	A skin past the old 128-bone cap, weighted by joints 300 and 301: must match RiggedSimple --instances 2 pixel for pixel, the second body bent. Byte-truncated indices lose the bend (42k pixels).
nodeanim	corpus: --model $CORPUS/sample-assets/BoxAnimated/BoxAnimated.glb --clip anim --time 0.3	Node animation with no skin: the inner box is lifted out of the outer one at 0.3 s. Drawn frozen before the animated hierarchy (clips reached only skin 0's joints).
samplers	corpus: --model $CORPUS/sample-assets/TextureSettingsTest/TextureSettingsTest.glb --zoom 1.6	glTF samplers: repeat and mirror show checkmarks, clamp shows solid green (README). Mirror was impossible before v15 (no mirrored wrap). The single-sided row's red X is the static pipeline not culling, plan.md §I.
uvtransform	corpus: --model $CORPUS/sample-assets/TextureTransformMultiTest/TextureTransformMultiTest.glb --zoom 1.3	KHR_texture_transform on every core channel and TEXCOORD_1: checkmarks; the transform bypassed shows the asset's fail symbol. Clearcoat rows are dark: Studio does not draw clearcoat (plan.md §I 3c).
negscale	corpus: --model $CORPUS/sample-assets/NegativeScaleTest/NegativeScaleTest.glb	Mirrored nodes: single-sided culling follows the reversed winding (every row a checkmark), and the -1.0 column's doubleSided spheres light like the 1.0 column's (README). Needs the front face to follow the determinant even unculled.
uv1	corpus: --model $CORPUS/sample-assets/MultiUVTest/MultiUVTest_uv1.gltf	A texture naming texCoord 1. The locally derived asset — upstream ships the second UV set but no material that names it, so the stock file cannot tell the two readers apart.
interleaved	corpus: --model $CORPUS/cesium/BoxInterleaved/BoxInterleaved.gltf	Interleaved attributes. A vertex buffer is walked at the PIPELINE's stride, so a mismatch reads as garbage geometry rather than a missing attribute.
nonormals	corpus: --model $CORPUS/cesium/BoxNoNormals/BoxNoNormals.gltf	No NORMAL attribute at all. Blix's behaviour here is recorded rather than asserted — this mode exists so a change to it is noticed.
negative-noposition	corpus:judge:1 --model $CORPUS/negative/Mesh_NoPosition/Mesh_NoPosition_00.gltf	Must be REFUSED, exit 1, by NAME. It aborted with a stack trace (134) until the refusal was made to cover the import and not only the parse — valid glTF JSON, invalid mesh.
negative-restart	corpus:judge:1 --model $CORPUS/negative/Mesh_PrimitiveRestart/Mesh_PrimitiveRestart_00.gltf	Must be REFUSED. Primitive restart is not in glTF. A reader that accepts these passes every positive test in this file.
MODES
}

if [ "$ACTION" = list ]; then
    modes | while IFS="$(printf '\t')" read -r name args why; do printf '%-24s %s\n' "$name" "$why"; done
    exit 0
fi

# stdout is evidence only in the part that is the same on any machine and any run. The GPU
# and audio banner name this laptop; a decode time is a stopwatch; an absolute path is this
# checkout. Strip those and what is left is what the tool CONCLUDED.
# -E throughout: BSD sed does not honour \? in a basic regex, so the first version of the
# duration strip silently matched nothing and every log carried "decoded 1 images in 34 ms".
# A filter that quietly fails to filter makes every check red for no reason, which is the
# fastest way to get a control ignored.
filter() {
    sed -E -e "s#$1/?##g" \
        -e "s#$REPO_ROOT/##g" \
        -e 's#/private/tmp/[^ ]*/##g' \
        -e 's#/var/folders/[^ ]*/##g' \
        -e 's/[0-9]+(\.[0-9]+)? ?ms/<ms>/g' \
        -e '/^\[teardown\]/d' \
        -e '/^Graphics:/d' -e '/^OpenAL:/d' -e '/^Audio:/d' \
        -e '/Build succeeded/d' -e '/Warning\(s\)/d' -e '/Error\(s\)/d' -e '/Time Elapsed/d' \
        -e '/^ *$/d'
}

# <b>The teardown trace is ON here and filtered out of what gets hashed.</b> A mode that exits 139
# has historically left nothing but "Exiting after N frames" to go on; with this the raw output names
# the teardown step, and the raw is KEPT whenever a mode's exit code is not what was expected.
export BLIX_TEARDOWN_TRACE=1

RUN="$DIR"
if [ "$ACTION" = check ]; then
    RUN="$(mktemp -d "${TMPDIR:-/tmp}/lab-baseline-run.XXXXXX")"
    trap 'rm -rf "$RUN"' EXIT
fi
mkdir -p "$RUN"

have_corpus=1
[ -d "$CORPUS" ] || have_corpus=0
skipped=0
unlit=0
# Percent of a capture that must be lit. Every mode draws the stage's sky and ground, so a real
# capture sits near 100; the MSAA read-back fault that lost the scene left about 5 (the lines).
LIT_FLOOR=50

echo "── recording into $RUN"
while IFS="$(printf '\t')" read -r name args why; do
    case "$args" in
    corpus:*)
        args="${args#corpus:}"
        if [ "$have_corpus" -eq 0 ]; then
            echo "  SKIP $name (no corpus — run tools/fetch-gltf-corpus.sh)"
            skipped=$((skipped + 1))
            continue
        fi ;;
    esac

    want=0
    case "$args" in judge:*) want="${args#judge:}"; want="${want%% *}"; args="${args#judge:$want}" ;; esac

    # The mode table names models through variables so a path lives in exactly one place.
    args=$(eval "printf '%s' \"$args\"")

    set +e
    # shellcheck disable=SC2086
    "${CAPTURE[@]}" $args --out "$RUN/$name.png" > "$RUN/$name.raw" 2>&1
    code=$?
    set -e
    filter "$RUN" < "$RUN/$name.raw" > "$RUN/$name.log"
    echo "$code" > "$RUN/$name.exit"

    if [ "$code" != "$want" ]; then
        echo "  FAIL $name — exit $code, expected $want"
        # The raw survives a failure, trace and all. It is the only record of where a crash was.
        echo "        raw output kept at $RUN/$name.raw"
        grep '^\[teardown\]' "$RUN/$name.raw" | tail -3 | sed 's/^/        /'
        sed -n '1,4p' "$RUN/$name.log" | sed 's/^/        /'
    else
        rm -f "$RUN/$name.raw"
        echo "  ok   $name"
    fi

    # A picture with no scene in it hashes as stably as one with, so a stable hash cannot say the
    # capture saw anything. Shot reports each image's lit fraction; hold every one to the floor.
    while read -r image percent; do
        if [ "$percent" -lt "$LIT_FLOOR" ]; then
            echo "  UNLIT $name — $image is $percent% lit, under the $LIT_FLOOR% floor"
            unlit=$((unlit + 1))
        fi
    done < <(sed -n 's/^\(.*\.png\): \([0-9]*\)% of pixels lit$/\1 \2/p' "$RUN/$name.log")
done < <(modes)

if [ "$unlit" -gt 0 ]; then
    echo "REFUSING: $unlit capture(s) hold no scene. The read-back lost the picture; nothing here is a baseline." >&2
    exit 3
fi

# Hash everything the run produced. --viewport writes a second PNG nothing named, so the
# manifest is built by LOOKING at the directory rather than from the mode list: a file a
# mode produces and the list does not mention is exactly the file a rename would lose.
( cd "$RUN" && find . -type f ! -name MANIFEST.sha256 | sort | xargs shasum -a 256 ) > "$RUN/MANIFEST.sha256"
echo "  $(wc -l < "$RUN/MANIFEST.sha256" | tr -d ' ') artifact(s) hashed"

if [ "$ACTION" != check ]; then
    [ "$skipped" -eq 0 ] || echo "  NOTE: $skipped corpus mode(s) skipped — this baseline is partial"
    echo "recorded. compare a later build with: tools/lab-baseline.sh check $DIR"
    exit 0
fi

REF="$DIR/MANIFEST.sha256"
[ -f "$REF" ] || { echo "no baseline at $DIR — record one first" >&2; exit 2; }

echo
echo "── checking against $DIR"
if [ "$skipped" -gt 0 ]; then
    echo "REFUSING to pass: $skipped corpus mode(s) could not run, so this check covers less" >&2
    echo "than the recorded baseline. Run tools/fetch-gltf-corpus.sh." >&2
    exit 2
fi

if diff <(sort "$REF") <(sort "$RUN/MANIFEST.sha256") > /tmp/lab-baseline.diff 2>&1; then
    echo "IDENTICAL — $(wc -l < "$REF" | tr -d ' ') artifact(s) byte-for-byte"
    exit 0
fi

echo "DIFFERS:"
# Name what changed rather than printing two columns of hashes. A .log difference is the
# informative one — it says what the tool concluded differently — so print it.
awk '/^[<>]/ { print $NF }' /tmp/lab-baseline.diff | sed 's#^\./##' | sort -u | while read -r f; do
    if [ ! -f "$RUN/$f" ]; then echo "  GONE    $f"
    elif [ ! -f "$DIR/$f" ]; then echo "  NEW     $f"
    else
        echo "  CHANGED $f"
        # sed -n '1,12p' rather than `| head -12`: head closes the pipe early, and under
        # `set -o pipefail` that SIGPIPE ends the whole report after the first changed file.
        case "$f" in *.log) diff "$DIR/$f" "$RUN/$f" | sed -n '1,12p' | sed 's/^/          /' || true ;; esac
    fi
done
exit 1
