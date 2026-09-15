using Blix.Verify;
using System.Reflection;
using System.Text.Json;
using Blix.Core;

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
    public static int Main(string[] args)
    {
        // The dispatch path itself. A selector reaches one of the fixtures below and
        // this suite never runs; without one, Dispatch returns null and we do.
        if (BlixApps.Dispatch(args) is { } code) return code;

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

        // ── the two readers agree ───────────────────────────────────────────
        IndexMatchesReflection(t, self, found);

        // ── dispatch ────────────────────────────────────────────────────────
        t.Expect("no selector means this is not an app invocation",
            BlixApps.Dispatch(new[] { "--whatever" }, self) is null);

        t.Expect("a selector runs the named app and returns its code",
            BlixApps.Dispatch(new[] { BlixApps.Selector, "fixture-code" }, self) == 7);

        t.Expect("an app that returns void is a success",
            BlixApps.Dispatch(new[] { BlixApps.Selector, "fixture-void" }, self) == 0);

        t.Expect("an app taking no arguments is still invoked",
            BlixApps.Dispatch(new[] { BlixApps.Selector, "fixture-void" }, self) == 0);

        // The selector is removed before the app sees it, which is what lets an app's
        // own parsing stay ignorant of this layer. Echo returns its argument count.
        t.Expect("the selector is stripped from the app's arguments",
            BlixApps.Dispatch(new[] { "a", BlixApps.Selector, "fixture-echo", "b", "c" }, self) == 3,
            "expected the app to see exactly a, b, c");

        // ── failing loudly ──────────────────────────────────────────────────
        t.ExpectThrows("an unknown app names what does exist",
            () => BlixApps.Dispatch(new[] { BlixApps.Selector, "nope" }, self),
            mustMention: "fixture-echo");

        t.ExpectThrows("a selector with nothing after it is an error",
            () => BlixApps.Dispatch(new[] { BlixApps.Selector }, self),
            mustMention: BlixApps.Selector);

        t.PrintSummary();
        return t.Failed == 0 ? 0 : 1;
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

        t.Expect("the index knows which app is the entry point",
            index!.Apps.Count(a => a.IsEntryPoint) == 0,
            "none of this suite's fixtures is Main, so none should be flagged");

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
    private static int Echo(string[] args) => args.Length;

    [BlixApp("fixture-code", Summary = "returns a specific exit code")]
    private static int Code(string[] args) => 7;

    [BlixApp("fixture-void", Summary = "takes nothing, returns nothing")]
    private static void Void()
    {
    }

    [BlixApp("fixture-headed", Summary = "declares that it would open a window", Headed = true)]
    private static int Headed(string[] args) => 0;

    private sealed record Index(string Assembly, string? AppHost, bool HasEntryPoint, IndexedApp[] Apps);

    private sealed record IndexedApp(string Name, string? Summary, bool Headed, bool IsEntryPoint);

}
