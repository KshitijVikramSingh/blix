namespace Blix.Core;

/// <summary>
/// Marks the one static method <see cref="BlixApps.Main"/> runs before any app in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>For setup every app in the assembly shares.</b> The culture numbers print in, a lever a
/// dozen scenarios read, a log that must be open before anything writes to it. Without a place
/// for it, each app repeats it or one hand-written dispatcher runs it first, and a declared app
/// that skips that dispatcher silently runs without it. The RTS printed "8,65,710 wood" that way.
/// </para>
/// <para>
/// <b>It takes <see cref="AppArgs"/> or nothing, and returns nothing.</b> What it reads counts as
/// read. To stop the run, it throws: an <see cref="AppArgsException"/> is printed and exits 2 like
/// any other argument that cannot mean what it says. At most one per assembly, checked at build.
/// </para>
/// <para>
/// Blix sets no policy of its own here. The engine does not change the process culture; a program
/// that wants invariant output says so in its startup method.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [BlixStartup]
/// static void Startup(AppArgs args) =>
///     CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BlixStartupAttribute : Attribute;
