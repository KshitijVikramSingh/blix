namespace Blix.Graphics;

public sealed record ShaderSources(
    string VertexShader,
    string FragmentShader,
    string? VertexName = null,
    string? FragmentName = null,
    IReadOnlyList<string>? VertexSourceMap = null,
    IReadOnlyList<string>? FragmentSourceMap = null);

public sealed record ShaderProgramDescription(IReadOnlyList<ShaderSource> Sources)
{
    public static ShaderProgramDescription FromVertexFragment(
        string vertexShader,
        string fragmentShader,
        string? vertexName = null,
        string? fragmentName = null,
        IReadOnlyList<string>? vertexSourceMap = null,
        IReadOnlyList<string>? fragmentSourceMap = null)
    {
        return new ShaderProgramDescription(
        [
            new ShaderSource(ShaderStage.Vertex, vertexShader, vertexName, vertexSourceMap),
            new ShaderSource(ShaderStage.Fragment, fragmentShader, fragmentName, fragmentSourceMap)
        ]);
    }
}

public sealed record ShaderSource(
    ShaderStage Stage,
    string Source,
    string? Name = null,
    // Source-id -> filename map produced by GlslPreprocessor's #line emission.
    // When set, the compile-error formatter prints this alongside the
    // info log so messages like "ERROR: 1:42: ..." resolve to a real file.
    IReadOnlyList<string>? SourceMap = null);

public enum ShaderStage
{
    Vertex = 0,
    Fragment
}
