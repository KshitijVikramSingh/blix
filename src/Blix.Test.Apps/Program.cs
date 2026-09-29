using Blix.Verify;
using System.Reflection;
using System.Text.Json;
using Blix.Cooked;
using Blix.Core;
using Blix.Cli;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Runtime.Headless;

namespace Blix.Test.Apps;

// CLI test harness for the app layer — declaration, discovery and dispatch.
//
// Same shape as the other suites: GREEN OK / RED FAIL per case, non-zero exit if any
// fails. It is also the layer's first real consumer of MANY APPS IN ONE ASSEMBLY,
// which is not a contrivance — a suite that tests dispatch needs several things to
// dispatch to, and declaring them is cheaper than mocking them.
//
// ── The check that matters most ─────────────────────────────────────────────
//   There are two readers of one truth: Blix.Tools.Apps reads metadata at BUILD time
//   and writes the index; BlixApps.Find reads the loaded assembly at RUN time. They
//   agree because they read the same attributes — and "because" is a claim, so
//   IndexMatchesReflection checks it. If the two ever disagree, every other guarantee
//   in this layer is worthless, because `blix ls` would be describing a program that
//   does not exist.
public static class Program
{
    // The dispatch path itself. A selector reaches one of the fixtures below and this suite
    // never runs; without one, and with no default declared here, the suite is what runs.
    public static int Main(string[] args) => BlixApps.Main(args, _ => Suite());

    private static int Suite()
    {
        var t = new TestRunner();
        var self = Assembly.GetExecutingAssembly();

        // ── declaration ─────────────────────────────────────────────────────
        var found = BlixApps.Find(self);
        t.Expect("one assembly declares many apps", found.Length >= 4, $"found {found.Length}");
        t.Expect("a declared name is discoverable", found.Any(a => a.Name == "fixture-echo"));
        t.Expect("a summary survives to the reader",
            found.Single(a => a.Name == "fixture-echo").Summary is { Length: > 0 });
        t.Expect("headed is carried, not inferred",
            found.Single(a => a.Name == "fixture-headed").Headed);
        t.Expect("and headless is the default",
            !found.Single(a => a.Name == "fixture-echo").Headed);
        t.Expect("nothing here is marked default, so the suite runs unnamed",
            !found.Any(a => a.Default));

        // ── the two readers agree ───────────────────────────────────────────
        IndexMatchesReflection(t, self, found);

        // ── and recipes, read by the same two readers ────────────────────────
        // <b>The index gained a second kind of entry, so it gained a second way to be wrong.</b>
        // Blix.Tools.Apps reads [Recipe] out of ECMA-335 at build; BlixRecipes.Find reads it off
        // the loaded assembly at run time. The declarations below exist so this suite is a real
        // consumer of both — a fixture is cheaper than a mock and cannot drift from the thing it
        // stands for.
        var recipes = BlixRecipes.Find(self);
        t.Expect("a recipe declared here is found by reflection", recipes.Length == 2, $"found {recipes.Length}");
        t.Expect("its id survives", recipes.Any(r => r.Id == "fix1"));
        t.Expect("what it consumes survives, split", recipes.Single(r => r.Id == "fix1").Consumes.Length == 2);
        t.Expect("what it produces survives", recipes.Single(r => r.Id == "fix1").Produces == ".fixture");
        t.Expect("its version survives", recipes.Single(r => r.Id == "fix2").Version == 9u);

        RecipeIndexMatchesReflection(t, self, recipes);

        // A recipe is invoked by the same reflection that found it — the path a build rule takes.
        var outcome = recipes.Single(r => r.Id == "fix1")
            .Cook(new CookRequest("in.fixture-a", "out.fixture"));
        t.Expect("a recipe found by reflection can be run", outcome.Wrote);
        t.Expect("and its outcome comes back", outcome.Detail == "in.fixture-a -> out.fixture");

        // ── the command line, read ──────────────────────────────────────────
        ArgumentsAreReadNotParsed(t);

        // ── dispatch ────────────────────────────────────────────────────────
        t.Expect("no selector and no default runs what the caller hands in",
            Run(out _, self, new string[0], otherwise: _ => 42) == 42);

        t.Expect("a selector runs the named app and returns its code",
            Run(out _, self, BlixApps.Selector, "fixture-code") == 7);

        t.Expect("an app that returns void is a success",
            Run(out _, self, BlixApps.Selector, "fixture-void") == 0);

        // The selector is removed before the app sees it, which is what lets an app's own reading
        // stay ignorant of this layer. Echo returns its positional count.
        t.Expect("the selector is stripped from the app's arguments",
            Run(out _, self, "a", BlixApps.Selector, "fixture-echo", "b", "c") == 3,
            "expected the app to see exactly a, b, c");

        // ── what nobody read is said out loud ───────────────────────────────
        Run(out var typo, self, BlixApps.Selector, "fixture-reads-years", "--years", "3", "--year", "4");
        t.Expect("an argument nothing read is one warning line",
            typo.Contains("nothing read --year 4", StringComparison.Ordinal), typo.Trim());
        t.Expect("and one that was read is not in it",
            !typo.Contains("--years", StringComparison.Ordinal), typo.Trim());

        Run(out var clean, self, BlixApps.Selector, "fixture-reads-years", "--years", "3");
        t.Expect("a fully read command line prints nothing", clean.Length == 0, clean.Trim());

        var malformed = Run(out var complaint, self, BlixApps.Selector, "fixture-reads-years", "--years", "three");
        t.Expect("a value that cannot mean what its reader asked for exits 2",
            malformed == 2, $"exit {malformed}");
        t.Expect("and says which flag and what it got",
            complaint.Contains("--years", StringComparison.Ordinal)
            && complaint.Contains("'three'", StringComparison.Ordinal), complaint.Trim());

        // ── typed parameters: sugar over the same view ──────────────────────
        t.Expect("typed parameters bind from their flags",
            Run(out _, self, BlixApps.Selector, "fixture-typed", "--years", "3", "--map-seed", "40", "--fog",
                "--view", "shadow-map", "--scale", "0.5", "--tint", "a", "--tint", "b") == 3 + 40 + 100 + 1000 + 5 + 20000,
            "the fixture sums what it was given");
        t.Expect("and leave out what has a default",
            Run(out _, self, BlixApps.Selector, "fixture-typed", "--years", "2") == 2 + 7);
        t.Expect("a squashed name reaches a kebab parameter",
            Run(out _, self, BlixApps.Selector, "fixture-typed", "--years", "1", "--mapseed", "40") == 41);
        var missing = Run(out var needs, self, BlixApps.Selector, "fixture-typed");
        t.Expect("a parameter with no default is required, and exits 2", missing == 2, $"exit {missing}");
        t.Expect("naming the flag and its type", needs.Contains("--years <int>", StringComparison.Ordinal), needs.Trim());
        t.Expect("typed parameters and AppArgs mix",
            Run(out _, self, BlixApps.Selector, "fixture-mixed", "--count", "2", "--extra", "5") == 7);
        t.Expect("usage is written from the parameters",
            AppParameters.Usage(found.Single(a => a.Name == "fixture-typed").Method)
                == "--years <int> --tint <text, repeatable> --map-seed <int>=7 --fog --view <lit|shadow-map>=lit [--scale <number>]",
            AppParameters.Usage(found.Single(a => a.Name == "fixture-typed").Method));

        // ── setup every app shares ──────────────────────────────────────────
        t.Expect("the assembly's startup method is found",
            BlixApps.FindStartup(self)?.Name == nameof(Startup));
        var before = startupRuns;
        var seen = Run(out var leverNoise, self, BlixApps.Selector, "fixture-sees-startup", "--lever", "5");
        t.Expect("startup runs before the named app, and what it read reaches it",
            seen == 5 && startupRuns == before + 1, $"app saw {seen}, startup ran {startupRuns - before} time(s)");
        t.Expect("and what startup read is not reported unread", leverNoise.Length == 0, leverNoise.Trim());
        var beforeOtherwise = startupRuns;
        Run(out _, self, new string[0], otherwise: _ => 0);
        t.Expect("startup runs before the app Main hands in, too", startupRuns == beforeOtherwise + 1);

        // ── failing loudly ──────────────────────────────────────────────────
        t.ExpectThrows("an unknown app names what does exist",
            () => BlixApps.Main(new[] { BlixApps.Selector, "nope" }, assembly: self),
            mustMention: "fixture-echo");

        t.ExpectThrows("a selector with nothing after it is an error",
            () => BlixApps.Main(new[] { BlixApps.Selector }, assembly: self),
            mustMention: BlixApps.Selector);

        // ── is what runs what the sources say? ──────────────────────────────
        FreshnessAnswersHonestly(t);
        ABuildChangesWhatANameMeans(t);
        TheExternalStarterBuildsFromCleanState(t);

        // ── a loop with no window ───────────────────────────────────────────
        HeadlessRunsTheSameLoop(t);

        t.PrintSummary();
        return t.Failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The staleness report is right about what it claims and silent about what it cannot know.
    /// </summary>
    /// <remarks>
    /// <b>An instrument that cries wolf is one people learn to read past</b>, so the false
    /// positives are tested as hard as the true ones: output under bin/, a README beside the
    /// code, a run inside the slack. Each of those would fire on every single run if it were
    /// wrong, and the warning would be worthless within a day.
    /// <para>
    /// Built on real directories rather than an abstraction over the filesystem. The question is
    /// literally about mtimes on disk, and a seam introduced to make it mockable would be a seam
    /// where the thing being tested no longer is the thing that ships.
    /// </para>
    /// </remarks>
    private static void FreshnessAnswersHonestly(TestRunner t)
    {
        var root = Path.Combine(Path.GetTempPath(), "blix-freshness-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // A two-project closure: an app that references a library, which is the shape the
            // transitive case needs and the shape every demo in this repository has.
            var appDir = Path.Combine(root, "App");
            var libDir = Path.Combine(root, "Lib");
            var outDir = Path.Combine(appDir, "bin", "Debug", "net8.0");
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(libDir);

            var appProject = Path.Combine(appDir, "App.csproj");
            var libProject = Path.Combine(libDir, "Lib.csproj");
            var assembly = Path.Combine(outDir, "App.dll");

            // Doubled separators on purpose: Blix.Test.Apps.csproj itself contains one, so a
            // resolver that only handled the tidy form would silently drop a real reference in
            // this very repository and report "current" for a project it never looked at.
            File.WriteAllText(appProject,
                "<Project><ItemGroup><ProjectReference Include=\"..\\\\Lib\\\\Lib.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(libProject, "<Project />");

            var appSource = Path.Combine(appDir, "Program.cs");
            var libSource = Path.Combine(libDir, "Thing.cs");
            var shader = Path.Combine(appDir, "Shaders", "world.vert");
            var readme = Path.Combine(appDir, "README.md");
            Directory.CreateDirectory(Path.GetDirectoryName(shader)!);
            foreach (var f in new[] { appSource, libSource, shader, readme }) File.WriteAllText(f, "x");
            File.WriteAllText(assembly, "dll");

            var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var newer = old.AddHours(1);

            void Age(string path, DateTime when) => File.SetLastWriteTimeUtc(path, when);
            void AllOld()
            {
                foreach (var f in new[] { appSource, libSource, shader, readme, appProject, libProject })
                {
                    Age(f, old);
                }

                Age(assembly, old.AddMinutes(30));
            }

            AllOld();
            t.Expect("I.0 CONTROL a build newer than every source is not reported",
                Freshness.Check(appProject, assembly) is null);

            AllOld();
            Age(appSource, newer);
            var own = Freshness.Check(appProject, assembly);
            t.Expect("I.1 a source in the app's own project is caught", own is not null);
            t.Expect("I.2 and the report names the file that moved",
                own?.Source == appSource, own?.Source ?? "(nothing)");

            AllOld();
            Age(libSource, newer);
            t.Expect("I.3 a source in a REFERENCED project is caught too",
                Freshness.Check(appProject, assembly)?.Source == libSource,
                "the engine edit behind a demo run is the whole reason this exists");

            AllOld();
            Age(shader, newer);
            t.Expect("I.4 a shader counts as a source",
                Freshness.Check(appProject, assembly)?.Source == shader);

            AllOld();
            Age(readme, newer);
            t.Expect("I.5 a README beside the code does not",
                Freshness.Check(appProject, assembly) is null);

            AllOld();
            var buildOutput = Path.Combine(outDir, "Generated.cs");
            File.WriteAllText(buildOutput, "generated");
            Age(buildOutput, newer.AddHours(2));
            t.Expect("I.6 and a .cs the BUILD wrote under bin/ does not",
                Freshness.Check(appProject, assembly) is null,
                "obj/ and bin/ hold generated sources; counting them would fire on every run");
            File.Delete(buildOutput);

            AllOld();
            Age(appSource, File.GetLastWriteTimeUtc(assembly).AddMilliseconds(500));
            t.Expect("I.7 a source inside the one-second slack is not reported",
                Freshness.Check(appProject, assembly) is null,
                "filesystem granularity, matching the index's own staleness rule");

            AllOld();
            Age(appSource, newer);
            Age(libSource, newer.AddMinutes(10));
            t.Expect("I.8 of several movers the NEWEST is named",
                Freshness.Check(appProject, assembly)?.Source == libSource);

            AllOld();
            Age(appSource, newer);
            var copied = Path.Combine(outDir, "Lib.dll");
            File.WriteAllText(copied, "dll");
            Age(copied, newer.AddMinutes(5));
            t.Expect("I.9 a dependency copied in AFTER the edit clears the report",
                Freshness.Check(appProject, assembly) is null,
                "the whole output directory is the datum, not this app's own dll -- MSBuild may " +
                "leave App.dll untouched when only Lib's method bodies changed");
            File.Delete(copied);

            // ── the build files nobody imports by hand ──────────────────────
            // The root Directory.Build.targets in this repository drives shader compilation,
            // reflection sidecars, cooked-asset staging and app-index generation. It sits ABOVE
            // every project directory, so the folder scan cannot reach it and adding .targets to
            // SourceExtensions would not have either. Pinned because it is now a contract.
            var rootTargets = Path.Combine(root, "Directory.Build.targets");
            File.WriteAllText(rootTargets, "<Project />");

            AllOld();
            Age(rootTargets, old);
            t.Expect("I.16 CONTROL an old Directory.Build.targets above the project says nothing",
                Freshness.Check(appProject, assembly) is null);

            Age(rootTargets, newer);
            t.Expect("I.17 and a newer one is caught, though it is in no project folder",
                Freshness.Check(appProject, assembly)?.Source == rootTargets);

            // Nearest wins, and only the nearest is read -- which is what MSBuild does, and the
            // reason this is worth an assertion at all. The FARTHER file is the newer one here,
            // so a walk that collected both instead of stopping would report it and pass for the
            // wrong reason.
            var nearTargets = Path.Combine(appDir, "Directory.Build.targets");
            var libTargets = Path.Combine(libDir, "Directory.Build.targets");
            File.WriteAllText(nearTargets, "<Project />");
            File.WriteAllText(libTargets, "<Project />");
            Age(nearTargets, old);
            Age(libTargets, old);
            Age(rootTargets, newer.AddHours(3));

            // BOTH projects get one, because shadowing is per project and the closure has two.
            // Written first with only the app shadowed, which failed correctly: Lib has no near
            // file, so Lib really does import the far one and MSBuild would too.
            t.Expect("I.18 a nearer Directory.Build.targets shadows a farther one",
                Freshness.Check(appProject, assembly) is null,
                "the far file is newer; finding it would mean the walk did not stop at the first");

            t.Expect("I.19 and the nearer one is what gets watched instead",
                Freshness.ImplicitImports(appDir).Contains(nearTargets)
                && !Freshness.ImplicitImports(appDir).Contains(rootTargets));

            // props and targets are two independent walks, not one. A project can take its
            // props from one level and its targets from another, and MSBuild imports both.
            var rootProps = Path.Combine(root, "Directory.Build.props");
            File.WriteAllText(rootProps, "<Project />");
            Age(rootProps, newer.AddHours(4));
            t.Expect("I.20 props is searched separately, so a far props still counts",
                Freshness.Check(appProject, assembly)?.Source == rootProps,
                "targets came from the near folder; props had to come from the far one");

            File.Delete(nearTargets);
            File.Delete(libTargets);
            File.Delete(rootTargets);
            File.Delete(rootProps);

            // ── what it must stay quiet about ───────────────────────────────
            AllOld();
            Age(appSource, newer);
            t.Expect("I.10 no project recorded is not an answer",
                Freshness.Check(null, assembly) is null);
            t.Expect("I.11 a project that is no longer there is not an answer",
                Freshness.Check(Path.Combine(root, "Gone.csproj"), assembly) is null);
            t.Expect("I.12 and an output directory with nothing in it is not an answer",
                Freshness.Check(appProject, Path.Combine(root, "Empty", "App.dll")) is null);

            // A cycle is not legal MSBuild, but this walks files a person can edit and must
            // terminate on anything it is handed rather than trust the input.
            File.WriteAllText(libProject,
                "<Project><ItemGroup><ProjectReference Include=\"..\\App\\App.csproj\" /></ItemGroup></Project>");
            var closure = Freshness.Closure(appProject);
            t.Expect("I.13 a reference cycle terminates", closure.Count == 2, $"{closure.Count} projects");

            // Malformed XML drops the REFERENCES, not the answer. The project's own folder is
            // still scanned and still reported -- this check is a courtesy on the way to running
            // something and must never be the reason a run does not happen.
            File.WriteAllText(appProject, "<Project><ItemGroup><ProjectRef");
            Age(appProject, old);  // else the rewrite itself is the newest thing here
            t.Expect("I.14 malformed XML drops its references rather than throwing",
                Freshness.Closure(appProject).Count == 1, "the unreadable project itself, alone");
            Freshness.Staleness? afterGarbage = null;
            string? thrown = null;
            try { afterGarbage = Freshness.Check(appProject, assembly); }
            catch (Exception e) { thrown = e.GetType().Name; }

            t.Expect("I.15 and the check still answers for what it CAN read",
                thrown is null && afterGarbage?.Source == appSource,
                thrown ?? afterGarbage?.Source ?? "(nothing)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// After a build, the thing launched is read from the indexes the BUILD wrote.
    /// </summary>
    /// <remarks>
    /// <b>The one case the Freshness suite above cannot reach.</b> I.0-I.15 are about comparing
    /// timestamps; this is about ORDER. `run --build` used to resolve a name, build, and then
    /// launch the App record it had read beforehand -- but a .csproj decides AssemblyName,
    /// OutputPath, TargetFramework, UseAppHost and which apps an assembly declares, which is
    /// every fact the index carries. So the successful build could hand back a fossil, which is
    /// exactly the failure the command exists to prevent, now happening after you asked for a
    /// build.
    /// <para>
    /// Built and run for real, in process, through <c>Blix.Cli.Program.Main</c>. A mock of the
    /// build step would be a mock of the only thing that makes the bug possible.
    /// </para>
    /// </remarks>
    private static void ABuildChangesWhatANameMeans(TestRunner t)
    {
        var repo = RepositoryRoot();
        if (repo is null)
        {
            // Reported, never a silent pass: a check that quietly shrinks to what it happens to
            // be able to run is a green light for the wrong reason (conventions §5).
            t.Expect("J.0 the fixture needs the repository root, to be under its build rules", false);
            return;
        }

        var dir = Path.Combine(repo, ".blixtest", "Ident");
        var project = Path.Combine(dir, "Ident.csproj");
        var source = Path.Combine(dir, "Program.cs");
        var was = Directory.GetCurrentDirectory();

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Ident</AssemblyName>
                    <UseAppHost>false</UseAppHost>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(repo, "src", "Blix.Core", "Blix.Core.csproj")}" />
                  </ItemGroup>
                </Project>
                """);

            // The declaration is renamed, not the assembly: the index is then REWRITTEN in place
            // rather than a new one appearing beside the old, which is what makes the pre-build
            // resolution succeed and the post-build one have to fail.
            // It leaves a FILE rather than printing. An app runs in its own process, so its
            // stdout goes to the inherited handle and Console.SetOut in this one never sees it --
            // measured, by first writing this test the obvious way and watching the assertion
            // read an empty string while the word it wanted scrolled past on the real console.
            // A marker on disk also makes "it did not run" observable, which a missing line in
            // captured output only pretends to be.
            void Declare(string app, string says) => File.WriteAllText(source, $$"""
                using Blix.Core;
                public static class P
                {
                    public static int Main(string[] args) => BlixApps.Main(args, _ => 0);

                    [BlixApp("{{app}}")]
                    public static int Run()
                    {
                        System.IO.File.WriteAllText(
                            System.IO.Path.Combine(System.AppContext.BaseDirectory, "ident.ran"), "{{says}}");
                        return 0;
                    }
                }
                """);

            Directory.SetCurrentDirectory(repo);

            Declare("ident-alpha", "alpha");
            if (Dotnet($"build \"{project}\" -c Debug --nologo -v:q") != 0)
            {
                t.Expect("J.0 the fixture project builds", false, "see the build output above");
                return;
            }

            var ran = Path.Combine(dir, "bin", "Debug", "net8.0", "ident.ran");
            string? Ran() => File.Exists(ran) ? File.ReadAllText(ran) : null;
            void Forget() { if (File.Exists(ran)) File.Delete(ran); }

            Forget();
            var before = Cli(out var beforeOut, "run", "ident-alpha");
            t.Expect("J.1 CONTROL the declared app runs before anything changes",
                before == 0 && Ran() == "alpha", $"exit {before}, ran {Ran() ?? "nothing"}. {beforeOut.Trim()}");

            // The rename. Nothing else moves: same project, same assembly, same output path.
            Declare("ident-beta", "beta");

            // J.3 is the assertion with teeth here, and J.2 is a true statement that does not
            // discriminate -- measured by reverting the fix and re-running: J.2 still passed.
            // Without re-resolution the old record launches the assembly the build just
            // OVERWROTE, carrying a selector that assembly no longer declares, so its own
            // dispatch refuses and nothing runs either way. What actually differs is WHO
            // diagnosed it: blix, before starting anything, or a child process failing
            // obscurely on an argument it was handed. J.2 stays because "the fossil did not
            // run" is the property being protected even when a second thing also prevents it.
            Forget();
            var gone = Cli(out var goneOut, "run", "--build", "ident-alpha");
            t.Expect("J.2 a name the build removed is NOT launched from the old index",
                gone != 0 && Ran() is null, $"exit {gone}, ran {Ran() ?? "nothing"}");
            t.Expect("J.3 and blix is what refuses, naming the build as the change",
                goneOut.Contains("the build succeeded"), goneOut.Trim());

            Forget();
            var renamed = Cli(out var renamedOut, "run", "--build", "ident-beta");
            t.Expect("J.4 the name the build CREATED is what runs",
                renamed == 0 && Ran() == "beta", $"exit {renamed}, ran {Ran() ?? "nothing"}. {renamedOut.Trim()}");

            // ── the sibling ghost ───────────────────────────────────────────
            // An index outlives its assembly, because our target writes it and MSBuild's
            // incremental clean only knows about files it recorded in FileWrites. Found by doing
            // exactly this rename with AssemblyName instead of the declaration.
            var output = Path.Combine(dir, "bin", "Debug", "net8.0");
            var assembly = Path.Combine(output, "Ident.dll");
            var parked = assembly + ".parked";
            File.Move(assembly, parked);

            var ghost = Cli(out var ghostOut, "ls");
            t.Expect("J.5 an index whose assembly has gone is not listed as an app",
                ghost == 0 && !ghostOut.Contains("ident-beta"), "a name blix ls offers must be startable");

            File.Move(parked, assembly);
            t.Expect("J.6 CONTROL and it comes back when the assembly does",
                Cli(out var backOut, "ls") == 0 && backOut.Contains("ident-beta"), backOut.Trim());
        }
        finally
        {
            Directory.SetCurrentDirectory(was);
            try { Directory.Delete(Path.Combine(repo, ".blixtest"), recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Run <see cref="BlixApps.Main"/> in process, with what it wrote to stderr.</summary>
    private static int Run(out string stderr, Assembly self, params string[] args) =>
        Run(out stderr, self, args, otherwise: null);

    private static int Run(out string stderr, Assembly self, string[] args, Func<AppArgs, int>? otherwise)
    {
        var was = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            return BlixApps.Main(args, otherwise, self);
        }
        finally
        {
            Console.SetError(was);
            stderr = captured.ToString();
        }
    }

    /// <summary>
    /// The command line is parsed once and each part is interpreted by whoever reads it.
    /// </summary>
    /// <remarks>
    /// Every case here is one the hand-written parsers in this tree got wrong at least once: a
    /// flag that swallowed the file after it, a number read in the machine's culture, a repeated
    /// option where only the first was seen, and a typo that looked accepted.
    /// </remarks>
    private static void ArgumentsAreReadNotParsed(TestRunner t)
    {
        var both = AppArgs.Parse(new[] { "--width", "640", "--height=480" });
        t.Expect("--name value and --name=value read the same",
            both.Int("width") == 640 && both.Int("height") == 480);

        var spelled = AppArgs.Parse(new[] { "--mapseed", "7", "--Drive-Root" });
        t.Expect("names match without dashes or case",
            spelled.Int("map-seed") == 7 && spelled.Flag("drive-root"));

        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma, which is the machine the RTS's
            // "8,65,710 wood" came from.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var scale = AppArgs.Parse(new[] { "--scale", "1.5" }).Float("scale");
            t.Expect("numbers are read culture-invariant", scale == 1.5f, $"read {scale}");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }

        var repeated = AppArgs.Parse(new[] { "--tint", "a=1", "--tint", "b=2" });
        t.Expect("a repeated option keeps every value", repeated.All("tint").SequenceEqual(new[] { "a=1", "b=2" }));
        t.Expect("and a single read takes the last",
            AppArgs.Parse(new[] { "--frames", "1", "--frames", "2" }).Int("frames") == 2);

        var cam = AppArgs.Parse(new[] { "--win", "1280", "720", "scene.glb" });
        t.Expect("an option can take several values",
            cam.Values("win", 2) is ["1280", "720"] && cam.Positionals is ["scene.glb"]);

        var flagThenFile = AppArgs.Parse(new[] { "--flip-v", "model.glb" });
        t.Expect("a flag does not swallow the file after it",
            flagThenFile.Flag("flip-v") && flagThenFile.Positionals is ["model.glb"]);

        var negative = AppArgs.Parse(new[] { "--offset", "-3" });
        t.Expect("a negative number is a value, not an option", negative.Int("offset") == -3);

        var terminated = AppArgs.Parse(new[] { "--out", "x", "--", "--not-a-flag" });
        t.Expect("everything after -- is positional",
            terminated.String("out") == "x" && terminated.Positionals is ["--not-a-flag"]);

        var verb = AppArgs.Parse(new[] { "batch", "list.tsv", "--quiet" });
        t.Expect("a leading verb is read as a command",
            verb.Command() == "batch" && verb.Flag("quiet") && verb.Positionals is ["list.tsv"]);

        var view = AppArgs.Parse(new[] { "--view", "shadow-map" });
        t.Expect("an enum matches without dashes or case",
            view.Enum<FixtureView>("view") == FixtureView.ShadowMap);

        var unread = AppArgs.Parse(new[] { "--years", "3", "--year", "4" });
        unread.Int("years");
        t.Expect("what nothing read is what is left", unread.Unread is ["--year", "4"]);

        t.ExpectThrows("a value that is not a number is refused, naming the flag",
            () => AppArgs.Parse(new[] { "--frames", "abc" }).Int("frames"),
            mustMention: "--frames");
        t.ExpectThrows("an option with nothing after it is refused",
            () => AppArgs.Parse(new[] { "--out" }).String("out"),
            mustMention: "--out");
    }

    /// <summary>
    /// A headless host runs an ordinary loop in the order a window does, on a clock of its own.
    /// </summary>
    /// <remarks>
    /// The loop here is written once and knows nothing about which host runs it, which is the
    /// claim: symmetry is a property of the host, not something each loop arranges.
    /// </remarks>
    private static void HeadlessRunsTheSameLoop(TestRunner t)
    {
        // The layering this arc moved the loop contract for. If either assembly ever references a
        // backend, a headless run links Vulkan again and nothing else would say so.
        var backends = new[] { "Blix.Graphics.Vulkan", "Blix.Render", "Blix.Runtime.Silk", "Silk.NET.Windowing" };
        foreach (var assembly in new[] { typeof(IGameLoop).Assembly, typeof(HeadlessHost).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            t.Expect($"{assembly.GetName().Name} references no graphics backend",
                !references.Any(backends.Contains), string.Join(", ", references));
        }
        t.Expect("the loop contract lives in Blix.Core", typeof(IGameLoop).Assembly.GetName().Name == "Blix.Core");

        var bounded = new RecordingLoop();
        new HeadlessHost(bounded, new HeadlessOptions(ExitAfterFrames: 5)).Run();
        t.Expect("a bounded run stops at the bound", bounded.Renders == 5, $"{bounded.Renders} frames");
        t.Expect("in a window's order: load, then update before render, then unload",
            bounded.Calls.First() == "load" && bounded.Calls.Last() == "unload"
            && bounded.Calls.Skip(1).Take(2).SequenceEqual(new[] { "update", "render" }),
            string.Join(" ", bounded.Calls.Take(6)));
        t.Expect("time is a fixed step, never the wall clock",
            Math.Abs(bounded.LastTime.Total - 5.0 / 60.0) < 1e-9 && Math.Abs(bounded.LastTime.Delta - 1.0 / 60.0) < 1e-12,
            $"total {bounded.LastTime.Total}, delta {bounded.LastTime.Delta}");
        t.Expect("the loop is told the size it renders at",
            bounded.Frame == new RenderFrameContext(1280, 720), bounded.Frame.ToString());

        var closing = new RecordingLoop { CloseAfter = 3 };
        new HeadlessHost(closing).Run();
        t.Expect("with no bound, the loop closes the run", closing.Renders == 3, $"{closing.Renders} frames");

        var device = new RecordingLoop { ExitAfterFirstRender = true };
        new HeadlessHost(device).Run();
        t.Expect("the device describes itself as absent",
            device.Device?.Info.Renderer.Contains("headless", StringComparison.Ordinal) == true);
        t.ExpectThrows("and refuses to make anything, naming the call",
            () => device.Device!.CreateTexture2D(default!, ReadOnlySpan<byte>.Empty),
            mustMention: "CreateTexture2D");

        var pressed = new RecordingLoop { CloseAfter = 5 };
        new HeadlessHost(pressed, input: (frame, input) =>
        {
            if (frame == 2) input.RecordKeyDown(Key.Space);
        }).Run();
        t.Expect("scripted input presses on exactly one tick, as a device's would",
            pressed.SpacePressedOn.SequenceEqual(new[] { 2 }), string.Join(",", pressed.SpacePressedOn));
        t.Expect("and is held afterwards", pressed.SpaceHeldOn.SequenceEqual(new[] { 2, 3, 4 }),
            string.Join(",", pressed.SpaceHeldOn));

        var args = AppArgs.Parse(new[] { "--frames", "4", "--width", "320", "--height", "200", "--step", "0.5", "--title", "x" });
        var options = HeadlessOptions.FromArgs(args);
        t.Expect("the shared flags mean what they mean in a window",
            options is { ExitAfterFrames: 4, Width: 320, Height: 200, Step: 0.5 }, options.ToString());
        t.Expect("and a window's own flag is left unread", args.Unread.SequenceEqual(new[] { "--title", "x" }),
            string.Join(" ", args.Unread));

        // The dump a window writes, from a run that never had one.
        var debuggable = new DebuggableLoop();
        var host = new HeadlessHost(debuggable, new HeadlessOptions(ExitAfterFrames: 6, DumpOnFrame: 4));
        var dump = Path.Combine(host.DumpDirectory!, "frame-000004.json");
        if (File.Exists(dump)) File.Delete(dump);
        var wasOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { host.Run(); } finally { Console.SetOut(wasOut); }
        t.Expect("--dump-frame writes that frame's dump", File.Exists(dump), dump);
        if (File.Exists(dump))
        {
            var root = JsonDocument.Parse(File.ReadAllText(dump)).RootElement;
            t.Expect("in the schema a window writes",
                root.GetProperty("SchemaVersion").GetInt32() == 2);
            t.Expect("carrying what the loop reported",
                root.GetRawText().Contains("ticks", StringComparison.Ordinal), "no 'ticks' value in the dump");
        }
    }

    /// <summary>
    /// The external starter is a real project, not a collection of plausible snippets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copied outside this checkout before it is built. That detail is the assertion: a project
    /// left under the Blix root would inherit this repository's props and targets automatically,
    /// hiding a broken source-consumer import behind the environment it is meant to replace.
    /// </para>
    /// <para>
    /// The headed app is only discovered here. The declared gate is deliberately headless, so the
    /// same check runs on CI machines with no GPU and still proves the whole project path: imported
    /// build rules, app indexing, project scoping, discovery, dispatch, and a child-process verdict.
    /// </para>
    /// </remarks>
    private static void TheExternalStarterBuildsFromCleanState(TestRunner t)
    {
        var repo = RepositoryRoot();
        if (repo is null)
        {
            t.Expect("K.0 the starter fixture needs the repository root", false);
            return;
        }

        var source = Path.Combine(repo, "examples", "hello-blix");
        var staged = Path.Combine(
            Path.GetTempPath(), "blix-external-starter-" + Guid.NewGuid().ToString("N")[..8]);
        var wasDirectory = Directory.GetCurrentDirectory();
        var wasBlixRoot = Environment.GetEnvironmentVariable("BLIX_ROOT");

        try
        {
            CopyTree(source, staged);
            Directory.SetCurrentDirectory(staged);
            Environment.SetEnvironmentVariable("BLIX_ROOT", repo);

            var build = Dotnet(
                "build \"src/HelloBlix/HelloBlix.csproj\" -c Debug --nologo -v:q " +
                "--disable-build-servers -p:UseSharedCompilation=false -m:1");
            t.Expect("K.0 the external starter builds from a clean staged copy", build == 0,
                $"dotnet exited {build}");
            if (build != 0) return;

            var output = Path.Combine(staged, "src", "HelloBlix", "bin", "Debug", "net8.0");
            var index = Path.Combine(output, "HelloBlix.blixapps.json");
            t.ExpectTrue("K.1 the clean build writes its own application index", File.Exists(index));

            var listed = Cli(out var listing, "ls");
            var listedLines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            t.Expect("K.2 discovery sees the headed app and the headless gate",
                listed == 0
                && listedLines.Any(line => line.TrimStart().StartsWith("hello ", StringComparison.Ordinal))
                && listedLines.Any(line => line.TrimStart().StartsWith("hello-check ", StringComparison.Ordinal))
                && !listing.Contains("HelloBlix", StringComparison.Ordinal),
                listing.Trim());

            var gate = Cli(out var gateOutput, "test");
            t.Expect("K.3 the external project's declared gate runs and passes",
                gate == 0, $"exit {gate}. {gateOutput.Trim()}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLIX_ROOT", wasBlixRoot);
            Directory.SetCurrentDirectory(wasDirectory);
            try { Directory.Delete(staged, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copy a tracked fixture without carrying any build output from its source tree.</summary>
    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "dist")) continue;

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Run the resolver in process, with everything it printed.</summary>
    private static int Cli(out string printed, params string[] args)
    {
        var captured = new StringWriter();
        var outWas = Console.Out;
        var errWas = Console.Error;

        try
        {
            Console.SetOut(captured);
            Console.SetError(captured);
            return Blix.Cli.Program.Main(args);
        }
        finally
        {
            Console.SetOut(outWas);
            Console.SetError(errWas);
            printed = captured.ToString();
        }
    }

    private static int Dotnet(string arguments)
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var muxer = root is not null && File.Exists(Path.Combine(root, "dotnet"))
            ? Path.Combine(root, "dotnet")
            : "dotnet";

        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(muxer, arguments) { UseShellExecute = false });
        process!.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The nearest folder above this assembly that carries the repository build rules.</summary>
    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.targets"))) return dir.FullName;
        }

        return null;
    }

    /// <summary>
    /// The build-time index and run-time reflection describe the same program.
    /// </summary>
    /// <remarks>
    /// The one risk generating the index accepts. It cannot drift by being forgotten —
    /// it is rewritten every build — but it could drift by the two readers disagreeing
    /// about what an attribute means, and nothing else in the layer would notice.
    /// </remarks>
    private static void IndexMatchesReflection(TestRunner t, Assembly self, BlixApps.DeclaredApp[] found)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(self.Location) + ".blixapps.json");

        if (!File.Exists(path))
        {
            t.Expect("the build wrote an index next to the assembly", false, path);
            return;
        }

        var index = JsonSerializer.Deserialize<Index>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var indexed = (index?.Apps ?? Array.Empty<IndexedApp>())
            .Select(a => (a.Name, a.Summary, a.Headed)).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
        var reflected = found
            .Select(a => (a.Name, a.Summary, a.Headed)).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();

        t.Expect("the index lists exactly what reflection finds",
            indexed.SequenceEqual(reflected),
            $"index {indexed.Length} [{string.Join(", ", indexed.Select(i => i.Name))}] vs " +
            $"reflection {reflected.Length} [{string.Join(", ", reflected.Select(r => r.Name))}]");

        t.Expect("the index marks no default where the source declares none",
            index!.Apps.Count(a => a.IsDefault) == 0,
            "no fixture here is Default, which is what lets this suite run unnamed");

        // The indexer rebuilds usage from metadata without loading the assembly; reflection builds
        // it from the loaded method. Two readers of one line, so the line is compared.
        foreach (var app in found)
        {
            var fromReflection = AppParameters.Usage(app.Method);
            var fromIndex = index.Apps.Single(a => a.Name == app.Name).Usage ?? string.Empty;
            t.Expect($"{app.Name}: the index's usage is reflection's", fromIndex == fromReflection,
                $"index '{fromIndex}' vs reflection '{fromReflection}'");
        }

        t.Expect("the index records the assembly it describes",
            index.Assembly == Path.GetFileName(self.Location));
    }

    // ── fixtures ────────────────────────────────────────────────────────────
    //
    // Four shapes, because four shapes are allowed and the reason to allow them all is
    // that the smallest app this layer promises to support is a script that reads four
    // files and exits — which should not have to accept arguments it will not read, or
    // return a code it has no opinion about.

    [BlixApp("fixture-echo", Summary = "returns how many arguments it was given")]
    private static int Echo(AppArgs args) => args.Positionals.Count;

    [BlixApp("fixture-code", Summary = "returns a specific exit code")]
    private static int Code(AppArgs args) => 7;

    [BlixApp("fixture-void", Summary = "takes nothing, returns nothing")]
    private static void Void()
    {
    }

    private sealed record Index(string Assembly, string? AppHost, bool HasEntryPoint, IndexedApp[] Apps);

    private sealed record IndexedApp(string Name, string? Summary, bool Headed, bool IsDefault, string? Usage);

    // ── recipe fixtures ─────────────────────────────────────────────────────
    // Two, because one cannot show that ids stay distinct or that two recipes can claim different
    // extensions. Neither cooks anything: what is under test is declaration and discovery, and a
    // fixture that did real work would be testing the work instead.
    [Recipe("fix1", Produces = ".fixture", Consumes = ".fixture-a;.fixture-b", Summary = "a fixture recipe")]
    public static CookOutcome FixtureRecipe(CookRequest request) =>
        CookOutcome.Written($"{request.SourcePath} -> {request.OutputPath}");

    [Recipe("fix2", Produces = ".fixture2", Consumes = ".fixture-c", Version = 9, Summary = "a second fixture recipe")]
    public static CookOutcome SecondFixtureRecipe(CookRequest request) => CookOutcome.Skipped("nothing to do");

    private static void RecipeIndexMatchesReflection(TestRunner t, Assembly self, FoundRecipe[] found)
    {
        var sidecar = Path.ChangeExtension(self.Location, null) + ".blixapps.json";
        if (!File.Exists(sidecar))
        {
            t.Fail("the recipe index exists beside the assembly", $"no {Path.GetFileName(sidecar)}");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
        if (!document.RootElement.TryGetProperty("recipes", out var listed))
        {
            t.Fail("the index carries recipes", "no 'recipes' array in the sidecar");
            return;
        }

        var indexed = listed.EnumerateArray()
            .Select(e => (Id: e.GetProperty("id").GetString(), Produces: e.GetProperty("produces").GetString()))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();

        t.Expect("the index lists exactly the recipes reflection finds",
            indexed.Length == found.Length, $"index {indexed.Length}, reflection {found.Length}");

        // Ids AND outputs, because an index that agreed on names while disagreeing on what they
        // write would send a build rule to the wrong file — which is worse than not finding one.
        t.ExpectTrue("and agrees on every id and output",
            indexed.Select(x => $"{x.Id}{x.Produces}")
                .SequenceEqual(found.Select(r => $"{r.Id}{r.Produces}")));
    }

    [BlixApp("fixture-headed", Summary = "declares that it would open a window", Headed = true)]
    private static int Headed(AppArgs args) => 0;

    [BlixApp("fixture-typed", Summary = "takes typed parameters")]
    private static int Typed(
        int years, IReadOnlyList<string> tint, int mapSeed = 7, bool fog = false,
        FixtureView view = FixtureView.Lit, float? scale = null) =>
        years + mapSeed + (fog ? 100 : 0) + (view == FixtureView.ShadowMap ? 1000 : 0)
        + (scale is { } s ? (int)(s * 10) : 0) + tint.Count * 10000;

    [BlixApp("fixture-mixed", Summary = "takes a typed parameter and the view")]
    private static int Mixed(int count, AppArgs args) => count + args.Int("extra", 0);

    // Setup every app in this assembly shares. It counts its runs and reads one lever, which is
    // the shape the RTS needed: a flag that has to be read before whichever scenario runs.
    private static int startupRuns;
    private static int startupLever;

    [BlixStartup]
    private static void Startup(AppArgs args)
    {
        startupRuns++;
        startupLever = args.Int("lever", 0);
    }

    [BlixApp("fixture-sees-startup", Summary = "returns what startup read")]
    private static int SeesStartup() => startupLever;

    [BlixApp("fixture-reads-years", Summary = "reads --years and nothing else")]
    private static int ReadsYears(AppArgs args) => args.Int("years", 1);
}

internal enum FixtureView
{
    Lit,
    ShadowMap,
}

/// <summary>An ordinary loop that writes down what happened to it.</summary>
internal class RecordingLoop : IGameLoop
{
    public List<string> Calls { get; } = new();
    public List<int> SpacePressedOn { get; } = new();
    public List<int> SpaceHeldOn { get; } = new();
    public int Renders { get; private set; }
    public int CloseAfter { get; init; }
    public bool ExitAfterFirstRender { get; init; }
    public Time LastTime { get; private set; }
    public RenderFrameContext Frame { get; private set; }
    public IGraphicsDevice? Device { get; private set; }
    private IRenderHost host = null!;
    private int updates;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        Device = graphicsDevice;
        Calls.Add("load");
    }

    public void OnUpdate(Time time)
    {
        Calls.Add("update");
        if (host.Input[Key.Space].Pressed) SpacePressedOn.Add(updates);
        if (host.Input[Key.Space].Down) SpaceHeldOn.Add(updates);
        updates++;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        Calls.Add("render");
        LastTime = time;
        Frame = frame;
        commandList.Pass(
            "clear",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new(0f, 0f, 0f, 1f) },
                ClearDepth: false),
            _ => { });
        Renders++;
        if (ExitAfterFirstRender || (CloseAfter > 0 && Renders >= CloseAfter)) host.RequestClose();
    }

    public void OnUnload() => Calls.Add("unload");
}

/// <summary>The same loop, reporting into diagnostics.</summary>
internal sealed class DebuggableLoop : RecordingLoop, IDebuggable
{
    public string DebugName => "headless-fixture";

    public void Debug(DebugContext debug) => debug.Values.Value("ticks", Renders);
}
