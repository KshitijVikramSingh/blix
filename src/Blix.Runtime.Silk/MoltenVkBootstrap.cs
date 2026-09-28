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
//    MoltenVK_icd.json under $prefix/etc/vulkan/icd.d/, which the loader
//    does search (it is the sysconfdir it was built with), so this is
//    belt-and-braces on a Homebrew box. It is load-bearing for a bundle:
//    setting VK_ICD_FILENAMES to the bundled manifest is what stops the
//    loader from also finding an installed MoltenVK and running two copies
//    of the driver in one process (measured: objc reports duplicate
//    MVKBlockObserver classes, and the installed copy wins).
//
//    The variable has to be set with libc setenv, not
//    Environment.SetEnvironmentVariable: on Unix the latter updates .NET's
//    own copy of the environment and never touches the `environ` that the
//    loader's getenv reads. Unlike DYLD_*, though, it does not need to be
//    set before exec -- the loader reads it at vkCreateInstance time.
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

        // A bundled runtime wins over anything installed: it is the one a shipped application
        // can rely on, and a developer machine that happens to have Homebrew should still
        // exercise what was shipped rather than what is lying around.
        if (BundledRuntime() is { } bundled)
        {
            // dyld froze its view of DYLD_* at exec and GLFW's dlopen consults that view, so the
            // only way to point it at the bundle is to be exec'd with the variable already set.
            // Nothing outside can be relied on to do that: Finder sets no environment, and an
            // LSEnvironment entry is a static string that bakes an absolute path and dies the
            // first time the .app is dragged to /Applications (measured).
            ReExecWithBundledRuntime(bundled);

            TryPreload(Path.Combine(bundled, "libvulkan.1.dylib"));

            // The loader finds this manifest on its own -- Contents/Resources/vulkan/icd.d is one
            // of the places it looks inside a .app. Naming it explicitly is still what makes the
            // bundled driver the ONLY one: on a machine that also has MoltenVK installed the
            // loader finds both manifests, loads both dylibs, and uses the installed one.
            var icd = Path.GetFullPath(Path.Combine(
                bundled, "..", "Resources", "vulkan", "icd.d", "MoltenVK_icd.json"));
            if (File.Exists(icd) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_ICD_FILENAMES")))
            {
                SetEnvironment("VK_ICD_FILENAMES", icd);
            }

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

    /// <summary>The sentinel that stops the re-exec below from being a fork bomb.</summary>
    private const string ReExecMarker = "BLIX_RUNTIME_REEXEC";

    /// <summary>
    /// Replace this process with itself, carrying the bundle's library path in the environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because the variable has to exist before the process starts, and only the process knows
    /// where it lives.</b> The path is computed from <see cref="Environment.ProcessPath"/> at
    /// runtime, so a bundle keeps working when it is moved — which is the one thing a baked
    /// LSEnvironment path cannot do.
    /// </para>
    /// <para>
    /// Uses setenv + execv rather than .NET's own environment API, because on Unix
    /// <see cref="Environment.SetEnvironmentVariable"/> updates the managed copy and not the
    /// native <c>environ</c> that <c>execv</c> hands to the new image.
    /// </para>
    /// <para>
    /// Every failure here is non-fatal on purpose: if the exec does not happen the application
    /// carries on and fails the way it would have anyway, which is a worse picture but not a
    /// worse process.
    /// </para>
    /// </remarks>
    private static void ReExecWithBundledRuntime(string frameworks)
    {
        if (Environment.GetEnvironmentVariable(ReExecMarker) is not null) return;

        var existing = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH") ?? string.Empty;
        if (existing.Split(':').Contains(frameworks)) return;

        if (Environment.ProcessPath is not { } exe || !File.Exists(exe)) return;

        var argv = IntPtr.Zero;
        try
        {
            SetEnv("DYLD_FALLBACK_LIBRARY_PATH",
                existing.Length == 0 ? frameworks : frameworks + ":" + existing, 1);
            SetEnv(ReExecMarker, "1", 1);

            // argv must be NULL-terminated, which is not something the default marshaller does.
            var args = Environment.GetCommandLineArgs();
            var slots = new IntPtr[args.Length + 1];
            slots[0] = Marshal.StringToHGlobalAnsi(exe);
            for (var i = 1; i < args.Length; i++) slots[i] = Marshal.StringToHGlobalAnsi(args[i]);
            slots[^1] = IntPtr.Zero;

            argv = Marshal.AllocHGlobal(IntPtr.Size * slots.Length);
            Marshal.Copy(slots, 0, argv, slots.Length);

            Execv(exe, argv);

            // execv only returns on failure.
            Console.Error.WriteLine(
                "[MoltenVkBootstrap] could not re-exec to pick up the bundled runtime; " +
                "continuing, and Vulkan will probably not initialise.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MoltenVkBootstrap] re-exec failed: {ex.Message}");
        }
        finally
        {
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
        }
    }

    /// <summary>
    /// Set a variable so that native <c>getenv</c> sees it, which is the only kind the Vulkan
    /// loader reads.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.SetEnvironmentVariable"/> alone is not enough on Unix: it writes to
    /// a dictionary .NET keeps for itself and leaves the process's real <c>environ</c> untouched,
    /// so a native library that calls <c>getenv</c> never sees the value (measured -- the loader
    /// ignored an ICD path set this way, and only appeared to work because its built-in search
    /// covers Homebrew's prefix anyway). We write both, so managed readers stay consistent too.
    /// </remarks>
    private static void SetEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            SetEnv(name, value, 1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MoltenVkBootstrap] setenv({name}) failed: {ex.Message}");
        }
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int Execv(string path, IntPtr argv);

    /// <summary>
    /// The <c>Contents/Frameworks</c> of the .app this process is running from, if it is one.
    /// </summary>
    /// <remarks>
    /// Two filenames have to be there, not one. GLFW dlopens <c>libvulkan.1.dylib</c> and
    /// Silk.NET's own resolver asks for <c>libvulkan.dylib</c>; they are separate name lists and
    /// a bundle carrying only one of them gets past whichever asks first and dies on the other.
    /// </remarks>
    private static string? BundledRuntime()
    {
        var frameworks = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "Frameworks"));
        return File.Exists(Path.Combine(frameworks, "libvulkan.1.dylib"))
            && File.Exists(Path.Combine(frameworks, "libvulkan.dylib"))
            ? frameworks
            : null;
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

    private static void TryPreloadLibVulkan(string prefix) =>
        TryPreload(Path.Combine(prefix, "lib", "libvulkan.dylib"));

    private static void TryPreload(string path)
    {
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
            SetEnvironment("VK_ICD_FILENAMES", icd);
        }
    }

    private static void TrySetLayerPath(string prefix)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LAYER_PATH"))) return;
        var layerDir = Path.Combine(prefix, "share", "vulkan", "explicit_layer.d");
        if (Directory.Exists(layerDir))
        {
            SetEnvironment("VK_LAYER_PATH", layerDir);
        }
    }
}
