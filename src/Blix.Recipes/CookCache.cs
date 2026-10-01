using System.Security.Cryptography;
using System.Text;

namespace Blix.Recipes;

/// <summary>
/// Cook-on-open for tools: any model path, resolved to a cooked one.
/// </summary>
/// <remarks>
/// <para>
/// The engine reads cooked models only, so a tool that opens an arbitrary <c>.glb</c> cooks it
/// first. A <c>.blixmesh</c> is itself; a source whose cooked sibling is current uses the sibling;
/// anything else is cooked, with its images, into a per-user cache and loaded from there. The cooked
/// file is what the engine will draw, so an inspector shows exactly that.
/// </para>
/// <para>
/// Freshness is the cook's own rule (<see cref="MeshRecipe.IsShippedCurrent"/>): the recipe, the
/// format and the source's stamp. The cache is one directory per source path, so re-cooking an edited
/// file replaces its entry rather than accumulating copies.
/// </para>
/// </remarks>
public static class CookCache
{
    /// <summary>Where cooked copies go: <c>BLIX_COOK_CACHE</c>, else the platform's per-user cache.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("BLIX_COOK_CACHE") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(PlatformCache(), "blix", "cooked");

    /// <summary>A cooked model to load for <paramref name="modelPath"/>, cooking it into the cache if it must.</summary>
    public static string Resolve(string modelPath, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        var full = Path.GetFullPath(modelPath);
        if (full.EndsWith(".blixmesh", StringComparison.OrdinalIgnoreCase)) return full;
        // The one refusal type even for the plainest no: a tool catches AssetImportException and lets anything else
        // crash, so a missing file is a refusal naming the path, not a FileNotFoundException escaping past it.
        if (!File.Exists(full)) throw new Blix.Cooked.AssetImportException(modelPath, null, "no file there.");

        var sibling = Path.ChangeExtension(full, ".blixmesh");
        if (File.Exists(sibling) && MeshRecipe.IsShippedCurrent(full, sibling)) return sibling;

        var entry = Path.Combine(Root, $"{Key(full)}-{Path.GetFileNameWithoutExtension(full)}");
        var cooked = Path.Combine(entry, Path.GetFileNameWithoutExtension(full) + ".blixmesh");
        if (File.Exists(cooked) && MeshRecipe.IsShippedCurrent(full, cooked)) return cooked;

        // One cook per entry at a time: a second tool opening the same file waits for the first
        // rather than writing the same tree beside it.
        Directory.CreateDirectory(entry);
        using var hold = Hold(Path.Combine(entry, ".cooking"));
        if (File.Exists(cooked) && MeshRecipe.IsShippedCurrent(full, cooked)) return cooked;

        log?.Invoke($"cooking {Path.GetFileName(full)} into {entry}");
        // The engine's one refusal type, naming the file, so a tool reports "blix cannot read this"
        // the same way whether the parser or the cook said no.
        return Blix.Cooked.AssetImportException.Refusing(
            modelPath, () => AssetCook.Cook(full, entry).MeshPath);
    }

    private static string Key(string fullPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..16].ToLowerInvariant();

    private static FileStream Hold(string lockPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 600)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string PlatformCache()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    }
}
