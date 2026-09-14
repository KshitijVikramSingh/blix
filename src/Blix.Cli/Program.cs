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
                "help" or "-h" or "--help" => Help(0),
                _ => Unknown(verb),
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

    private static string ProjectName(DirectoryInfo root)
    {
        var marker = Path.Combine(root.FullName, "blix.project");
        if (File.Exists(marker))
        {
            var named = File.ReadAllText(marker).Trim();
            if (named.Length > 0) return named.Split('\n')[0].Trim();
        }

        return root.Name.ToLowerInvariant();
    }

    private static List<App> Discover(DirectoryInfo root)
    {
        var apps = new List<App>();

        foreach (var file in root.EnumerateFiles("*.blixapps.json", SearchOption.AllDirectories))
        {
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
                        app.Name, app.Summary, app.Headed, host, assembly,
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
                apps.Add(new App(name, null, false, host, assembly, null, stale, Declared: false));
            }
        }

        return apps;
    }

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

        Console.WriteLine();
        var declared = apps.Where(a => a.Declared).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
        var byConvention = apps.Where(a => !a.Declared).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();

        Print("declared", declared);
        Print("by convention", byConvention);

        if (apps.Any(a => a.Stale))
        {
            Console.WriteLine();
            Console.WriteLine("  (*) the index is older than its assembly — rebuild to be sure this is current");
        }

        return 0;
    }

    private static void Print(string heading, App[] apps)
    {
        if (apps.Length == 0) return;

        Console.WriteLine($"  {heading}");
        var width = apps.Max(a => a.Name.Length);
        foreach (var app in apps)
        {
            var mark = app.Stale ? "*" : " ";
            var kind = app.Headed ? "  [window]" : string.Empty;
            Console.WriteLine($"   {mark} {app.Name.PadRight(width)}  {app.Summary ?? string.Empty}{kind}");
        }
        Console.WriteLine();
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) throw new BlixCliException("run what? `blix ls` shows this project's apps.");

        var root = ProjectRoot();
        var wanted = args[0];
        var rest = args.Skip(1).ToArray();

        // project:app addresses across folders; a bare name means this project. Both
        // resolve the same way, which is what keeps "I am standing in it" from being a
        // different mechanism than "I am not".
        if (wanted.Contains(':'))
        {
            var parts = wanted.Split(':', 2);
            wanted = parts[1];
        }

        var app = Resolve(Discover(root), wanted);

        if (app.Stale)
        {
            Console.Error.WriteLine($"blix: the index for '{app.Name}' is older than its assembly — rebuilding.");
        }

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
                $"'{wanted}' is ambiguous: {string.Join(", ", candidates.Select(c => c.Name).Order())}");
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

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"blix: no verb '{verb}'.");
        return Help(1);
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
        string Name, string? Summary, bool Headed, string? AppHost, string Assembly,
        string? Selector, bool Stale, bool Declared);

    private sealed class BlixCliException(string message) : Exception(message);
}
