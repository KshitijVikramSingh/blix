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
//   It is also where the MoltenVK/DYLD prologue lives, once. Eleven launcher scripts
//   carried an identical copy of it because there was nowhere else to put it — not
//   because packaging was missing, but because a project had no way to say what it
//   contains. All eleven are deleted; the prologue is in ./blix, which execs this.
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
                "publish" => Publish(args.Skip(1).ToArray()),
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

            // <b>An index whose project has gone cannot describe a live application.</b> Moving
            // a folder carries its bin/ along, so the old assemblies and their indexes travel
            // with it and go on describing applications under names nothing builds any more --
            // measured: renaming the character collection made `blix ls` show every one of its
            // apps twice, once from each name, and both looked equally real.
            //
            // The index records the .csproj it was generated from precisely so this is cheap to
            // ask. It is the same failure as a cooked artifact whose recipe moved or a reflection
            // sidecar whose shader was deleted: a generated fact that outlived its authority.
            // Dropped rather than reported, because there is nothing a person can do about it and
            // nothing they would want to see -- the project does not exist.
            if (index.Project is { Length: > 0 } declaredProject && !File.Exists(declaredProject))
            {
                continue;
            }

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
                        app.IsEntryPoint ? null : app.Name, stale, Declared: true,
                        SourceProject: index.Project));
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
                apps.Add(new App(project, name, null, false, host, assembly, null, stale,
                    Declared: false, SourceProject: index.Project));
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

    /// <summary>
    /// Run one app, having first said whether it is what your sources would build.
    /// </summary>
    /// <remarks>
    /// <b>blix's own options come BEFORE the app name; everything after it belongs to the app.</b>
    /// The boundary has to be somewhere and it cannot be a list of names, because an app is free
    /// to take a <c>--build</c> of its own and this must not eat it. Position says whose an
    /// argument is without either side having to know about the other.
    /// </remarks>
    private static int Run(string[] args)
    {
        var build = false;
        var first = 0;
        for (; first < args.Length; first++)
        {
            if (args[first] is "--build" or "-b") { build = true; continue; }
            if (args[first].StartsWith('-'))
            {
                throw new BlixCliException(
                    $"'{args[first]}' is not an option of run. There is --build (-b), and every other " +
                    "argument goes to the app, so it belongs AFTER the app's name.");
            }

            break;
        }

        args = args.Skip(first).ToArray();
        if (args.Length == 0) throw new BlixCliException("run what? `blix ls` shows this project's apps.");

        var rest = args.Skip(1).ToArray();
        var app = ResolveOne(args[0]);

        if (app.Stale)
        {
            Console.Error.WriteLine(
                $"blix: the index for '{app.Name}' is older than its assembly, so this may not be all " +
                "of what that assembly declares. Build it to be sure.");
        }

        if (build)
        {
            if (Build(app) is var code and not 0) return code;
        }
        else if (Freshness.Check(app.SourceProject, app.Assembly) is { } stale)
        {
            // Reported, then run anyway. The person asked to run something and may well have
            // meant the old one -- comparing against a build is a normal thing to want. What
            // they cannot do is notice this for themselves, so the only job here is to say it.
            Console.Error.WriteLine(
                $"blix: '{app.Name}' was built before {Relative(stale.Source)} changed " +
                $"({Ago(stale.SourceWritten, stale.Built)} newer). Running it anyway — " +
                "pass --build, or run `dotnet build`, for what your sources say.");
        }

        return Launch(app, rest);
    }

    /// <summary>Build the project an app came from, and hand back dotnet's verdict.</summary>
    /// <remarks>
    /// The configuration comes from the resolved assembly's own path rather than from an
    /// environment variable: `blix run` may have picked a Release build, and rebuilding Debug
    /// because that is the default would leave the person running the very binary they just
    /// asked to replace.
    /// </remarks>
    private static int Build(App app)
    {
        if (app.SourceProject is not { Length: > 0 } project || !File.Exists(project))
        {
            throw new BlixCliException(
                $"--build needs the project '{app.Name}' was built from, and its index does not name one. " +
                "Run `dotnet build` once and the index it writes will.");
        }

        var start = new ProcessStartInfo(Muxer()) { UseShellExecute = false };
        foreach (var argument in new[] { "build", project, "-c", Configuration(app.Assembly), "--nologo" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new BlixCliException($"could not start a build of {project}");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The configuration an output path was built in, defaulting the way the front door does.</summary>
    private static string Configuration(string assembly)
    {
        var parts = assembly.Split(Path.DirectorySeparatorChar);
        var bin = Array.LastIndexOf(parts, "bin");
        return bin >= 0 && bin + 1 < parts.Length
            ? parts[bin + 1]
            : Environment.GetEnvironmentVariable("BLIX_CONFIG") is { Length: > 0 } configured
                ? configured
                : "Debug";
    }

    /// <summary>
    /// The dotnet to spawn, which is never the one on PATH if there is a better answer.
    /// </summary>
    /// <remarks>
    /// DOTNET_ROOT first, deliberately. Homebrew's `dotnet` on PATH is a "#!/bin/bash" shim and
    /// /bin/bash is SIP-protected, so going through it would strip the DYLD_* this process was so
    /// carefully given. The real binary under DOTNET_ROOT is not a shim and keeps the environment
    /// intact. Shared by running and building so the two cannot come to different answers.
    /// </remarks>
    private static string Muxer()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        return dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, "dotnet"))
            ? Path.Combine(dotnetRoot, "dotnet")
            : "dotnet";
    }

    /// <summary>A path as the person would type it, when it is below where they are standing.</summary>
    private static string Relative(string path)
    {
        var from = Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(from, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }

    /// <summary>How much newer one moment is than another, in the coarsest unit that still says it.</summary>
    private static string Ago(DateTime source, DateTime built)
    {
        var gap = source - built;
        if (gap.TotalDays >= 1) return $"{(int)gap.TotalDays}d";
        if (gap.TotalHours >= 1) return $"{(int)gap.TotalHours}h";
        if (gap.TotalMinutes >= 1) return $"{(int)gap.TotalMinutes}m";
        return $"{Math.Max(1, (int)gap.TotalSeconds)}s";
    }

    /// <summary>
    /// Find the one app a name means, from wherever the caller is standing.
    /// </summary>
    /// <remarks>
    /// Shared by run and publish rather than written twice: which app a name refers to is one
    /// question, and two verbs answering it differently is how `blix run x` and `blix publish x`
    /// come to mean different apps.
    ///
    /// <c>project:app</c> addresses across folders; a bare name means "anywhere below here",
    /// which from inside a project folder is that project and from the repository root is
    /// everything. Both resolve the same way, which keeps "I am standing in it" from being a
    /// different mechanism than "I am not".
    /// </remarks>
    private static App ResolveOne(string wanted)
    {
        var root = ProjectRoot();
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

        return Resolve(candidates, wanted);
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
            start = new ProcessStartInfo(Muxer()) { UseShellExecute = false };
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
        to.WriteLine("  blix run --build <app>     build it first (-b); blix's own options go BEFORE the name");
        to.WriteLine("  blix run <project>:<app>   reach across folders");
        to.WriteLine("  blix publish <app>         build a distributable; --target <rid>");
        to.WriteLine();
        to.WriteLine("A project is the nearest folder above you with a blix.project marker,");
        to.WriteLine("a solution, or a repository in it.");
        to.WriteLine();
        to.WriteLine("run does not build. It says so when what it is about to run is older than");
        to.WriteLine("your sources, and runs it anyway — the point is that you can tell.");
        return code;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Index(
        string Assembly, string? AppHost, bool HasEntryPoint, IndexedApp[] Apps, string? Project = null);

    private sealed record IndexedApp(string Name, string? Summary, bool Headed, bool IsEntryPoint);

    /// <param name="AppHost">The native binary, when the project builds one.</param>
    /// <param name="Assembly">Its dll, which is how an app with no apphost is run.</param>
    /// <param name="Selector">The name to pass as <c>--blix-app</c>, or null when the app IS the
    /// executable and its entry point needs no selecting.</param>
    /// <param name="Project">The project FOLDER this app is grouped under, for addressing.</param>
    /// <param name="SourceProject">The .csproj it was built from, which is what publishing needs
    /// and running does not. Null for an index written before the field existed.</param>
    private sealed record App(
        string Project, string Name, string? Summary, bool Headed, string? AppHost, string Assembly,
        string? Selector, bool Stale, bool Declared, string? SourceProject = null);


    /// <summary>
    /// One target of a publish, resolved once instead of threaded around as a string.
    /// </summary>
    /// <remarks>
    /// <b>A RID is not a packaging policy.</b> `osx-arm64` names what .NET should build; it does
    /// not say that the result is a `.app` bundle with an Info.plist, or that its native
    /// dependencies are dylibs found by rpath. Windows and Linux will want different answers to
    /// the second question while .NET hands back the same shape of string for the first, so the
    /// distinction is worth having before there is a second target rather than after.
    ///
    /// Deliberately NOT a target abstraction: one target per invocation, no list, no matrix. CI
    /// can call this three times.
    /// </remarks>
    private sealed record PublishTarget(string Rid, string Platform, string Architecture)
    {
        public static PublishTarget Parse(string rid)
        {
            var dash = rid.LastIndexOf('-');
            if (dash <= 0 || dash == rid.Length - 1)
            {
                throw new BlixCliException(
                    $"'{rid}' is not a runtime identifier. Expected something like osx-arm64.");
            }

            return new PublishTarget(rid, rid[..dash], rid[(dash + 1)..]);
        }

        /// <summary>Whether this target wants a macOS application bundle rather than a plain folder.</summary>
        public bool WantsAppBundle => Platform is "osx";
    }

    /// <summary>
    /// Build one application into something a person can run, for one target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because seven launcher scripts each grew their own `dotnet publish` plus a
    /// hand-copy of whatever that missed. The copying is gone now -- shaders and cooked assets
    /// are declared content and publish carries them -- so what is left is the part a script
    /// should never have been doing: knowing which project an app came from, and what shape the
    /// target wants its output in.
    /// </para>
    /// <para>
    /// <b>For a macOS target this means the closed thing, not merely the built thing.</b> The
    /// bundle carries its own Vulkan loader, MoltenVK and OpenAL, finds them without help from the
    /// environment, and is signed — and the command reads the finished artifact back rather than
    /// trusting the steps that produced it. A missing runtime library or a signature that will not
    /// verify stops the publish and parks what was built under a name nothing can ship, because a
    /// command that returns 0 having produced something other than what it claims is the failure
    /// this engine keeps meeting.
    /// </para>
    /// <para>
    /// What it still does not claim: no clean Mac has run one. Every measurement was taken on the
    /// machine that built the bundle, and "nothing maps out of /opt/homebrew" is the strongest
    /// proxy available here rather than a substitute for the real test.
    /// </para>
    /// </remarks>
    private static int Publish(string[] args)
    {
        if (args.Length == 0)
        {
            throw new BlixCliException("publish what? `blix ls` shows this project's apps.");
        }

        var wanted = args[0];
        var rid = ValueAfter(args, "--target") ?? DefaultRid();
        var target = PublishTarget.Parse(rid);
        var outRoot = ValueAfter(args, "--out");

        var app = ResolveOne(wanted);
        if (app.SourceProject is not { } project)
        {
            throw new BlixCliException(
                $"'{app.Name}' does not record the project it was built from — rebuild it, " +
                "and the index will.");
        }

        if (!File.Exists(project))
        {
            throw new BlixCliException($"'{app.Name}' was built from {project}, which is gone.");
        }

        var dest = Path.GetFullPath(outRoot
            ?? Path.Combine(ProjectRoot().FullName, "dist", app.Name, target.Rid));
        var payload = target.WantsAppBundle
            ? Path.Combine(dest, $"{BundleName(app)}.app", "Contents", "MacOS")
            : dest;

        Console.WriteLine($"publishing {app.Name} for {target.Rid}");

        // Self-contained, because the whole point is that the person running it did not install
        // anything. A framework-dependent publish is a second set of prerequisites wearing a
        // folder, and the launchers it replaces were all self-contained already.
        var publish = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            ArgumentList =
            {
                "publish", project,
                "-c", Environment.GetEnvironmentVariable("BLIX_CONFIG") ?? "Debug",
                "-r", target.Rid,
                "--self-contained", "true",
                "-o", payload,
                "--nologo",
            },
        }) ?? throw new BlixCliException("could not start dotnet publish");
        publish.WaitForExit();
        if (publish.ExitCode != 0) return publish.ExitCode;

        if (target.WantsAppBundle)
        {
            WriteAppBundle(app, target, dest);
            MoveDataOutOfMacOS(app, dest);
            CloseMacRuntime(app, dest);
            // Before signing, because a seal over an incomplete bundle is a worse lie than no seal.
            VerifyMacBundle(app, dest);
            SignAppBundle(app, dest);
        }

        Console.WriteLine($"  {dest}");
        return 0;
    }

    /// <summary>The bundle's name: the apphost's, because that is the binary Info.plist names.</summary>
    private static string BundleName(App app) =>
        app.AppHost is { } host ? Path.GetFileName(host) : app.Name;

    /// <summary>
    /// The two files that make a folder of Mach-O into something Finder will launch.
    /// </summary>
    /// <remarks>
    /// Written here rather than by the project, because it is a property of the TARGET and not of
    /// the application: the same app published for win-x64 wants neither of them. This is the
    /// first thing in the tree that is packaging policy rather than build policy, which is why
    /// PublishTarget exists to be asked instead of a RID string being matched on.
    /// </remarks>
    private static void WriteAppBundle(App app, PublishTarget target, string dest)
    {
        var bundle = Path.Combine(dest, $"{BundleName(app)}.app");
        var contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "Resources"));

        File.WriteAllText(Path.Combine(contents, "Info.plist"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleName</key><string>{BundleName(app)}</string>
              <key>CFBundleDisplayName</key><string>{app.Name}</string>
              <key>CFBundleIdentifier</key><string>dev.blix.{BundleId(app)}</string>
              <key>CFBundleExecutable</key><string>{BundleName(app)}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleVersion</key><string>1.0</string>
              <key>CFBundleShortVersionString</key><string>1.0</string>
              <key>LSMinimumSystemVersion</key><string>11.0</string>
              <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>

            """);

        // A bundle with no CFBundleIconFile is a bundle Finder draws a blank page for. The mark
        // is not converted here — that needs iconutil and belongs with the icon work — so the
        // key is left out rather than pointing at a file that is not there.
        File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????");
    }

    /// <summary>The reverse-DNS tail, without repeating the "blix" the prefix already said.</summary>
    private static string BundleId(App app)
    {
        var name = app.Name.ToLowerInvariant();
        return name.StartsWith("blix.", StringComparison.Ordinal) ? name["blix.".Length..] : name;
    }

    /// <summary>
    /// Copy the native runtime the application needs into the bundle, so it needs nothing installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the line between a developer dependency and a runtime one.</b> glslc and
    /// spirv-cross ran at build time and are not here. MoltenVK, the Vulkan loader and OpenAL
    /// Soft are needed by the application itself every time it runs, so they travel with it —
    /// a shipped .app cannot tell somebody to install Homebrew.
    /// </para>
    /// <para>
    /// <b>Both loader filenames, deliberately.</b> GLFW dlopens <c>libvulkan.1.dylib</c> and
    /// Silk.NET's own resolver asks for <c>libvulkan.dylib</c>. They are separate name lists, and
    /// a bundle carrying one of them gets past whichever asks first and dies on the other — which
    /// cost an afternoon to find, because each failure looked like a different bug.
    /// </para>
    /// <para>
    /// The ICD manifest is written rather than copied, because Homebrew's points at Homebrew.
    /// Its <c>library_path</c> is relative to the manifest, so a bundle-relative one keeps
    /// working wherever the bundle is moved.
    /// </para>
    /// <para>
    /// Sourced from the build machine's Homebrew, which is honest rather than ideal: it means a
    /// publish inherits whatever version that machine has. Pinning the runtime is a real question
    /// and not this one.
    /// </para>
    /// </remarks>
    private static void CloseMacRuntime(App app, string dest)
    {
        var prefix = new[] { "/opt/homebrew", "/usr/local" }
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "lib", "libvulkan.dylib")));
        if (prefix is null)
        {
            throw new BlixCliException(
                "no Vulkan runtime found to bundle, so the .app cannot be closed. Expected Homebrew " +
                "under /opt/homebrew or /usr/local. This is fatal rather than a warning because " +
                "`publish` for this target means \"produce the closed runtime\", and a command that " +
                "returns 0 having not done that is the exact failure this engine keeps hunting.");
        }

        var contents = Path.Combine(dest, $"{BundleName(app)}.app", "Contents");
        var frameworks = Path.Combine(contents, "Frameworks");
        Directory.CreateDirectory(frameworks);

        var wanted = new[]
        {
            Path.Combine(prefix, "lib", "libvulkan.dylib"),
            Path.Combine(prefix, "lib", "libvulkan.1.dylib"),
            Path.Combine(prefix, "lib", "libMoltenVK.dylib"),
            Path.Combine(prefix, "opt", "openal-soft", "lib", "libopenal.1.dylib"),
            Path.Combine(prefix, "lib", "libopenal.1.dylib"),
        };

        var copied = 0;
        foreach (var source in wanted)
        {
            var name = Path.GetFileName(source);
            var into = Path.Combine(frameworks, name);
            if (!File.Exists(source) || File.Exists(into)) continue;

            // Resolve the symlink chain: Homebrew's lib/ is links into Cellar, and a link into a
            // directory the target machine does not have is not a dependency that travelled.
            File.Copy(new FileInfo(source).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? source, into);
            copied++;
        }

        var icdDir = Path.Combine(contents, "Resources", "vulkan", "icd.d");
        Directory.CreateDirectory(icdDir);
        File.WriteAllText(Path.Combine(icdDir, "MoltenVK_icd.json"),
            """
            {
                "file_format_version": "1.0.0",
                "ICD": {
                    "library_path": "../../../Frameworks/libMoltenVK.dylib",
                    "api_version": "1.4.0",
                    "is_portability_driver": true
                }
            }

            """);

        Console.WriteLine($"  bundled {copied} native librar{(copied == 1 ? "y" : "ies")} from {prefix}");
    }

    /// <summary>
    /// Move the application's data out of <c>Contents/MacOS</c>, which is for Mach-O only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not tidiness: codesign refuses the bundle otherwise. A cooked <c>foo.textures/</c> sidecar
    /// is a directory with an extension, which codesign reads as a nested bundle and rejects as
    /// "bundle format unrecognized" — one such directory in Bulwark was enough to fail the whole
    /// signature. Under Resources it is data, and is sealed by hash without being interpreted.
    /// </para>
    /// <para>
    /// The runtime half of this is <c>Blix.Core.AppFiles</c>, which looks here when there is no
    /// Assets directory beside the binary. The two have to agree, and this comment is the
    /// other end of the one over there.
    /// </para>
    /// </remarks>
    private static void MoveDataOutOfMacOS(App app, string dest)
    {
        var contents = Path.Combine(dest, $"{BundleName(app)}.app", "Contents");
        var from = Path.Combine(contents, "MacOS", "Assets");
        if (!Directory.Exists(from)) return;

        var to = Path.Combine(contents, "Resources", "Assets");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.Move(from, to);
    }

    /// <summary>
    /// Judge the finished bundle against what this target promised, and fail if it falls short.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because every step above reports on itself.</b> `blix publish` for a macOS target means
    /// "produce the closed runtime", and the way that quietly stops being true is a step that
    /// copied four files where five were needed and said so cheerfully. This reads the bundle
    /// back instead, so the command is an instrument rather than a sequence of hopeful actions.
    /// </para>
    /// <para>
    /// REQUIRED is the runtime closure and nothing else: the apphost, the plist, both loader
    /// filenames (GLFW asks for one, Silk.NET's resolver the other), MoltenVK, OpenAL and the ICD
    /// manifest. Shaders and cooked assets are deliberately absent — an application that declares
    /// none is not incomplete, and `dotnet publish` already carries what was declared. Icons,
    /// Developer ID and notarisation are not closure at all.
    /// </para>
    /// </remarks>
    private static void VerifyMacBundle(App app, string dest)
    {
        var bundle = Path.Combine(dest, $"{BundleName(app)}.app");
        var contents = Path.Combine(bundle, "Contents");
        var required = new (string What, string Path)[]
        {
            ("the executable", Path.Combine(contents, "MacOS", BundleName(app))),
            ("Info.plist", Path.Combine(contents, "Info.plist")),
            ("the Vulkan loader (GLFW's name)", Path.Combine(contents, "Frameworks", "libvulkan.1.dylib")),
            ("the Vulkan loader (Silk.NET's name)", Path.Combine(contents, "Frameworks", "libvulkan.dylib")),
            ("MoltenVK", Path.Combine(contents, "Frameworks", "libMoltenVK.dylib")),
            ("OpenAL Soft", Path.Combine(contents, "Frameworks", "libopenal.1.dylib")),
            ("the ICD manifest", Path.Combine(contents, "Resources", "vulkan", "icd.d", "MoltenVK_icd.json")),
        };

        var missing = required.Where(r => !File.Exists(r.Path)).ToArray();
        if (missing.Length > 0)
        {
            throw new BlixCliException(
                "the bundle is not closed — " +
                string.Join("; ", missing.Select(m => $"{m.What} is missing ({Path.GetFileName(m.Path)})")) +
                ". Publishing stopped rather than hand you a bundle that runs only here." +
                ParkIncomplete(bundle));
        }

        Console.WriteLine($"  closure verified: {required.Length} required parts present");
    }

    /// <summary>Ad-hoc sign the finished bundle, so the machine it lands on will run it.</summary>
    /// <remarks>
    /// <para>
    /// The signature has to come last: copying libraries into Contents/Frameworks, moving the
    /// assets and writing the ICD manifest all invalidate a seal made before them.
    /// </para>
    /// <para>
    /// <c>--deep</c> rather than a plain sign, and deprecated though it is: a self-contained
    /// publish drops managed assemblies beside the apphost, and codesign counts a <c>.dll</c> as
    /// nested code it will not seal unsigned (measured -- it named System.Net.WebSockets.Client
    /// and stopped). Signing each by hand is the same walk with more rope.
    /// </para>
    /// <para>
    /// Ad-hoc, not Developer ID: this identifies nothing and gets no Gatekeeper pass, so a
    /// download still needs the quarantine bit cleared. What it buys is that the bundle is
    /// internally consistent -- an unsigned or stale-signed .app is killed on launch on Apple
    /// silicon, where a valid signature is not optional.
    /// </para>
    /// <para>
    /// Only attempted where codesign exists, and never fatal: an unsigned bundle still runs on
    /// the machine that built it, which is where most of them are run.
    /// </para>
    /// </remarks>
    private static void SignAppBundle(App app, string dest)
    {
        var bundle = Path.Combine(dest, $"{BundleName(app)}.app");
        if (!Directory.Exists(bundle) || !File.Exists("/usr/bin/codesign")) return;

        var (signed, complaint) = RunCodesign("--force", "--deep", "--sign", "-", bundle);
        if (!signed)
        {
            throw new BlixCliException(
                $"could not sign the bundle: {complaint}. A bundle whose seal does not verify is " +
                "not one this command should claim to have produced." + ParkIncomplete(bundle));
        }

        Console.WriteLine("  signed the bundle ad-hoc");

        // <b>And then ask, rather than assume.</b> Everything before this point judged the bundle
        // as a set of files; the signature is a property of the finished artifact and can only be
        // checked once it exists. Signing and then hoping is the shape this whole command spent an
        // arc removing.
        var (valid, why) = RunCodesign("--verify", "--strict", bundle);
        if (!valid)
        {
            throw new BlixCliException(
                $"the bundle signed but does not verify: {why}." + ParkIncomplete(bundle));
        }

        Console.WriteLine("  signature verifies");
    }

    /// <summary>Run codesign, and say whether it was happy.</summary>
    /// <remarks>
    /// The catch is narrow on purpose. A <c>catch (Exception)</c> around the whole of the previous
    /// version swallowed the <see cref="BlixCliException"/> that version threw to make a signing
    /// failure fatal — so the failure printed, the command carried on, and publish returned 0
    /// having produced an unsigned bundle. Measured, not deduced: with a bogus identity the exit
    /// code was 0 and the message arrived doubled, which was the catch wrapping its own throw.
    /// Only the process failing to start is translated here; a verdict is returned, not thrown.
    /// </remarks>
    private static (bool Ok, string Complaint) RunCodesign(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("/usr/bin/codesign")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);

            var run = Process.Start(info);
            if (run is null) return (false, "codesign did not start");

            var complaint = run.StandardError.ReadToEnd();
            run.WaitForExit();
            return (run.ExitCode == 0, complaint.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Rename a bundle that failed its checks, so nothing can ship it by accident.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, because a failed publish is worth looking at — but not under a
    /// name Finder will launch. Returns the sentence to append to the refusal.
    /// </remarks>
    private static string ParkIncomplete(string bundle)
    {
        var parked = bundle + ".incomplete";
        try
        {
            if (Directory.Exists(parked)) Directory.Delete(parked, recursive: true);
            Directory.Move(bundle, parked);
            return $" What was built is at {Path.GetFileName(parked)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth masking the real error over.
            return $" ({Path.GetFileName(bundle)} could not be set aside: {ex.Message})";
        }
    }

    private static string DefaultRid() =>
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }

        return null;
    }

    private sealed class BlixCliException(string message) : Exception(message);
}
