using System.Xml.Linq;

namespace Blix.Cli;

/// <summary>
/// Whether the thing about to run was built from what is on disk now.
/// </summary>
/// <remarks>
/// <b>The resolver stays a resolver: this reports, it does not act.</b> `blix run` execs what
/// the index points at, and until this existed it did so in silence — so editing a demo and
/// running it gave you the PREVIOUS binary with no sign that anything was wrong. That is the
/// worst shape a failure can take, because it looks exactly like an edit that did nothing, and
/// the eleven launcher scripts this replaced all ran a build first, so the silence was new.
/// <para>
/// It is deliberately not a build system and does not try to be right the way MSBuild is right.
/// It answers one question a person can act on — "is this older than my code?" — and answers it
/// conservatively, because a check that cries wolf is one people learn to read past.
/// </para>
/// </remarks>
public static class Freshness
{
    /// <summary>
    /// What counts as a source: the things whose edits reach a build output.
    /// </summary>
    /// <remarks>
    /// Shaders are in here because a shader edit is exactly as invisible as a code edit and
    /// this is a graphics engine — <c>.vert</c> compiles to a <c>.spv</c> that is staged as
    /// content, so an old binary carries an old shader with nothing to say so.
    /// <para>
    /// Content and cooked assets are NOT in here, and that is the honest limit of this check:
    /// they are large, they live in trees whose enumeration would dominate the cost, and a
    /// stale texture is visible in a way a stale code path is not.
    /// </para>
    /// </remarks>
    public static readonly string[] SourceExtensions =
    {
        ".cs", ".csproj", ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese", ".glsl", ".glslh",
    };

    /// <summary>Directories whose contents are output or history, never source.</summary>
    private static readonly string[] SkippedDirectories = { "bin", "obj", ".git", ".vs", "node_modules" };

    /// <param name="Source">The newest source file found, which is the one to name.</param>
    /// <param name="SourceWritten">When it was last written.</param>
    /// <param name="Built">When the output last changed.</param>
    public sealed record Staleness(string Source, DateTime SourceWritten, DateTime Built);

    /// <summary>
    /// Report staleness, or null when the output is current, unknowable, or not there.
    /// </summary>
    /// <remarks>
    /// Null on every uncertainty on purpose. An index written before it recorded its .csproj, a
    /// project file that has since moved, an output directory with nothing in it — each is a
    /// case where this cannot tell, and a check that guesses when it cannot tell is worse than
    /// one that stays quiet. "Not built at all" is already reported by the caller, with a better
    /// message than this could give.
    /// </remarks>
    /// <param name="project">The .csproj the app was built from.</param>
    /// <param name="assembly">The built assembly, whose directory is the output.</param>
    public static Staleness? Check(string? project, string assembly)
    {
        if (project is not { Length: > 0 } || !File.Exists(project)) return null;

        var output = Path.GetDirectoryName(assembly);
        if (output is null || !Directory.Exists(output)) return null;

        // The whole output directory, not just this app's own dll.
        //
        // Measured, because the obvious choice is wrong in a way that would make this cry wolf
        // forever: change a method body in Blix.Core and MSBuild may not recompile a demo that
        // only calls it, leaving the demo's own dll untouched. But the new Blix.Core.dll MUST be
        // copied in beside it, or the demo could not run the new code at all. So if anything new
        // reached this app, SOMETHING here is newer -- which makes the maximum the right datum
        // and this app's own timestamp the wrong one.
        var built = NewestBuild(output);
        if (built is null) return null;

        var newest = NewestSource(Closure(project));
        if (newest is null) return null;

        // A second of slack for filesystem timestamp granularity, matching the index's own
        // staleness rule so the two cannot disagree about what "newer" means.
        return newest.Value.Written > built.Value.AddSeconds(1)
            ? new Staleness(newest.Value.Path, newest.Value.Written, built.Value)
            : null;
    }

    private static DateTime? NewestBuild(string output)
    {
        DateTime? newest = null;
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.TopDirectoryOnly))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (newest is null || written > newest) newest = written;
        }

        return newest;
    }

    private static (string Path, DateTime Written)? NewestSource(IEnumerable<string> projects)
    {
        (string Path, DateTime Written)? newest = null;

        foreach (var project in projects)
        {
            var dir = Path.GetDirectoryName(project);
            if (dir is null || !Directory.Exists(dir)) continue;

            foreach (var file in Sources(dir))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (newest is null || written > newest.Value.Written) newest = (file, written);
            }
        }

        return newest;
    }

    /// <summary>Every source below <paramref name="dir"/>, skipping what a build wrote.</summary>
    /// <remarks>
    /// Walked by hand rather than with EnumerateFiles(AllDirectories) so that bin/ and obj/ are
    /// never descended into at all. Recursing into them and filtering afterwards would visit
    /// thousands of files per project to discard every one of them.
    /// </remarks>
    private static IEnumerable<string> Sources(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (!SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                if (SourceExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// A project and everything it references, transitively.
    /// </summary>
    /// <remarks>
    /// Transitive rather than just the app's own folder, because in an engine repository the
    /// edit you are most likely to be chasing is in the ENGINE, not in the demo you ran to look
    /// at it. A check that only watched the demo's folder would stay quiet for exactly the case
    /// it exists to catch.
    /// </remarks>
    public static IReadOnlyCollection<string> Closure(string project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(project));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current) || !File.Exists(current)) continue;

            foreach (var reference in References(current))
            {
                if (!seen.Contains(reference)) pending.Push(reference);
            }
        }

        return seen;
    }

    /// <summary>The ProjectReferences one .csproj declares, as absolute paths.</summary>
    /// <remarks>
    /// Malformed XML is treated as "no references" rather than thrown: this check is an
    /// unasked-for courtesy on the way to running something, and it must never be the reason a
    /// run does not happen.
    /// </remarks>
    private static IEnumerable<string> References(string project)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(project);
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException)
        {
            yield break;
        }

        var dir = Path.GetDirectoryName(project)!;

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (include is not { Length: > 0 }) continue;

            // MSBuild writes Windows separators whatever the platform, and hand-edited files in
            // this tree contain doubled ones. Both have to become one separator here or the path
            // resolves to nothing on Unix and the reference is silently dropped.
            var normalized = include.Replace('\\', '/');
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");

            yield return Path.GetFullPath(Path.Combine(dir, normalized));
        }
    }
}
