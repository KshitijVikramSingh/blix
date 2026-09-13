#!/usr/bin/env bash
# Launcher for Blix.Labs.Toolchain.Capture — the toolchain lab capture tool.
#
# Execs the apphost rather than `dotnet run`: Homebrew's $prefix/bin/dotnet is a
# "#!/bin/bash" wrapper and /bin/bash is SIP-protected, so dyld strips DYLD_* from
# its environment and the Vulkan loader path never reaches the app. Same rationale
# as every other launcher here.
#
# --frames N gives a bounded run; the HOST honours it, so this application never
# mentions it.
#
# --model / --rig as the viewer. A rig capture is reproducible: --clip <name> --time <s>
# samples once and never runs the clock, and --xray drops depth testing on gizmos so a
# skeleton inside an opaque mesh is visible at all.
#     tools/run-lab-capture.sh --rig .../Rogue.glb --clip Walking_A --time 0.35 --xray --out walk.png
# --advance <s> runs a fixed number of fixed steps (still no wall clock) and draws the
# integrated root path; --drive-root strips the root and moves the body by the delta.
#     tools/run-lab-capture.sh --rig .../Rogue.glb --clip Dodge_Forward --advance 2.0 --drive-root
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Blix.Labs.Toolchain.Capture/Blix.Labs.Toolchain.Capture.csproj"
APPHOST="$REPO_ROOT/src/Blix.Labs.Toolchain.Capture/bin/Debug/net8.0/Blix.Labs.Toolchain.Capture"

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
exec "$APPHOST" "$@"
