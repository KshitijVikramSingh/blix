#!/usr/bin/env bash
# Launcher for Blix.Labs.Toolchain.Viewer — the toolchain lab viewer.
#
# Execs the apphost rather than `dotnet run`: Homebrew's $prefix/bin/dotnet is a
# "#!/bin/bash" wrapper and /bin/bash is SIP-protected, so dyld strips DYLD_* from
# its environment and the Vulkan loader path never reaches the app. Same rationale
# as every other launcher here.
#
# --frames N gives a bounded run; the HOST honours it, so this application never
# mentions it.
#
# --model <path.glb> loads an asset as its authored NODE TREE (pivots, bounds, picking).
# --rig   <path.glb> loads one as a SKELETON and its clips (pose, playback, root motion).
#     tools/run-lab.sh --rig src/Blix.Demos.Runner/Assets/models/Rogue.glb
# --clip <name> starts on a clip; --blend <name> / --additive <name> name the second clip
# and pick the composition with it.
#     tools/run-lab.sh --rig .../Rogue.glb --clip Walking_A --blend Running_A
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Blix.Labs.Toolchain.Viewer/Blix.Labs.Toolchain.Viewer.csproj"
APPHOST="$REPO_ROOT/src/Blix.Labs.Toolchain.Viewer/bin/Debug/net8.0/Blix.Labs.Toolchain.Viewer"

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
