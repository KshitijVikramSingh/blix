namespace Blix.Core;

/// <summary>
/// Marks a static method as a Blix app — something <c>blix run</c> can find and invoke.
/// </summary>
/// <remarks>
/// <para>
/// <b>An app is any entry point built on Blix.</b> A panel on a blank screen is one. A script that
/// reads four files, prints, and exits is one. A shipped game is one with the debug turned off.
/// Nothing here distinguishes them beyond <see cref="Headed"/>, because the difference is a window
/// and not a category.
/// </para>
/// <para>
/// <b>Why a method and not a class.</b> One assembly can carry many apps. That is the property the
/// whole layer rests on: a project with 35 scenario runners in one executable declares them where
/// they already are and deletes its dispatch, while a thirty-line tool in its own csproj declares
/// one and drags nothing with it. Neither has to restructure to be reachable.
/// </para>
/// <para>
/// <b>This is a contract, which is why it lives in Blix.Core.</b> It brings no dependency anyone
/// did not already have, and it imposes nothing on how an app is written — an app is still a
/// method. Blix learns how to find it, not how it works.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [BlixApp("fightbench", Summary = "the fight bench matrix over three grounds")]
/// public static int Run(string[] args) => ...;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BlixAppAttribute : Attribute
{
    /// <param name="name">
    /// What you type after <c>blix run</c>. Lower-case, and unique within the project rather than
    /// within the assembly — two assemblies in one folder claiming one name is an error, not a
    /// race.
    /// </param>
    public BlixAppAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>What you type after <c>blix run</c>.</summary>
    public string Name { get; }

    /// <summary>One line, shown by <c>blix ls</c>. The only documentation a reader gets for free.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// True when this app opens a window.
    /// </summary>
    /// <remarks>
    /// Read by the launcher rather than by the app: a headed app needs the Vulkan loader and ICD
    /// paths in its environment, and a headless one does not care. It is not a category — a project
    /// holds both, side by side, and the only thing that changes is what has to be set up before it
    /// starts.
    /// </remarks>
    public bool Headed { get; init; }
}
