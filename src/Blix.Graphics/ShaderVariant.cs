namespace Blix.Graphics;

// Selects a compiled shader variant by tag. Shaders are offline-compiled SPIR-V
// (no runtime glslc), so variants are distinct .spv files produced at cook time by
// `glslc -DTAG` (see each demo csproj's CompileSpirV target). The runtime picks one
// by a filename-suffix convention: the base variant is `world.frag.spv`, a tagged
// variant is `world.FOG.frag.spv`. This is a pure path builder — no device or I/O —
// so it lives in Blix.Graphics and is unit-testable.
public readonly record struct ShaderVariantKey(string? Tag)
{
    // The base (un-tagged) variant: the .spv compiled with no extra defines.
    public static ShaderVariantKey Base => new((string?)null);

    // Filename infix: "" for base, ".TAG" otherwise.
    public string Suffix => string.IsNullOrEmpty(Tag) ? string.Empty : "." + Tag;
}

public static class ShaderVariantPath
{
    // shaderDir/<baseName><suffix><stageExt>.spv — e.g. ("Shaders","world",".frag",FOG)
    // -> "Shaders/world.FOG.frag.spv".
    public static string Spv(string shaderDir, string baseName, string stageExt, ShaderVariantKey variant)
    {
        ArgumentNullException.ThrowIfNull(shaderDir);
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        ArgumentNullException.ThrowIfNull(stageExt);
        return Path.Combine(shaderDir, $"{baseName}{variant.Suffix}{stageExt}.spv");
    }

    // The spirv-cross reflection sidecar next to the .spv (for reflection-driven
    // demos; the runner hand-declares its interfaces and doesn't need this).
    public static string Refl(string shaderDir, string baseName, string stageExt, ShaderVariantKey variant)
        => Spv(shaderDir, baseName, stageExt, variant) + ".refl.json";
}
