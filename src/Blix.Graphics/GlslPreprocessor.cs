using System.Text;
using System.Text.RegularExpressions;

namespace Blix.Graphics;

// Resolves `#include "filename"` directives in GLSL source by inlining the
// referenced content at the directive's location. Recursive: an included file
// may itself include others. The include name is passed to a caller-provided
// `readInclude` callback so this preprocessor stays file-IO-free (the caller
// owns the disk layout / asset system).
//
// Cycle detection via a visiting-set: an include nested inside its own
// inclusion chain throws InvalidOperationException. Independent re-inclusions
// of the same file from sibling sites are deduplicated only when the included
// file declares `#pragma once` somewhere in its body -- without the pragma
// the file is inlined every time it's referenced (matches C preprocessor
// behaviour and lets non-idempotent snippets work).
//
// `#line` directives are emitted around every inclusion so GLSL compile
// errors report line numbers from the original source file rather than the
// post-expansion line counter. GLSL `#line N M` sets next-line to N and
// source-string number to M; we assign 0 to the top-level source and a
// fresh integer to each unique included file. The result carries the
// resulting source-id -> name map alongside the expanded text so callers
// can decode compiler messages of the form `M:N(C): ...`.
//
// Deliberate limits:
// - Only the double-quoted `#include "name"` form is recognised. Angle-
//   bracket `#include <name>` is reserved for a future "library" path that
//   resolves against a separate search root.
// - `#pragma once` must appear as a line on its own; comments on the same
//   line are tolerated but the pragma itself isn't parsed as a token.
public static class GlslPreprocessor
{
    private static readonly Regex IncludeRegex =
        new("""^\s*#include\s+"([^"]+)"\s*$""", RegexOptions.Compiled);
    private static readonly Regex PragmaOnceRegex =
        new(@"^\s*#pragma\s+once\b", RegexOptions.Compiled);

    // Backwards-compatible simple form: returns just the expanded text. Use
    // PreprocessDetailed when you need the source-id map for error remapping.
    public static string Preprocess(string source, Func<string, string> readInclude)
        => PreprocessDetailed(source, "(main)", readInclude).ExpandedSource;

    public static GlslPreprocessResult PreprocessDetailed(
        string source,
        string sourceName,
        Func<string, string> readInclude)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(readInclude);

        var sourceMap = new List<string> { sourceName };
        var visiting = new HashSet<string>();
        var onceConsumed = new HashSet<string>();
        var output = new StringBuilder();
        ExpandInto(output, source, sourceId: 0, sourceMap, visiting, onceConsumed, readInclude);
        return new GlslPreprocessResult(output.ToString(), sourceMap);
    }

    private static void ExpandInto(
        StringBuilder output,
        string source,
        int sourceId,
        List<string> sourceMap,
        HashSet<string> visiting,
        HashSet<string> onceConsumed,
        Func<string, string> readInclude)
    {
        var lines = source.Split('\n');
        // For non-root files emit a #line directive so the included content's
        // numbering matches the original file. For the root file (sourceId 0)
        // skip it: line numbering already starts at 1, and emitting #line
        // before a leading `#version` directive trips strict GLSL drivers
        // (Apple in particular). Returns into the root after an include still
        // get a restoring #line emitted on its own line below.
        if (sourceId != 0)
        {
            output.Append("#line 1 ").Append(sourceId).Append('\n');
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var includeMatch = IncludeRegex.Match(line);
            if (!includeMatch.Success)
            {
                output.Append(line);
                if (i < lines.Length - 1) output.Append('\n');
                continue;
            }

            var name = includeMatch.Groups[1].Value;
            if (!visiting.Add(name))
            {
                throw new InvalidOperationException(
                    $"Circular #include detected for '{name}'. Currently including: " +
                    string.Join(" -> ", visiting));
            }
            try
            {
                if (onceConsumed.Contains(name))
                {
                    // Already pulled in once with #pragma once active; emit a
                    // blank line so subsequent line numbers in this source
                    // still match the original file.
                    output.Append('\n');
                    continue;
                }

                var included = readInclude(name)
                    ?? throw new InvalidOperationException(
                        $"readInclude callback returned null for '{name}'.");

                if (HasPragmaOnce(included))
                {
                    onceConsumed.Add(name);
                }

                var childId = sourceMap.Count;
                sourceMap.Add(name);
                ExpandInto(output, included, childId, sourceMap, visiting, onceConsumed, readInclude);
                // Restore parent source ID + advance to the line AFTER the
                // include directive (i + 2 = 1-indexed (i+1) plus next).
                output.Append("\n#line ").Append(i + 2).Append(' ').Append(sourceId).Append('\n');
            }
            finally
            {
                visiting.Remove(name);
            }
        }
    }

    private static bool HasPragmaOnce(string source)
    {
        foreach (var line in source.Split('\n'))
        {
            if (PragmaOnceRegex.IsMatch(line)) return true;
        }
        return false;
    }
}

public sealed record GlslPreprocessResult(
    // GLSL ready to feed to the driver. Contains #line directives delimiting
    // the original source files; compiler errors will reference these.
    string ExpandedSource,
    // Index = the source-id integer used in #line directives. SourceMap[0]
    // is the top-level source's name; subsequent indices are include names
    // in the order they were first reached.
    IReadOnlyList<string> SourceMap);
