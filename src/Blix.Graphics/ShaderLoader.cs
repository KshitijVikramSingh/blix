namespace Blix.Graphics;

// Convenience loader that bundles disk read + #include resolution + ShaderSources
// construction. Most callers want exactly this combination; using it ensures the
// source map for each stage is wired through to the OpenGL compile-error
// formatter (see ShaderSource.SourceMap / BuildCompileError).
//
// Resolution order for `#include "name"`:
//   1. Adjacent to the requesting file's directory.
//   2. Each entry of `includeDirs`, in order.
//
// This intentionally mirrors C's quoted-include semantics: relative-first,
// then a search-path fallback. Angle-bracket includes are not yet supported.
public static class ShaderLoader
{
    public static ShaderSources LoadVertexFragment(
        string vertexPath,
        string fragmentPath,
        IReadOnlyList<string>? includeDirs = null,
        // Defines injected before the source's first non-version line.
        // Same for both stages; pass per-stage by calling LoadOne twice if
        // the stages need different feature sets. Value can be empty for a
        // bare `#define NAME`.
        IReadOnlyDictionary<string, string>? defines = null)
    {
        var dirs = includeDirs ?? Array.Empty<string>();
        var vert = LoadOne(vertexPath, dirs, defines);
        var frag = LoadOne(fragmentPath, dirs, defines);
        return new ShaderSources(
            VertexShader: vert.ExpandedSource,
            FragmentShader: frag.ExpandedSource,
            VertexName: Path.GetFileName(vertexPath),
            FragmentName: Path.GetFileName(fragmentPath),
            VertexSourceMap: vert.SourceMap,
            FragmentSourceMap: frag.SourceMap);
    }

    private static GlslPreprocessResult LoadOne(
        string path,
        IReadOnlyList<string> includeDirs,
        IReadOnlyDictionary<string, string>? defines)
    {
        var source = File.ReadAllText(path);
        if (defines is { Count: > 0 })
        {
            source = InjectDefines(source, defines);
        }
        var ownDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        return GlslPreprocessor.PreprocessDetailed(
            source,
            Path.GetFileName(path),
            name => ResolveInclude(name, ownDir, includeDirs));
    }

    private static string InjectDefines(string source, IReadOnlyDictionary<string, string> defines)
    {
        // GLSL forbids any directive before `#version`, so we splice the
        // defines AFTER the version line when one exists. When the source
        // omits #version (rare; helper shader files maybe), prepend.
        var lines = source.Split('\n');
        var insertAt = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("#version", StringComparison.Ordinal))
            {
                insertAt = i + 1;
                break;
            }
        }

        var defineLines = new List<string>(defines.Count);
        foreach (var kv in defines)
        {
            defineLines.Add(string.IsNullOrEmpty(kv.Value)
                ? $"#define {kv.Key}"
                : $"#define {kv.Key} {kv.Value}");
        }

        if (insertAt < 0)
        {
            return string.Join('\n', defineLines) + '\n' + source;
        }
        var head = lines.Take(insertAt);
        var tail = lines.Skip(insertAt);
        return string.Join('\n', head.Concat(defineLines).Concat(tail));
    }

    private static string ResolveInclude(
        string name,
        string requestingDir,
        IReadOnlyList<string> includeDirs)
    {
        var local = Path.Combine(requestingDir, name);
        if (File.Exists(local)) return File.ReadAllText(local);
        foreach (var dir in includeDirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Shader include '{name}' not found. Looked next to the requesting file ({requestingDir}) " +
            $"and in {includeDirs.Count} include dir(s): {string.Join(", ", includeDirs)}");
    }
}
