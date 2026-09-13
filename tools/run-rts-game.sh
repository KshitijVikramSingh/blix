#!/usr/bin/env bash
# macOS Vulkan launcher for RTSGame. It publishes and execs the apphost directly:
# dyld snapshots DYLD_* at exec, and going through the dotnet muxer can drop the
# Vulkan loader path before GLFW starts.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/RTSGame/RTSGame.csproj"
RID="osx-arm64"

# <b>The config is a flag, because every judgement from the chair had been made on the slow build.</b>
# §83's rule is "judge feel in Release and label every figure with its config", and this launcher — the
# only way anybody actually plays the thing — published Debug and said nothing about it. Measured on the
# placement click in §118: Debug 520 ms against Release 291, and the mesh term alone 322 against 134. A
# stall judged here was being judged at roughly twice its shipped cost.
#
# Debug remains the default: it is what the assertions and the self-tests run under, and switching the
# default silently would make every previous figure in the plan incomparable to the next one. --release
# is one word, and the banner below says which one you got either way.
CONFIG="Debug"
for arg in "$@"; do
    case "$arg" in
        --release) CONFIG="Release" ;;
        --debug) CONFIG="Debug" ;;
    esac
done
PUBLISH_DIR="$REPO_ROOT/src/RTSGame/bin/Publish-$CONFIG"

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
echo "Building $CONFIG (so the shader copy below is not stale) ..."
dotnet build "$PROJECT" -c "$CONFIG" --nologo -v:q || exit 1

echo "Publishing self-contained ($RID, $CONFIG) ..."
dotnet publish "$PROJECT" -c "$CONFIG" -r "$RID" --self-contained true -o "$PUBLISH_DIR" --nologo -v:q

BUILD_SHADERS="$REPO_ROOT/src/RTSGame/bin/$CONFIG/net8.0/$RID/Shaders"
[ -d "$BUILD_SHADERS" ] || BUILD_SHADERS="$REPO_ROOT/src/RTSGame/bin/$CONFIG/net8.0/Shaders"
if [ -d "$BUILD_SHADERS" ]; then
    mkdir -p "$PUBLISH_DIR/Shaders"
    cp -p "$BUILD_SHADERS"/*.spv "$PUBLISH_DIR/Shaders/" 2>/dev/null || true
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

# Said out loud, every run. A figure quoted from the chair without its config is the thing §83 spent a
# whole section learning not to do.
echo "Running $CONFIG build — quote every figure from this run with its config."
exec "$PUBLISH_DIR/RTSGame" "$@"
