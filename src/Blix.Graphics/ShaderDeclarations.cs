namespace Blix.Graphics;

/// <summary>
/// Marks a <c>partial struct</c> as the C# face of a push-constant block. The build fills in its
/// fields from the shaders' reflection, at the offsets the shaders read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shader is the one list of fields; the struct is yours.</b> You name the type and say
/// which program it describes; <c>Blix.Shaders.Generator</c> writes every field with its
/// <c>[FieldOffset]</c> from the <c>.spv.refl.json</c> the shader build produced, padding
/// included, so the bytes are what the GPU reads. A block that disagrees between the listed stages,
/// a type C# cannot hold, or a stage with no reflection is a build error that says which.
/// </para>
/// <para>
/// <b>Names are exact unless this declaration says otherwise.</b> A member <c>uWorld</c> is the
/// field <c>uWorld</c>. <see cref="Prefix"/> removes a prefix where a member has it, and
/// <see cref="ShaderNameAttribute"/> renames one member. Nothing is renamed by an engine
/// convention: the rule in force is always on the line you are reading.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [PushConstants("world.vert", "world.frag", Prefix = "u")]
/// partial struct WorldPush;
///
/// var push = new WorldPush { World = model, Tint = tint };
/// pass.DrawIndexed(..., pushConstants: push.ToBytes());
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class PushConstantsAttribute : Attribute
{
    /// <param name="stages">The program's shader files, as the project lists them: <c>world.vert</c>.</param>
    public PushConstantsAttribute(params string[] stages) => Stages = stages;

    /// <summary>The program's shader files.</summary>
    public string[] Stages { get; }

    /// <summary>Removed from the start of each member's name where it is present: <c>u</c> makes <c>uWorld</c> <c>World</c>.</summary>
    public string? Prefix { get; init; }
}

/// <summary>
/// Gives one shader member a C# name of its own, on a type whose other names follow the rule in force.
/// </summary>
/// <example><c>[ShaderName("uMVP", "ModelViewProjection")]</c></example>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class ShaderNameAttribute : Attribute
{
    /// <param name="shaderName">The name in the shader.</param>
    /// <param name="name">The name in C#.</param>
    public ShaderNameAttribute(string shaderName, string name)
    {
        ShaderName = shaderName;
        Name = name;
    }

    /// <summary>The name in the shader.</summary>
    public string ShaderName { get; }

    /// <summary>The name in C#.</summary>
    public string Name { get; }
}

/// <summary>
/// Marks a C# enum as the names of a shader value that picks one of several branches, checked at
/// build against the <c>//@tune enum{ … }</c> declared above that member in the shader.
/// </summary>
/// <remarks>
/// <para>
/// <b>A shader ladder compares against positions</b> (<c>if (uTonemap == 2u)</c>), so the C# enum
/// and the shader agree only if their names are in the same order. Written twice with nothing
/// between them, they drift silently: the overlay says AgX and the picture is Hejl. This checks
/// the two declarations against each other and fails the build, naming both lists, when they differ.
/// </para>
/// <para>
/// Names match ignoring case, spaces, dashes and underscores, the same rule command-line flags and
/// enum options follow everywhere in Blix, so <c>BentNormal</c> is <c>Bent normal</c>. Members
/// must be 0, 1, 2 and so on, in order, because those are the numbers the shader compares with.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // present.frag:   //@tune enum{ Reinhard, ACES, AgX, Hejl }
/// //                 uint uTonemap;
/// [ShaderEnum("present.frag", "uTonemap")]
/// enum TonemapMode { Reinhard, ACES, AgX, Hejl }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class ShaderEnumAttribute : Attribute
{
    /// <param name="stage">The shader file that declares the member, as the project lists it.</param>
    /// <param name="member">The member the <c>//@tune enum{ … }</c> sits above.</param>
    public ShaderEnumAttribute(string stage, string member)
    {
        Stage = stage;
        Member = member;
    }

    /// <summary>The shader file that declares the member.</summary>
    public string Stage { get; }

    /// <summary>The member the enum's names belong to.</summary>
    public string Member { get; }
}
