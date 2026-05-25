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

cd "$(dirname "$0")/.."
exec dotnet run --project src/Blix.Demos.VulkanHello/Blix.Demos.VulkanHello.csproj "$@"
