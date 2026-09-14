#!/usr/bin/env bash
# Launcher for Blix.Labs.Character.Motion — the character lab's ROOM: contact, with nothing else
# in the picture.
#
# Execs the apphost rather than `dotnet run`: Homebrew's $prefix/bin/dotnet is a
# "#!/bin/bash" wrapper and /bin/bash is SIP-protected, so dyld strips DYLD_* from
# its environment and the Vulkan loader path never reaches the app. Same rationale
# as every other launcher here.
#
# --frames N gives a bounded run; the HOST honours it, so this application never mentions it.
#
# There is no body in this room yet, deliberately — a room with a character in it cannot tell you
# whether a fault is the room's. What it can tell you now:
#   • the slope tint (T, or the panel slider) shades every surface by the normal the COLLIDER
#     reads, so a mis-wound face reads as a floor standing up;
#   • N draws each selected part's wound face normals — the same fact the probe checks in
#     arithmetic, at a glance;
#   • the part list prints each feature's claim and the slope its geometry actually measures.
#
# The headless half needs no GPU and no launcher:
#   dotnet run --project src/Blix.Labs.Character.Probe
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Blix.Labs.Character.Motion/Blix.Labs.Character.Motion.csproj"
APPHOST="$REPO_ROOT/src/Blix.Labs.Character.Motion/bin/Debug/net8.0/Blix.Labs.Character.Motion"

prefix=$(brew --prefix 2>/dev/null || echo "/opt/homebrew")
if [ ! -f "$prefix/lib/libvulkan.dylib" ]; then
    echo "libvulkan.dylib not found under $prefix/lib — install with:" >&2
    echo "  brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc" >&2
    exit 1
fi

if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$prefix/opt/dotnet@8/libexec" ]; then
    export DOTNET_ROOT="$prefix/opt/dotnet@8/libexec"
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

dotnet build "$PROJECT" -c Debug --nologo -v:q
# The Rogue unless told otherwise. The app's own fallback is relative to its binary, which is right
# for a normal build and wrong for a copy run from anywhere else — and "no rig" is a confusing way
# to start a lab whose whole subject is a rig.
if [[ " $* " != *" --rig "* ]]; then
    set -- "$@" --rig "$REPO_ROOT/src/Blix.Demos.Runner/Assets/models/Rogue.glb"
fi

exec "$APPHOST" "$@"
