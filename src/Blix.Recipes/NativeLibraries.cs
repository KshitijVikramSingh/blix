using System.Runtime.InteropServices;

namespace Blix.Recipes;

/// <summary>
/// One <see cref="NativeLibrary.SetDllImportResolver"/> for this assembly, resolving every native
/// the recipes call.
/// </summary>
/// <remarks>
/// <para>
/// .NET permits one import resolver per assembly, so BC7 and meshoptimizer share this registration.
/// </para>
/// <para>
/// Resolution is by absolute path from <see cref="AppContext.BaseDirectory"/> because default
/// native probing does not reliably find an app-local <c>.dylib</c> by bare name on macOS.
/// </para>
/// </remarks>
internal static class NativeLibraries
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["meshoptimizer"] = "libmeshoptimizer.dylib",
        ["blix_bc7"] = "libblix_bc7.dylib",
    };

    static NativeLibraries()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, _, _) =>
            Files.TryGetValue(name, out var file)
                ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, file))
                : IntPtr.Zero);
    }

    /// <summary>Forces the resolver to be registered. Idempotent; safe from any static ctor.</summary>
    public static void Ensure()
    {
        // Touching the type runs the static constructor exactly once, which is the guarantee the
        // two callers need and the one two separate registrations could not give.
    }
}
