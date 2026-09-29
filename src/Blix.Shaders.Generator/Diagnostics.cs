using Microsoft.CodeAnalysis;

namespace Blix.Shaders.Generator;

// Every message names the declaration, the shader member and what to do. A build error here is
// the whole interface between the two languages, so it has to be readable cold.
internal static class Diagnostics
{
    private const string Category = "Blix.Shaders";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "BLX1001", "Push-constant struct is not partial",
        "[PushConstants] on {0}: the struct must be partial, so its fields can be written from the shader",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor HasFields = new(
        "BLX1002", "Push-constant struct declares its own fields",
        "{0} declares the field '{1}'. Its fields come from the shader, so declare it with none.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoReflection = new(
        "BLX1003", "No reflection for a listed stage",
        "{0}: no reflection for '{1}'. It must be a GlslShader in this project, built with BlixShaderReflect=true.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoBlock = new(
        "BLX1004", "No push-constant block",
        "{0}: none of {1} declares a push-constant block",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Disagree = new(
        "BLX1005", "Stages disagree about the push block",
        "{0}: the push block is '{1}' ({2} at byte {3}) in {4} but '{5}' ({6} at byte {7}) in {8}. Stages sharing a block must declare it identically; an include keeps them one declaration.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Unsupported = new(
        "BLX1006", "Push-constant member has no C# type",
        "{0}: '{1}' is a {2}, which has no C# type here. Supported: float, int, uint, bool, vec2, vec3, vec4, mat4.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Array = new(
        "BLX1007", "Push-constant member is an array",
        "{0}: '{1}' in {2} is an array, and arrays in push blocks are not supported yet",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Collision = new(
        "BLX1008", "Two members get one C# name",
        "{0}: '{1}' and '{2}' both become '{3}' under the rule in force ({4}). Add [ShaderName(\"{1}\", \"...\")] for one of them.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnknownOverride = new(
        "BLX1009", "ShaderName names no member",
        "{0}: [ShaderName(\"{1}\", ...)] names no member of the push block. It has: {2}.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoStages = new(
        "BLX1010", "No stages listed",
        "[PushConstants] on {0} lists no shader. Name the program's stages: [PushConstants(\"a.vert\", \"a.frag\")].",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EnumNoShader = new(
        "BLX1101", "No shader sidecar for a shader enum",
        "[ShaderEnum] on {0}: '{1}' is not a GlslShader in this project, so there is nothing to check it against",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EnumNoDeclaration = new(
        "BLX1102", "Shader member declares no names",
        "{0}: '{1}' in {2} has no //@tune enum{{ ... }} above it, so the shader never says what its values mean. Add //@tune enum{{ {3} }} above '{1}'.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EnumMismatch = new(
        "BLX1103", "Enum and shader disagree",
        "{0} does not match '{1}' in {2}. C# has {3}; the shader has {4}. The shader compares against positions, so the names must be the same, in the same order; case, spaces, dashes and underscores are ignored.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EnumNotSequential = new(
        "BLX1104", "Enum values are not positions",
        "{0}.{1} is {2} where the shader reads position {3}. A shader enum's members must be 0, 1, 2 and so on, in order.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadReflection = new(
        "BLX1011", "Reflection could not be read",
        "{0}: the reflection for '{1}' could not be read: {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
