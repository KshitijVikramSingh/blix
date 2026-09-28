#!/usr/bin/env bash
# Launcher for Blix.Demos.VulkanInstanced on macOS (the instancing proof gate).
#
# Same exec-the-apphost rationale as run-pong.sh: dyld snapshots DYLD_* at exec,
# so going through the dotnet muxer can drop the Vulkan loader path. Pass
# --frames N for a bounded smoke run; use BLIX_VK_VALIDATE=1 to inspect Vulkan
# validation output.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Demos/Blix.Demos.VulkanInstanced/Blix.Demos.VulkanInstanced.csproj"
PUBLISH_DIR="$REPO_ROOT/src/Demos/Blix.Demos.VulkanInstanced/bin/Publish"
RID="osx-arm64"

prefix=$(brew --prefix 2>/dev/null || echo "/opt/homebrew")
if [ ! -f "$prefix/lib/libvulkan.dylib" ]; then
    echo "libvulkan.dylib not found under $prefix/lib — install with:" >&2
    echo "  brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc" >&2
    exit 1
fi

if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$prefix/opt/dotnet@8/libexec" ]; then
    export DOTNET_ROOT="$prefix/opt/dotnet@8/libexec"
fi
if [ -d "$prefix/opt/dotnet@8/bin" ]; then
    export PATH="$prefix/opt/dotnet@8/bin:$PATH"
fi
if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found — install with: brew install dotnet@8" >&2
    exit 1
fi

echo "Publishing self-contained ($RID) ..."
dotnet publish "$PROJECT" -c Debug -r "$RID" --self-contained true -o "$PUBLISH_DIR" --nologo -v:q

# InstancedBatch's instanced.*.spv are content shipped from Blix.Render. Mirror
# any *.spv into the publish dir in case content propagation lags.
# Shaders and cooked assets arrive through `dotnet publish` itself: both compile to a staged
# location and are declared as content, so publish carries them like any other item. This script
# used to mirror them by hand afterwards, because app shaders were written straight into the
# output directory and were never items at all. See plan.md stage A.

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

exec "$PUBLISH_DIR/Blix.Demos.VulkanInstanced" "$@"
