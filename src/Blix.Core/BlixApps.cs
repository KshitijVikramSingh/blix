using System.Reflection;

namespace Blix.Core;

/// <summary>
/// In-assembly dispatch: find the <see cref="BlixAppAttribute"/> the launcher asked for and run it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole point is that an assembly can hold many apps.</b> An executable carrying thirty-five
/// scenario runners calls this from <c>Main</c> and deletes its own dispatch — one scenario at a
/// time, because an undeclared one simply falls through and the hand-written chain keeps handling
/// it.
/// </para>
/// <code>
/// static int Main(string[] args) => BlixApps.Dispatch(args) ?? RunTheApplication(args);
/// </code>
/// <para>
/// <b>It returns null rather than exiting</b> when no app was asked for, which is what makes the
/// migration incremental. A caller that has nothing else to do can write <c>?? 0</c>; one that is
/// also an application runs it.
/// </para>
/// </remarks>
public static class BlixApps
{
    /// <summary>The argument the launcher passes to name which app to run.</summary>
    public const string Selector = "--blix-app";

    /// <summary>
    /// Run the app named by <c>--blix-app &lt;name&gt;</c> in <paramref name="args"/>, if there is one.
    /// </summary>
    /// <param name="args">The process arguments. The selector and its value are removed before the
    /// app sees them, so an app's own parsing never has to know this layer exists.</param>
    /// <param name="assembly">Which assembly to search. Defaults to the caller's.</param>
    /// <returns>The app's exit code, or null when no app was named.</returns>
    /// <exception cref="InvalidOperationException">
    /// An app was named and there is no such app, or more than one claims the name. Both are loud:
    /// a tool that silently does nothing because it was declared slightly wrong is the worst
    /// failure this layer can have.
    /// </exception>
    public static int? Dispatch(string[] args, Assembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var at = Array.IndexOf(args, Selector);
        if (at < 0) return null;
        if (at + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{Selector} needs the name of an app after it.");
        }

        var name = args[at + 1];
        var rest = args.Take(at).Concat(args.Skip(at + 2)).ToArray();
        var found = Find(assembly ?? Assembly.GetCallingAssembly());

        var matches = found.Where(a => a.Name == name).ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"No app named '{name}' in {(assembly ?? Assembly.GetCallingAssembly()).GetName().Name}. " +
                $"It declares {found.Length}: {string.Join(", ", found.Select(a => a.Name).Order())}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"'{name}' is declared {matches.Length} times: " +
                string.Join(", ", matches.Select(m => $"{m.Method.DeclaringType?.FullName}.{m.Method.Name}")));
        }

        return Invoke(matches[0].Method, rest);
    }

    /// <summary>Every app this assembly declares — the same set the build-time index reports.</summary>
    /// <remarks>
    /// Two readers of one truth, which is the risk this layer accepts: the index is written by
    /// reading metadata at build time, and this reads the loaded assembly at run time. They agree
    /// because they are reading the same attributes, and the probe checks that they do.
    /// </remarks>
    public static DeclaredApp[] Find(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var apps = new List<DeclaredApp>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<BlixAppAttribute>() is not { } app) continue;
                Validate(method);
                apps.Add(new DeclaredApp(app.Name, app.Summary, app.Headed, method));
            }
        }

        return apps.ToArray();
    }

    /// <summary>
    /// The signatures an app may have, checked here and again at build time.
    /// </summary>
    /// <remarks>
    /// Four shapes, and the reason to allow all four is that the smallest app this layer promises to
    /// support is a script that reads four files and exits — which should not have to accept
    /// arguments it will not read, or return a code it has no opinion about.
    /// </remarks>
    private static void Validate(MethodInfo method)
    {
        var where = $"{method.DeclaringType?.FullName}.{method.Name}";
        var parameters = method.GetParameters();

        var argsOk = parameters.Length == 0
            || (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]));
        if (!argsOk)
        {
            throw new InvalidOperationException(
                $"[BlixApp] on {where}: an app takes string[] or nothing, not " +
                $"({string.Join(", ", parameters.Select(p => p.ParameterType.Name))}).");
        }

        if (method.ReturnType != typeof(int) && method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"[BlixApp] on {where}: an app returns int or void, not {method.ReturnType.Name}.");
        }
    }

    private static int Invoke(MethodInfo method, string[] args)
    {
        var parameters = method.GetParameters().Length == 0 ? null : new object[] { args };
        var result = method.Invoke(null, parameters);
        return result is int code ? code : 0;
    }

    /// <summary>One app, as declared.</summary>
    public readonly record struct DeclaredApp(string Name, string? Summary, bool Headed, MethodInfo Method);
}
