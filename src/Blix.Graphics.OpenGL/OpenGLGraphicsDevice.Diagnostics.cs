using System.Text;
using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    private static int CreateShaderProgramFromDescription(ShaderProgramDescription description)
    {
        var vertex = GetRequiredShaderSource(description, ShaderStage.Vertex);
        var fragment = GetRequiredShaderSource(description, ShaderStage.Fragment);
        return LinkProgram(vertex, fragment);
    }

    private static int LinkProgram(ShaderSource vertex, ShaderSource fragment)
    {
        var vertexShader = CompileShader(vertex);
        var fragmentShader = CompileShader(fragment);
        var program = GL.CreateProgram();

        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkStatus);

        GL.DetachShader(program, vertexShader);
        GL.DetachShader(program, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        if (linkStatus == 0)
        {
            var infoLog = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            throw new InvalidOperationException(BuildLinkError(vertex, fragment, infoLog));
        }

        return program;
    }

    private static int CompileShader(ShaderSource source)
    {
        var shader = GL.CreateShader(MapShaderStage(source.Stage));
        GL.ShaderSource(shader, source.Source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compileStatus);

        if (compileStatus == 0)
        {
            var infoLog = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException(BuildCompileError(source, infoLog));
        }

        return shader;
    }

    private static string BuildCompileError(ShaderSource source, string infoLog)
    {
        var label = FormatShaderLabel(source);
        var numberedSource = BuildLineNumberedSource(source.Source);

        var builder = new StringBuilder();
        builder.Append(label);
        builder.AppendLine(" compilation failed:");

        if (!string.IsNullOrWhiteSpace(infoLog))
        {
            builder.AppendLine(infoLog.TrimEnd());
        }

        builder.AppendLine("--- Source ---");
        builder.Append(numberedSource);
        return builder.ToString();
    }

    private static string BuildLinkError(ShaderSource vertex, ShaderSource fragment, string infoLog)
    {
        var builder = new StringBuilder();
        builder.Append("Shader program link failed (");
        builder.Append(FormatShaderLabel(vertex));
        builder.Append(", ");
        builder.Append(FormatShaderLabel(fragment));
        builder.AppendLine("):");

        if (!string.IsNullOrWhiteSpace(infoLog))
        {
            builder.AppendLine(infoLog.TrimEnd());
        }

        return builder.ToString();
    }

    private static string FormatShaderLabel(ShaderSource source)
    {
        return string.IsNullOrWhiteSpace(source.Name)
            ? source.Stage.ToString()
            : $"{source.Stage} '{source.Name}'";
    }

    private static string BuildLineNumberedSource(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var width = lines.Length.ToString().Length;
        var builder = new StringBuilder(source.Length + lines.Length * (width + 4));

        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append((i + 1).ToString().PadLeft(width));
            builder.Append(" | ");
            builder.AppendLine(lines[i]);
        }

        return builder.ToString();
    }

    private static ShaderSource GetRequiredShaderSource(ShaderProgramDescription description, ShaderStage stage)
    {
        var matches = description.Sources.Where(source => source.Stage == stage).ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"Shader program description is missing a {stage} shader source.", nameof(description)),
            _ => throw new ArgumentException($"Shader program description has multiple {stage} shader sources.", nameof(description))
        };
    }
}
