using System.Runtime.InteropServices;

namespace Blix.Recipes;

/// <summary>
/// One <see cref="NativeLibrary.SetDllImportResolver"/> for this assembly, resolving every native
/// the recipes call — and the one place that knows what such a file is called here.
/// </summary>
/// <remarks>
/// <para>
/// .NET permits one import resolver per assembly, so BC7 and meshoptimizer share this registration.
/// </para>
/// <para>
/// Resolution is by absolute path from <see cref="AppContext.BaseDirectory"/> because default
/// native probing does not reliably find an app-local library by bare name on macOS.
/// </para>
/// <para>
/// The filename is built rather than written down. It used to be written down, as
/// <c>libmeshoptimizer.dylib</c> in this table and <c>libblix_bc7.dylib</c> again in
/// <see cref="Bc7Native"/> — two spellings of one fact, both true only on macOS, and the second
/// one not even reachable from here. Nothing punished that while one platform was the only
/// platform.
/// </para>
/// </remarks>
internal static class NativeLibraries
{
    /// <summary>The logical names, as the <c>DllImport</c> attributes spell them.</summary>
    private static readonly string[] Names = { "meshoptimizer", "blix_bc7", "blix_mikk" };

    static NativeLibraries()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, _, _) =>
            Names.Contains(name, StringComparer.Ordinal)
                ? NativeLibrary.Load(PathFor(name))
                : IntPtr.Zero);
    }

    /// <summary>What a shared library of this name is called on this platform, in full.</summary>
    /// <remarks>
    /// The three platforms disagree about both halves: Unix prefixes <c>lib</c> and Windows does
    /// not, and all three take a different extension. Answering in one place is what lets the two
    /// callers agree, and what lets a probe look for the file the loader will actually ask for.
    /// </remarks>
    public static string PathFor(string name) =>
        Path.Combine(AppContext.BaseDirectory, FileName(name));

    /// <summary>The bare filename, without a directory.</summary>
    public static string FileName(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{name}.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"lib{name}.dylib"
        : $"lib{name}.so";

    /// <summary>Is the library actually here to be loaded?</summary>
    /// <remarks>
    /// Cheap, and deliberately a file test rather than a load: a caller asks this to choose a
    /// path, and a failed load is not free. The callers that need certainty load and catch.
    /// </remarks>
    public static bool Present(string name) => File.Exists(PathFor(name));

    /// <summary>Forces the resolver to be registered. Idempotent; safe from any static ctor.</summary>
    public static void Ensure()
    {
        // Touching the type runs the static constructor exactly once, which is the guarantee the
        // two callers need and the one two separate registrations could not give.
    }
}
