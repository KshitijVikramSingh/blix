#!/usr/bin/env bash
# Launcher for Blix.Demos.VulkanSponza on macOS.
#
# macOS-specific quirk this script works around:
#   dyld snapshots DYLD_FALLBACK_LIBRARY_PATH at process exec. On macOS
#   Sequoia (Darwin 24+), the kernel additionally strips DYLD_* env vars
#   for any binary signed with CS_LINKER_SIGNED — and Homebrew's dotnet
#   muxer has that flag (`codesign -dvv` reports `flags=0x20002`). The
#   net effect: `dotnet run` from a shell where DYLD_FALLBACK_LIBRARY_PATH
#   is set still ends up with libvulkan unreachable, and Silk.NET +
#   GLFW abort with "doesn't support Vulkan on this computer."
#
# Workaround: `dotnet publish --self-contained` emits a fresh AppHost
# binary that's signed plain adhoc (flags=0x2) without CS_LINKER_SIGNED.
# That binary inherits DYLD_FALLBACK_LIBRARY_PATH normally, libvulkan +
# MoltenVK load, and the demo runs. We re-publish on every launch but
# cache by timestamp so the second run is fast.
#
# Linux + Windows: this whole dance is unnecessary; just
#   dotnet run --project src/Blix.Demos.VulkanSponza/Blix.Demos.VulkanSponza.csproj
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Blix.Demos.VulkanSponza/Blix.Demos.VulkanSponza.csproj"
PUBLISH_DIR="$REPO_ROOT/src/Blix.Demos.VulkanSponza/bin/Publish"
RID="osx-arm64"

# The Sponza pack set is large and typically kept outside the repo (e.g. on an
# external SSD shared across machines). Point BLIX_SPONZA_ASSETS at it — export
# it in your shell (~/.zshrc) or inline before this command. When set, the
# publish build skips copying it into bin/ and the process reads straight from
# that path; when unset, the build falls back to the in-repo Assets/ copy.
# Populate either location with tools/setup-sponza-modern.sh.
if [ -n "${BLIX_SPONZA_ASSETS:-}" ]; then
    if [ ! -d "$BLIX_SPONZA_ASSETS/main_sponza" ]; then
        echo "BLIX_SPONZA_ASSETS=$BLIX_SPONZA_ASSETS but $BLIX_SPONZA_ASSETS/main_sponza is missing." >&2
        echo "  Is the drive mounted? Populate with tools/setup-sponza-modern.sh." >&2
        exit 1
    fi
    export BLIX_SPONZA_ASSETS
fi

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

# Publish self-contained for osx-arm64. The output goes alongside the
# project's regular bin/ so subsequent builds know how to incrementally
# update; the apphost lands at $PUBLISH_DIR/Blix.Demos.VulkanSponza.
echo "Publishing self-contained ($RID) ..."
dotnet publish "$PROJECT" -c Debug -r "$RID" --self-contained true -o "$PUBLISH_DIR" --nologo -v:q

# CompileSpirV (the MSBuild target that runs glslc) writes into the
# project's bin/Debug/net8.0/$RID/Shaders/ during the publish build, but
# `dotnet publish` doesn't see those .spv files as content and skips them.
# Mirror them over manually — they're a few KB each.
SHADER_BUILD_DIR="$REPO_ROOT/src/Blix.Demos.VulkanSponza/bin/Debug/net8.0/$RID/Shaders"
if [ -d "$SHADER_BUILD_DIR" ]; then
    mkdir -p "$PUBLISH_DIR/Shaders"
    cp -p "$SHADER_BUILD_DIR"/*.spv "$PUBLISH_DIR/Shaders/" 2>/dev/null || true
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
# VK_ICD_FILENAMES + VK_LAYER_PATH are reread per-call by the Vulkan loader
# (unlike DYLD_*), so the engine's MoltenVkBootstrap can also set them
# from inside the process. Setting them here too keeps shell-level
# debugging predictable.
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

exec "$PUBLISH_DIR/Blix.Demos.VulkanSponza" "$@"
