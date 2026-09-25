using System.Diagnostics;
using System.Text.Json;

namespace Blix.Cli;

// blix — find a project's apps and run one.
//
// ── What this is, and what it deliberately is not ───────────────────────────
//   A resolver. It reads the indexes written at build time, matches a name, and
//   execs. It hosts nothing, provides nothing, and an app does not know it exists:
//   an app is still a Main, and this only learns how to FIND it.
//
//   It is also where the MoltenVK/DYLD prologue lives, once. Fourteen launcher
//   scripts carried an identical copy of it because there was nowhere else to put
//   it — not because packaging was missing, but because a project had no way to say
//   what it contains. The prologue is in tools/blix, which execs this binary.
//
// ── Why this is a binary and the prologue is a script ───────────────────────
//   The environment has to be set before the process starts and has to survive into
//   it. Homebrew's `dotnet` is a #!/bin/bash shim and /bin/bash is SIP-protected, so
//   dyld strips DYLD_* across it — which is why the script execs this APPHOST rather
//   than running `dotnet blix.dll`. Everything this process spawns then inherits a
//   correct environment for free, which is what makes a headed app runnable through
//   two hops.
public static class Program
{
    public static int Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0] : "help";

        try
        {
            return verb switch
            {
                "ls" or "list" => List(),
                "run" => Run(args.Skip(1).ToArray()),
                "test" => Test(args.Skip(1).ToArray()),
                "help" or "-h" or "--help" => Help(0),
                // A bare name is a run. `blix view rogue.glb` reads better than
                // `blix run view rogue.glb`, and underneath it is the same thing — which is
                // what keeps Blix's own tools from becoming a special class of app.
                _ => Run(args),
            };
        }
        catch (BlixCliException failure)
        {
            Console.Error.WriteLine($"blix: {failure.Message}");
            return 1;
        }
    }

    // ── discovery ───────────────────────────────────────────────────────────

    /// <summary>
    /// The project this command is about: the nearest folder above the working directory
    /// that says it is one.
    /// </summary>
    /// <remarks>
    /// A project is a folder, so the working directory is what picks it — the same way
    /// every version-control and build tool already behaves, and the reason you never
    /// have to name a project you are standing in.
    /// <para>
    /// The fallbacks matter more than the marker. A tree with no <c>blix.project</c>
    /// anywhere still resolves to its repository root, so this is useful before a single
    /// folder has been migrated. Adding markers later only makes addressing finer; it is
    /// not a precondition for anything.
    /// </para>
    /// </remarks>
    private static DirectoryInfo ProjectRoot()
    {
        var from = new DirectoryInfo(Directory.GetCurrentDirectory());

        for (var dir = from; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "blix.project"))) return dir;
        }

        for (var dir = from; dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.sln").Any() || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir;
            }
        }

        throw new BlixCliException(
            $"no Blix project at or above {from.FullName} — a project is a folder with a " +
            "blix.project marker, a solution, or a repository in it.");
    }

    /// <summary>The nearest project marker at or above <paramref name="from"/>, stopping at the root.</summary>
    private static string? OwningProject(DirectoryInfo? from, DirectoryInfo root)
    {
        for (var dir = from; dir is not null; dir = dir.Parent)
        {
            var marker = Path.Combine(dir.FullName, "blix.project");
            if (File.Exists(marker)) return ProjectName(dir);
            if (string.Equals(dir.FullName, root.FullName, StringComparison.Ordinal)) break;
        }

        return null;
    }

    private static string ProjectName(DirectoryInfo root) => Marker(root).Name;

    /// <summary>
    /// What <c>blix.project</c> says: a name on the first line, then optional <c>key: value</c> lines.
    /// </summary>
    /// <remarks>
    /// <b>The one hand-written file in this layer, and it earns it.</b> Everything about an app
    /// comes from the app — the index is generated precisely so it cannot drift. But "what
    /// constitutes verification for this project" is not a fact about any one app; it is a
    /// statement the project makes about itself, and no attribute can carry it. Longer or
    /// specialized verification can remain in project-owned scripts beside this quick gate.
    /// </remarks>
    private static Marked Marker(DirectoryInfo root)
    {
        var path = Path.Combine(root.FullName, "blix.project");
        if (!File.Exists(path)) return new Marked(root.Name.ToLowerInvariant(), Array.Empty<string>());

        var lines = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        var name = lines.FirstOrDefault(l => !l.Contains(':')) ?? root.Name.ToLowerInvariant();

        var gate = lines
            .FirstOrDefault(l => l.StartsWith("test:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        return new Marked(name, gate);
    }

    private sealed record Marked(string Name, string[] Gate);

    private static List<App> Discover(DirectoryInfo root)
    {
        var apps = new List<App>();
        var rootName = ProjectName(root);

        foreach (var file in root.EnumerateFiles("*.blixapps.json", SearchOption.AllDirectories))
        {
            // Which project this app belongs to: the nearest marker ABOVE it, or the
            // root's own name when there is none. This is what makes the marker do real
            // work rather than only scope a listing — two projects may both declare
            // "selftest", and `project:selftest` is how you say which.
            var project = OwningProject(file.Directory, root) ?? rootName;
            Index? index;
            try
            {
                index = JsonSerializer.Deserialize<Index>(File.ReadAllText(file.FullName), JsonOptions);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"blix: {file.FullName} is not readable as an index — ignoring it.");
                continue;
            }

            if (index is null) continue;

            var assembly = Path.Combine(file.DirectoryName!, index.Assembly);

            // No apphost is normal, not broken: the test suites and the cooker set
            // UseAppHost=false deliberately. Such an app runs through the dotnet host
            // with its dll instead, and the resolver carries that difference so the
            // project never has to change to be reachable.
            var host = index.AppHost is null ? null : Path.Combine(file.DirectoryName!, index.AppHost);

            // A stale index is REPORTED, never silently believed. It is the one failure
            // mode generating rather than hand-writing the index cannot rule out, and a
            // tool that has quietly moved is worse than one that says it might have.
            var stale = File.Exists(assembly)
                && File.GetLastWriteTimeUtc(assembly) > file.LastWriteTimeUtc.AddSeconds(1);

            {
                foreach (var app in index.Apps)
                {
                    // An app declared ON the entry point needs no selector: exec it and it
                    // simply runs. Only an assembly carrying SEVERAL apps has to be told
                    // which one, and only those callers need BlixApps.Dispatch at all.
                    apps.Add(new App(
                        project, app.Name, app.Summary, app.Headed, host, assembly,
                        app.IsEntryPoint ? null : app.Name, stale, Declared: true));
                }
            }

            // CONVENTION: an executable is runnable, named after itself, unless one of
            // its declarations already IS the entry point and has given it a better name.
            //
            // The condition is the whole rule. Suppressing this whenever an assembly
            // declares anything was too blunt: a project that declares thirty-five tools
            // would lose its application, which is the one app it certainly has. An
            // executable being runnable is simply true, and declaring only ever renames
            // it or adds neighbours.
            if (index.HasEntryPoint && !index.Apps.Any(a => a.IsEntryPoint))
            {
                var name = Path.GetFileNameWithoutExtension(index.Assembly);
                apps.Add(new App(project, name, null, false, host, assembly, null, stale, Declared: false));
            }
        }

        return Deduplicate(apps);
    }

    /// <summary>
    /// One app per (project, name, assembly), however many output directories hold it.
    /// </summary>
    /// <remarks>
    /// A RID-specific build writes its outputs twice — <c>bin/Debug/net8.0</c> and
    /// <c>bin/Debug/net8.0/osx-arm64</c> — so the same assembly gets two sidecars and
    /// every app in it looked ambiguous with itself. The key includes the assembly file
    /// name deliberately: two DIFFERENT assemblies in one project claiming one name is a
    /// real ambiguity and must still be reported.
    /// <para>
    /// The copy with a working apphost wins, then the shallower path, so the answer does
    /// not depend on directory enumeration order.
    /// </para>
    /// </remarks>
    private static List<App> Deduplicate(List<App> apps) => apps
        .GroupBy(a => (a.Project, a.Name, Assembly: Path.GetFileName(a.Assembly)))
        .Select(g => g
            .OrderByDescending(a => a.AppHost is not null && File.Exists(a.AppHost))
            .ThenBy(a => a.Assembly.Length)
            .First())
        .ToList();

    // ── verbs ───────────────────────────────────────────────────────────────

    private static int List()
    {
        var root = ProjectRoot();
        var apps = Discover(root);

        Console.WriteLine($"{ProjectName(root)} — {root.FullName}");

        if (apps.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  no apps indexed yet. Build the project once and try again:");
            Console.WriteLine("    dotnet build");
            return 0;
        }

        foreach (var group in apps.GroupBy(a => a.Project).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine($"  {group.Key}");
            Print(group.Where(a => a.Declared).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray());
            Print(group.Where(a => !a.Declared).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray());
        }

        if (apps.Any(a => a.Stale))
        {
            Console.WriteLine();
            Console.WriteLine("  (*) the index is older than its assembly — rebuild to be sure this is current");
        }

        return 0;
    }

    private static void Print(App[] apps)
    {
        if (apps.Length == 0) return;

        var width = apps.Max(a => a.Name.Length);
        foreach (var app in apps)
        {
            var mark = app.Stale ? "*" : " ";
            var kind = app.Headed ? "  [window]" : string.Empty;
            var summary = app.Summary is null ? string.Empty : $"  {app.Summary}";
            Console.WriteLine($"   {mark} {app.Name.PadRight(width)}{summary}{kind}");
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) throw new BlixCliException("run what? `blix ls` shows this project's apps.");

        var root = ProjectRoot();
        var wanted = args[0];
        var rest = args.Skip(1).ToArray();

        // project:app addresses across folders; a bare name means "anywhere below here",
        // which from inside a project folder is that project and from the repository root
        // is everything. Both resolve the same way, which is what keeps "I am standing in
        // it" from being a different mechanism than "I am not".
        var candidates = Discover(root);
        if (wanted.Contains(':'))
        {
            var parts = wanted.Split(':', 2);
            var project = parts[0];
            wanted = parts[1];

            var scoped = candidates
                .Where(a => string.Equals(a.Project, project, StringComparison.OrdinalIgnoreCase)).ToList();

            if (scoped.Count == 0)
            {
                var known = candidates.Select(a => a.Project).Distinct().Order(StringComparer.Ordinal);
                throw new BlixCliException(
                    $"no project '{project}' below {root.FullName}. There is: {string.Join(", ", known)}");
            }

            candidates = scoped;
        }

        var app = Resolve(candidates, wanted);

        if (app.Stale)
        {
            Console.Error.WriteLine($"blix: the index for '{app.Name}' is older than its assembly — rebuilding.");
        }

        return Launch(app, rest);
    }

    /// <summary>Start an app, wait for it, and hand back its exit code.</summary>
    private static int Launch(App app, string[] rest)
    {
        var forwarded = app.Selector is null
            ? rest
            : new[] { "--blix-app", app.Selector }.Concat(rest).ToArray();

        ProcessStartInfo start;
        if (app.AppHost is not null && File.Exists(app.AppHost))
        {
            start = new ProcessStartInfo(app.AppHost) { UseShellExecute = false };
        }
        else if (File.Exists(app.Assembly))
        {
            // DOTNET_ROOT first, deliberately. Homebrew's `dotnet` on PATH is a
            // "#!/bin/bash" shim and /bin/bash is SIP-protected, so going through it
            // would strip the DYLD_* this process was so carefully given. The real
            // binary under DOTNET_ROOT is not a shim and keeps the environment intact.
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            var muxer = dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, "dotnet"))
                ? Path.Combine(dotnetRoot, "dotnet")
                : "dotnet";

            start = new ProcessStartInfo(muxer) { UseShellExecute = false };
            start.ArgumentList.Add(app.Assembly);
        }
        else
        {
            throw new BlixCliException(
                $"'{app.Name}' is indexed but not built — nothing at {app.Assembly}.");
        }

        foreach (var argument in forwarded) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new BlixCliException($"could not start {app.AppHost}");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Run everything this project calls its gate, and return one verdict.
    /// </summary>
    /// <remarks>
    /// <b>The step run most often in a session gets one project declaration and one verdict.</b>
    /// Before this gate, proving a change meant several invocations and a person reconciling several
    /// outputs, which is the shape of thing that gets skipped. It keeps going after a failure so you
    /// learn everything that is broken rather than only the first thing.
    /// </remarks>
    private static int Test(string[] args)
    {
        var root = ProjectRoot();
        var marker = Marker(root);

        if (marker.Gate.Length == 0)
        {
            throw new BlixCliException(
                $"'{marker.Name}' declares no gate. Add a line to {Path.Combine(root.FullName, "blix.project")}:\n" +
                "    test: <app>, <app>, ...");
        }

        var apps = Discover(root);
        var failed = new List<string>();

        foreach (var name in marker.Gate)
        {
            var app = Resolve(apps, name);
            Console.WriteLine();
            Console.WriteLine($"=== {app.Name}");

            // <b>A gate leg that opens a window waits for a person, which makes it not a
            // gate.</b> Said rather than refused, because a headed app given --frames does
            // exit on its own and is a legitimate leg; what is never legitimate is finding
            // out by watching a window appear and wondering why the run stopped.
            //
            // It only covers DECLARED apps. A convention app has no Headed to read — nothing
            // said so and metadata cannot tell — so an undeclared window still surprises you.
            // That is the first thing declaring buys beyond a better name, and the honest
            // shape of the gap rather than a guess dressed as a check.
            if (app.Headed && !args.Any(a => a.StartsWith("--frames", StringComparison.Ordinal)))
            {
                Console.Error.WriteLine(
                    $"blix: '{app.Name}' opens a window and will wait for you to close it. " +
                    "Pass --frames N to bound it.");
            }

            var code = Launch(app, args);
            if (code != 0) failed.Add($"{app.Name} ({code})");
        }

        Console.WriteLine();
        if (failed.Count == 0)
        {
            Console.WriteLine($"{marker.Name}: all {marker.Gate.Length} green");
            return 0;
        }

        Console.Error.WriteLine($"{marker.Name}: {failed.Count} of {marker.Gate.Length} failed — {string.Join(", ", failed)}");
        return 1;
    }

    /// <summary>
    /// Match a name, and fail loudly when it does not.
    /// </summary>
    /// <remarks>
    /// Exact first, then case-insensitive, then an unambiguous suffix after the last dot
    /// — so <c>blix run Physics3D</c> reaches <c>Blix.Test.Physics3D</c> without anyone
    /// having to declare a short name for it. Ambiguity is an error listing the
    /// candidates rather than a silent first-match, because a tool that runs the wrong
    /// thing is worse than one that refuses.
    /// </remarks>
    private static App Resolve(List<App> apps, string wanted)
    {
        var exact = apps.Where(a => a.Name == wanted).ToArray();
        if (exact.Length == 1) return exact[0];

        var insensitive = apps.Where(a => string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (insensitive.Length == 1) return insensitive[0];

        var suffix = apps.Where(a =>
            string.Equals(Tail(a.Name), wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (suffix.Length == 1) return suffix[0];

        var candidates = exact.Length > 1 ? exact : insensitive.Length > 1 ? insensitive : suffix;
        if (candidates.Length > 1)
        {
            throw new BlixCliException(
                $"'{wanted}' is ambiguous — say which: " +
                string.Join(", ", candidates.Select(c => $"{c.Project}:{c.Name}").Order(StringComparer.Ordinal)));
        }

        var known = apps.Select(a => a.Name).Order(StringComparer.Ordinal).ToArray();
        throw new BlixCliException(
            $"no app named '{wanted}'. This project has {known.Length}: {string.Join(", ", known)}");
    }

    private static string Tail(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static int Help(int code)
    {
        var to = code == 0 ? Console.Out : Console.Error;
        to.WriteLine("blix — find a project's apps and run one.");
        to.WriteLine();
        to.WriteLine("  blix ls                    what this project has");
        to.WriteLine("  blix run <app> [args...]   run one; args after the name are the app's own");
        to.WriteLine("  blix run <project>:<app>   reach across folders");
        to.WriteLine();
        to.WriteLine("A project is the nearest folder above you with a blix.project marker,");
        to.WriteLine("a solution, or a repository in it.");
        return code;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Index(string Assembly, string? AppHost, bool HasEntryPoint, IndexedApp[] Apps);

    private sealed record IndexedApp(string Name, string? Summary, bool Headed, bool IsEntryPoint);

    /// <param name="AppHost">The native binary, when the project builds one.</param>
    /// <param name="Assembly">Its dll, which is how an app with no apphost is run.</param>
    /// <param name="Selector">The name to pass as <c>--blix-app</c>, or null when the app IS the
    /// executable and its entry point needs no selecting.</param>
    private sealed record App(
        string Project, string Name, string? Summary, bool Headed, string? AppHost, string Assembly,
        string? Selector, bool Stale, bool Declared);

    private sealed class BlixCliException(string message) : Exception(message);
}
