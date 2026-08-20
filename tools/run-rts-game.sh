#!/usr/bin/env bash
# macOS Vulkan launcher for RTSGame. It publishes and execs the apphost directly:
# dyld snapshots DYLD_* at exec, and going through the dotnet muxer can drop the
# Vulkan loader path before GLFW starts.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/RTSGame/RTSGame.csproj"
PUBLISH_DIR="$REPO_ROOT/src/RTSGame/bin/Publish"
RID="osx-arm64"

prefix=$(brew --prefix 2>/dev/null || echo "/opt/homebrew")
if [ ! -f "$prefix/lib/libvulkan.dylib" ]; then
    echo "libvulkan.dylib not found under $prefix/lib — install the Blix Vulkan prerequisites first." >&2
    exit 1
fi

if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$prefix/opt/dotnet@8/libexec" ]; then
    export DOTNET_ROOT="$prefix/opt/dotnet@8/libexec"
fi
if [ -d "$prefix/opt/dotnet@8/bin" ]; then
    export PATH="$prefix/opt/dotnet@8/bin:$PATH"
fi

# <b>Build first, because the shader copy below trusts the build directory.</b> The publish compiles its
# own SPIR-V and then those files are overwritten from bin/Debug — which is correct for the reason in the
# comment below, and silently wrong if bin/Debug is stale. Editing a shader and running only this script
# then launches the previous shader, and the way that surfaces is a push-constant payload-length mismatch at
# draw time: a number from a file you did not think you were using.
echo "Building (so the shader copy below is not stale) ..."
dotnet build "$PROJECT" -c Debug --nologo -v:q || exit 1

echo "Publishing self-contained ($RID) ..."
dotnet publish "$PROJECT" -c Debug -r "$RID" --self-contained true -o "$PUBLISH_DIR" --nologo -v:q

BUILD_SHADERS="$REPO_ROOT/src/RTSGame/bin/Debug/net8.0/$RID/Shaders"
[ -d "$BUILD_SHADERS" ] || BUILD_SHADERS="$REPO_ROOT/src/RTSGame/bin/Debug/net8.0/Shaders"
if [ -d "$BUILD_SHADERS" ]; then
    mkdir -p "$PUBLISH_DIR/Shaders"
    cp -p "$BUILD_SHADERS"/*.spv "$PUBLISH_DIR/Shaders/" 2>/dev/null || true
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

exec "$PUBLISH_DIR/RTSGame" "$@"
