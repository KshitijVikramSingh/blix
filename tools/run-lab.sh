#!/usr/bin/env bash
# Compatibility launcher for Blix.Tools.View — the model and rig viewer.
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
#     tools/run-lab.sh --rig src/Demos/Blix.Demos.Runner/Assets/models/Rogue.glb
# --clip <name> starts on a clip; --blend <name> / --additive <name> name the second clip
# and pick the composition with it.
#     tools/run-lab.sh --rig .../Rogue.glb --clip Walking_A --blend Running_A
# --mask <clip> composes that clip onto the first through a bone mask, and --mask-root <bone>
# names the mask's root (default: the rig's own spine, guessed). The Mask panel moves the root and
# the falloff live, and the drawn skeleton is coloured by the weights.
#     tools/run-lab.sh --rig .../Rogue.glb --clip Walking_A --mask Unarmed_Melee_Attack_Punch_A
# --instances N draws N copies of the rig, each on its own clock (max 8).
#     tools/run-lab.sh --rig .../Rogue.glb --clip Walking_A --instances 3
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Blix.Tools.View/Blix.Tools.View.csproj"
APPHOST="$REPO_ROOT/src/Blix.Tools.View/bin/Debug/net8.0/Blix.Tools.View"

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
