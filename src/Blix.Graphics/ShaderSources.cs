namespace Blix.Graphics;

public sealed record ShaderSources(
    string VertexShader,
    string FragmentShader,
    string? VertexName = null,
    string? FragmentName = null);

public sealed record ShaderProgramDescription(IReadOnlyList<ShaderSource> Sources)
{
    public static ShaderProgramDescription FromVertexFragment(
        string vertexShader,
        string fragmentShader,
        string? vertexName = null,
        string? fragmentName = null)
    {
        return new ShaderProgramDescription(
        [
            new ShaderSource(ShaderStage.Vertex, vertexShader, vertexName),
            new ShaderSource(ShaderStage.Fragment, fragmentShader, fragmentName)
        ]);
    }
}

public sealed record ShaderSource(ShaderStage Stage, string Source, string? Name = null);

public enum ShaderStage
{
    Vertex = 0,
    Fragment
}
