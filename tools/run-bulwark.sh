#!/usr/bin/env bash
# Launcher for Blix.Demos.Bulwark on macOS (tower-defense game #2 — see
# plan-bulwark.md). Same exec-the-apphost rationale as run-tank.sh: dyld snapshots
# DYLD_* at exec, so going through the dotnet muxer can drop the Vulkan loader path.
# Pass --frames N for a headless validation run (run under BLIX_VK_VALIDATE=1 and
# grep stderr for [vk-ERR]/[vk-WARN]).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Demos/Blix.Demos.Bulwark/Blix.Demos.Bulwark.csproj"
PUBLISH_DIR="$REPO_ROOT/src/Demos/Blix.Demos.Bulwark/bin/Publish"
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

BUILD_SHADERS="$REPO_ROOT/src/Demos/Blix.Demos.Bulwark/bin/Debug/net8.0/$RID/Shaders"
[ -d "$BUILD_SHADERS" ] || BUILD_SHADERS="$REPO_ROOT/src/Demos/Blix.Demos.Bulwark/bin/Debug/net8.0/Shaders"
if [ -d "$BUILD_SHADERS" ]; then
    mkdir -p "$PUBLISH_DIR/Shaders"
    cp -p "$BUILD_SHADERS"/*.spv "$PUBLISH_DIR/Shaders/" 2>/dev/null || true
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

exec "$PUBLISH_DIR/Blix.Demos.Bulwark" "$@"
