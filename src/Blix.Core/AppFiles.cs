namespace Blix.Core;

/// <summary>
/// Where an application's own files are, which is not always beside its executable.
/// </summary>
/// <remarks>
/// <para>
/// A build puts <c>Assets/</c> next to the binary and that is the end of it. A macOS .app cannot:
/// <c>Contents/MacOS</c> is meant to hold Mach-O and nothing else, and codesign enforces it —
/// a cooked <c>foo.textures/</c> sidecar there is read as a malformed nested bundle and refuses
/// the whole signature (measured). Data goes in <c>Contents/Resources</c>, so on a published
/// bundle the assets are one level across rather than right here.
/// </para>
/// <para>
/// This exists so the answer is arrived at once. Eleven call sites had each written
/// <c>Path.Combine(AppContext.BaseDirectory, "Assets", …)</c> by hand, which is the failure this
/// project keeps meeting: a private copy of an engine step, missing the case the engine knows
/// about, and wrong in a way that still runs. Bulwark under a signable layout did not crash —
/// it drew primitives instead of its models and carried on.
/// </para>
/// </remarks>
public static class AppFiles
{
    /// <summary>The <c>Assets</c> directory this application should read from.</summary>
    /// <remarks>
    /// Beside the executable when that is where they are, and the bundle's Resources when it is
    /// not. Falls back to the beside-the-executable path when neither exists, so a missing-asset
    /// message names the place a developer expects rather than the place we last looked.
    /// </remarks>
    public static string Assets { get; } = ResolveAssets();

    /// <summary>The same as <see cref="Assets"/>, with <paramref name="parts"/> appended.</summary>
    public static string Asset(params string[] parts) =>
        Path.Combine(new[] { Assets }.Concat(parts).ToArray());

    /// <summary>Where this application's compiled shaders are, its own and every library's it references.</summary>
    /// <remarks>
    /// <para>
    /// Beside the binary, in a build and in a published bundle alike: publishing moves <c>Assets/</c>
    /// into <c>Contents/Resources</c> for codesign, and leaves <c>Shaders/</c> where it is, because
    /// nothing in it looks like a nested bundle. A library's shaders land here too, carried by the
    /// shared shader staging, which is why a library can find its own without being told.
    /// </para>
    /// <para>
    /// Twenty-two places wrote <c>Path.Combine(AppContext.BaseDirectory, "Shaders")</c> by hand, four of
    /// them inside the engine's own libraries, and one library took the path as an argument so every
    /// caller had to know where its shaders were. This is the one statement of it.
    /// </para>
    /// </remarks>
    public static string Shaders { get; } = Path.Combine(AppContext.BaseDirectory, "Shaders");

    /// <summary>A file under <see cref="Shaders"/>, such as <c>Shader("lit.frag.spv")</c>.</summary>
    public static string Shader(params string[] parts) =>
        Path.Combine(new[] { Shaders }.Concat(parts).ToArray());

    private static string ResolveAssets()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Directory.Exists(beside)) return beside;

        var bundled = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Assets"));
        return Directory.Exists(bundled) ? bundled : beside;
    }
}
