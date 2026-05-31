#!/usr/bin/env bash
# Launcher for Blix.Demos.Pong on macOS.
#
# Publishes self-contained and exec's the apphost directly rather than going
# through `dotnet run`. The dotnet muxer re-execs the app, and modern dyld
# (macOS 11+) snapshots DYLD_* at exec — so DYLD_FALLBACK_LIBRARY_PATH set in
# this shell can be lost across the muxer hop, leaving GLFW unable to dlopen
# libvulkan ("doesn't support Vulkan on this computer"). Exec'ing the apphost
# means our DYLD_* is the environment the Vulkan process actually starts with.
#
# On Linux + Windows just `dotnet run` the demo directly; native Vulkan
# drivers are on the default loader search path.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Blix.Demos.Pong/Blix.Demos.Pong.csproj"
PUBLISH_DIR="$REPO_ROOT/src/Blix.Demos.Pong/bin/Publish"
RID="osx-arm64"

prefix=$(brew --prefix 2>/dev/null || echo "/opt/homebrew")
if [ ! -f "$prefix/lib/libvulkan.dylib" ]; then
    echo "libvulkan.dylib not found under $prefix/lib — install with:" >&2
    echo "  brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc" >&2
    exit 1
fi

# dotnet@8 is keg-only on Homebrew; expose the binary + DOTNET_ROOT so the
# muxer finds the bundled SDK.
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

# The SpriteBatch shaders are content shipped from Blix.Render and the imgui
# shaders from Blix.Runtime.Silk — both land in the regular build output.
# Mirror any *.spv over to the publish dir in case content propagation lags.
BUILD_SHADERS="$REPO_ROOT/src/Blix.Demos.Pong/bin/Debug/net8.0/$RID/Shaders"
[ -d "$BUILD_SHADERS" ] || BUILD_SHADERS="$REPO_ROOT/src/Blix.Demos.Pong/bin/Debug/net8.0/Shaders"
if [ -d "$BUILD_SHADERS" ]; then
    mkdir -p "$PUBLISH_DIR/Shaders"
    cp -p "$BUILD_SHADERS"/*.spv "$PUBLISH_DIR/Shaders/" 2>/dev/null || true
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

exec "$PUBLISH_DIR/Blix.Demos.Pong" "$@"
