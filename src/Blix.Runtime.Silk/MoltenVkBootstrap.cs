using System.Runtime.InteropServices;

namespace Blix.Runtime.Silk;

// macOS setup that lets the Vulkan loader + GLFW find MoltenVK and the
// Khronos validation layers without per-shell env var ceremony. Pure
// no-op on Linux + Windows.
//
// Three things have to be true before Silk.NET creates the window:
//
// 1. libvulkan.dylib must be loadable via dyld's bare-name search. The
//    Homebrew vulkan-loader installs to /opt/homebrew/lib (Apple Silicon)
//    or /usr/local/lib (Intel), neither of which is on dyld's default
//    search path. GLFW calls dlopen("libvulkan.1.dylib", ...) and fails
//    if dyld can't resolve that. We work around it by pre-loading the
//    absolute path via NativeLibrary.Load — once it's mapped into the
//    process image, dyld returns the existing handle for subsequent
//    bare-name dlopens.
//
// 2. The Vulkan loader has to find an ICD manifest. Homebrew installs
//    MoltenVK_icd.json under $prefix/etc/vulkan/icd.d/, not $prefix/share/
//    vulkan/icd.d/ where the loader's standard search looks. We set
//    VK_ICD_FILENAMES at process start so the loader picks it up at
//    vkCreateInstance time.
//
// 3. Validation layers (when present) live under $prefix/share/vulkan/
//    explicit_layer.d/, which IS on the loader's standard search list,
//    but we set VK_LAYER_PATH explicitly to keep behaviour reproducible
//    regardless of how a future loader version evolves its defaults.
//
// LunarG SDK users can skip this whole dance — install the .pkg and
// the loader/ICD/layers all land in /usr/local with the expected layout.
// EnsureLoaded() detects an existing libvulkan + ICD env var and short-
// circuits, so it doesn't interfere with a user's LunarG setup.
public static class MoltenVkBootstrap
{
    private static bool ensured;

    public static void EnsureLoaded()
    {
        if (ensured) return;
        ensured = true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Linux + Windows: native Vulkan drivers, standard loader search
            // paths. Nothing to do.
            return;
        }

        var prefix = DetectHomebrewPrefix();
        if (prefix is null)
        {
            // No Homebrew install detected. User may have LunarG SDK already
            // set up — let Silk + the loader try whatever the system gives
            // them and surface any failure naturally.
            return;
        }

        WarnIfDyldNotPreset(prefix);
        TryPreloadLibVulkan(prefix);
        TrySetIcdFilenames(prefix);
        TrySetLayerPath(prefix);
    }

    private static void WarnIfDyldNotPreset(string prefix)
    {
        // Modern dyld (macOS 11+) snapshots DYLD_* env vars at process exec
        // and does not re-read them. Setting DYLD_FALLBACK_LIBRARY_PATH from
        // inside the process is useless — GLFW's dlopen("libvulkan.1.dylib")
        // happens later but still consults dyld's frozen view. The variable
        // has to be in the environment that exec'd our process.
        //
        // tools/run-vulkan-hello.sh handles this. If a user runs `dotnet run`
        // directly without it on macOS, GLFW fails with "doesn't support
        // Vulkan on this computer" — warn loudly with the fix.
        var libDir = Path.Combine(prefix, "lib");
        var fallback = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH") ?? string.Empty;
        if (fallback.Split(':').Contains(libDir)) return;
        Console.Error.WriteLine($"[MoltenVkBootstrap] DYLD_FALLBACK_LIBRARY_PATH does not include {libDir}.");
        Console.Error.WriteLine("[MoltenVkBootstrap] GLFW will likely fail to find libvulkan. Run via tools/run-vulkan-hello.sh,");
        Console.Error.WriteLine($"[MoltenVkBootstrap] or set DYLD_FALLBACK_LIBRARY_PATH={libDir} in your shell before dotnet run.");
    }

    private static string? DetectHomebrewPrefix()
    {
        // Apple Silicon Homebrew first since that's the modern default.
        foreach (var candidate in new[] { "/opt/homebrew", "/usr/local" })
        {
            if (File.Exists(Path.Combine(candidate, "lib", "libvulkan.dylib")))
            {
                return candidate;
            }
        }
        return null;
    }

    private static void TryPreloadLibVulkan(string prefix)
    {
        var path = Path.Combine(prefix, "lib", "libvulkan.dylib");
        if (!File.Exists(path)) return;
        try
        {
            NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MoltenVkBootstrap] preload of {path} failed: {ex.Message}");
        }
    }

    private static void TrySetIcdFilenames(string prefix)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_ICD_FILENAMES"))) return;
        var icd = Path.Combine(prefix, "etc", "vulkan", "icd.d", "MoltenVK_icd.json");
        if (File.Exists(icd))
        {
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", icd);
        }
    }

    private static void TrySetLayerPath(string prefix)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LAYER_PATH"))) return;
        var layerDir = Path.Combine(prefix, "share", "vulkan", "explicit_layer.d");
        if (Directory.Exists(layerDir))
        {
            Environment.SetEnvironmentVariable("VK_LAYER_PATH", layerDir);
        }
    }
}
