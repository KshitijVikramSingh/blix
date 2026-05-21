using System.Text.RegularExpressions;

namespace Blix.Graphics;

// Resolves `#include "filename"` directives in GLSL source by inlining the
// referenced content at the directive's location. Recursive: an included file may
// itself include others. The include name is passed to a caller-provided
// `readInclude` callback so this preprocessor stays file-IO-free (the caller owns
// the disk layout / asset system).
//
// Cycle detection via a visiting-set: an include nested inside its own inclusion
// chain throws InvalidOperationException. Independent re-inclusions of the same
// file from sibling sites are fine.
//
// Deliberate limits:
// - No `#line` directives emitted at boundaries; compile errors after inlining
//   point at the wrong line numbers. Acceptable for the engine's scale; revisit
//   if shader errors become hard to localise.
// - Only the double-quoted `#include "name"` form is recognised. Angle-bracket
//   form (`#include <name>`) ignored.
// - Tokens *inside* the include name aren't validated against anything; the
//   readInclude callback is expected to handle / report unresolved names.
public static class GlslPreprocessor
{
    private static readonly Regex IncludeRegex =
        new("""^\s*#include\s+"([^"]+)"\s*$""", RegexOptions.Multiline);

    public static string Preprocess(string source, Func<string, string> readInclude)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(readInclude);
        var visiting = new HashSet<string>();
        return PreprocessCore(source, readInclude, visiting);
    }

    private static string PreprocessCore(string source, Func<string, string> readInclude, HashSet<string> visiting)
    {
        return IncludeRegex.Replace(source, match =>
        {
            var name = match.Groups[1].Value;
            if (!visiting.Add(name))
            {
                throw new InvalidOperationException(
                    $"Circular #include detected for '{name}'. Currently including: {string.Join(" -> ", visiting)}");
            }
            try
            {
                var included = readInclude(name)
                    ?? throw new InvalidOperationException($"readInclude callback returned null for '{name}'.");
                return PreprocessCore(included, readInclude, visiting);
            }
            finally
            {
                visiting.Remove(name);
            }
        });
    }
}
