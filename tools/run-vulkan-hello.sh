#!/usr/bin/env bash
# Launcher for Blix.Demos.VulkanHello on macOS.
#
# Required because DYLD_FALLBACK_LIBRARY_PATH must be set in the parent
# shell *before* the .NET process execs — modern dyld (macOS 11+) snapshots
# DYLD_* env vars at exec and ignores any runtime mutation. Without this,
# GLFW's dlopen("libvulkan.1.dylib") can't find the Homebrew loader and
# Silk.NET aborts with "doesn't support Vulkan on this computer."
#
# On Linux + Windows just `dotnet run` the demo directly; native Vulkan
# drivers are on the default loader search path.
set -euo pipefail

prefix=$(brew --prefix 2>/dev/null || echo "/opt/homebrew")
if [ ! -f "$prefix/lib/libvulkan.dylib" ]; then
    echo "libvulkan.dylib not found under $prefix/lib — install with:" >&2
    echo "  brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc" >&2
    exit 1
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
# VK_ICD_FILENAMES + VK_LAYER_PATH are also set by MoltenVkBootstrap inside
# the process (the Vulkan loader rereads them per call, unlike DYLD_*),
# but setting them here as well is harmless and makes shell-level debugging
# with vulkaninfo / glslc from this script's env predictable.
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Demos/Blix.Demos.VulkanHello/Blix.Demos.VulkanHello.csproj"
APPHOST="$REPO_ROOT/src/Demos/Blix.Demos.VulkanHello/bin/Debug/net8.0/Blix.Demos.VulkanHello"

# Exec the apphost, never `dotnet run` — the same rationale the other launchers
# carry, and the reason this one was broken while they worked.
#
# Homebrew's $prefix/bin/dotnet is a "#!/bin/bash" WRAPPER SCRIPT, and /bin/bash
# is SIP-protected. dyld strips DYLD_* from the environment of any protected
# binary it execs, so the exports above are laundered away by the shim before
# the app ever starts. Everything downstream then fails to dlopen libvulkan and
# Silk.NET reports "doesn't support Vulkan on this computer", which is about as
# misleading as an error message gets: Vulkan is installed and fine.
#
# DOTNET_ROOT is what the framework-dependent apphost needs in place of the
# muxer; with it set there is no need for a self-contained publish.
if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$prefix/opt/dotnet@8/libexec" ]; then
    export DOTNET_ROOT="$prefix/opt/dotnet@8/libexec"
fi

dotnet build "$PROJECT" -c Debug --nologo -v:q
exec "$APPHOST" "$@"
