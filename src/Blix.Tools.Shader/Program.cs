using System.Diagnostics;
using Blix.Graphics;

if (!TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    PrintUsage();
    return 2;
}

var defines = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var definition in options.Defines)
{
    if (string.IsNullOrWhiteSpace(definition)) continue;
    var split = definition.IndexOf('=');
    var name = split < 0 ? definition : definition[..split];
    var value = split < 0 ? string.Empty : definition[(split + 1)..];
    if (string.IsNullOrWhiteSpace(name))
    {
        Console.Error.WriteLine($"Invalid empty shader define in '{definition}'.");
        return 2;
    }
    defines[name] = value;
}

GlslPreprocessResult preprocessed;
try
{
    preprocessed = ShaderLoader.PreprocessFile(options.Input, options.IncludeDirs, defines);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Shader preprocessing failed for {options.Input}: {exception.Message}");
    return 1;
}

var tempDir = Path.Combine(Path.GetTempPath(), "blix-shader", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var tempSource = Path.Combine(tempDir, Path.GetFileName(options.Input));

try
{
    File.WriteAllText(tempSource, preprocessed.ExpandedSource);
    var output = Path.GetFullPath(options.Output);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);

    var start = new ProcessStartInfo
    {
        FileName = options.Glslc,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add("-o");
    start.ArgumentList.Add(output);
    start.ArgumentList.Add(tempSource);

    using var process = Process.Start(start)
        ?? throw new InvalidOperationException($"Could not start shader compiler '{options.Glslc}'.");
    var standardOutput = process.StandardOutput.ReadToEnd();
    var standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (standardOutput.Length > 0) Console.Out.Write(standardOutput);
    if (standardError.Length > 0) Console.Error.Write(standardError);
    if (process.ExitCode == 0)
    {
        // The //@tune sidecar, from the EXPANDED source, written only once the compile succeeded.
        //
        // This is the only place that has all three things the metadata needs: the GLSL (the
        // compiled module cannot carry a comment), every include already resolved against the
        // -I paths, and the .spv's own path to sit beside. Recovering it at load instead meant
        // shipping shader sources into the application output, and still could not see a tunable
        // declared in an engine-owned include, because those are not staged.
        //
        // Always written, even when empty: a present file with no entries says "scanned, none
        // here", which an absent file cannot distinguish from "not built yet".
        File.WriteAllText(
            ShaderTunableSidecar.PathFor(output),
            ShaderTunableSidecar.ToJson(ShaderTunables.Scan(preprocessed.ExpandedSource)));

        // The //@sampler sidecar, for the same reasons and from the same source. A separate sampler
        // with no declared state is a build error here, not a device error at the first draw; the
        // .spv goes too, so the next build compiles again rather than seeing it up to date.
        IReadOnlyList<ShaderSampler> samplers;
        try
        {
            samplers = ShaderSamplers.Scan(preprocessed.ExpandedSource);
        }
        catch (InvalidOperationException failure)
        {
            Console.Error.WriteLine($"{options.Input}: error: {failure.Message}");
            File.Delete(output);
            return 1;
        }
        File.WriteAllText(ShaderSamplerSidecar.PathFor(output), ShaderSamplerSidecar.ToJson(samplers));
        return 0;
    }

    Console.Error.WriteLine("Shader source map:");
    for (var i = 0; i < preprocessed.SourceMap.Count; i++)
    {
        Console.Error.WriteLine($"  {i}: {preprocessed.SourceMap[i]}");
    }
    return process.ExitCode;
}
finally
{
    Directory.Delete(tempDir, recursive: true);
}

static bool TryParse(string[] args, out Options options, out string error)
{
    string? input = null;
    string? output = null;
    string? glslc = null;
    var includes = new List<string>();
    var defines = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var argument = args[i];
        if (argument is "-h" or "--help")
        {
            options = default!;
            error = string.Empty;
            return false;
        }

        if (i + 1 >= args.Length)
        {
            options = default!;
            error = $"Missing value after '{argument}'.";
            return false;
        }

        var value = args[++i];
        switch (argument)
        {
            case "--input": input = value; break;
            case "--output": output = value; break;
            case "--glslc": glslc = value; break;
            case "--include": includes.Add(value); break;
            case "--define": defines.Add(value); break;
            default:
                options = default!;
                error = $"Unknown option '{argument}'.";
                return false;
        }
    }

    if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(glslc))
    {
        options = default!;
        error = "--input, --output and --glslc are required.";
        return false;
    }

    options = new Options(input, output, glslc, includes, defines);
    error = string.Empty;
    return true;
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: Blix.Tools.Shader --input <shader> --output <spv> --glslc <path> " +
        "[--include <directory>]... [--define <NAME[=VALUE]>]...");
}

sealed record Options(
    string Input,
    string Output,
    string Glslc,
    IReadOnlyList<string> IncludeDirs,
    IReadOnlyList<string> Defines);
