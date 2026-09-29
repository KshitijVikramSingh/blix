using System.Reflection;
using System.Runtime.CompilerServices;

namespace Blix.Core;

/// <summary>
/// The one entry point a Blix program has: parse the command line, run the app it names, and say
/// what nobody read.
/// </summary>
/// <remarks>
/// <para>
/// <b>An assembly can hold many apps.</b> Each is a <see cref="BlixAppAttribute"/> method, and
/// <c>Main</c> is one line that hands the command line here:
/// </para>
/// <code>
/// public static int Main(string[] args) => BlixApps.Main(args);
///
/// [BlixApp("hello", Default = true, Headed = true)]
/// static int Hello(AppArgs args) => ...;
///
/// [BlixApp("hello-check")]
/// static int Check() => ...;
/// </code>
/// <para>
/// <b>Which app runs.</b> The launcher names one with <see cref="Selector"/>. With none named, as
/// when a published build is started directly, the app marked <see cref="BlixAppAttribute.Default"/>
/// runs, or <paramref name="otherwise"/> when the program declares no default of its own.
/// </para>
/// <para>
/// <b>After it returns,</b> any argument nothing read is printed as one warning line. A malformed
/// one (<c>--frames abc</c>) is an <see cref="AppArgsException"/>, printed with exit code 2.
/// </para>
/// </remarks>
public static class BlixApps
{
    /// <summary>The argument the launcher passes to name which app to run.</summary>
    public const string Selector = "--blix-app";

    /// <summary>Run the app the command line names, or the default one.</summary>
    /// <param name="args">The process arguments, exactly as <c>Main</c> received them.</param>
    /// <param name="otherwise">What to run when no app is named and none is marked default.</param>
    /// <param name="assembly">Which assembly to search. Defaults to the caller's.</param>
    /// <returns>The app's exit code; 2 when an argument could not be read.</returns>
    /// <exception cref="InvalidOperationException">
    /// An app was named and there is no such app, more than one claims the name, or nothing was
    /// named and there is nothing to run. All loud: a tool that silently does nothing because it was
    /// declared slightly wrong is the worst failure this layer can have.
    /// </exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Main(string[] args, Func<AppArgs, int>? otherwise = null, Assembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        assembly ??= Assembly.GetCallingAssembly();

        var at = Array.IndexOf(args, Selector);
        if (at >= 0 && at + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{Selector} needs the name of an app after it.");
        }

        var name = at < 0 ? null : args[at + 1];
        var rest = at < 0 ? args : args.Take(at).Concat(args.Skip(at + 2)).ToArray();
        var parsed = AppArgs.Parse(rest);
        var run = Choose(name, assembly, otherwise);

        int code;
        try
        {
            code = run(parsed);
        }
        catch (AppArgsException e)
        {
            Console.Error.WriteLine($"blix: {e.Message}");
            return 2;
        }

        if (parsed.Unread is { Count: > 0 } unread)
        {
            Console.Error.WriteLine($"blix: warning: nothing read {string.Join(' ', unread)}");
        }

        return code;
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
                apps.Add(new DeclaredApp(app.Name, app.Summary, app.Headed, app.Default, method));
            }
        }

        return apps.ToArray();
    }

    private static Func<AppArgs, int> Choose(string? name, Assembly assembly, Func<AppArgs, int>? otherwise)
    {
        var found = Find(assembly);

        if (name is null)
        {
            var defaults = found.Where(a => a.Default).ToArray();
            if (defaults.Length > 1)
            {
                throw new InvalidOperationException(
                    $"{assembly.GetName().Name} marks {defaults.Length} apps as the default: " +
                    string.Join(", ", defaults.Select(d => d.Name)));
            }

            if (defaults.Length == 1) return a => Invoke(defaults[0].Method, a);
            if (otherwise is not null) return otherwise;

            throw new InvalidOperationException(
                $"No app was named and {assembly.GetName().Name} marks none as the default. " +
                $"It declares {found.Length}: {string.Join(", ", found.Select(a => a.Name).Order())}");
        }

        var matches = found.Where(a => a.Name == name).ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"No app named '{name}' in {assembly.GetName().Name}. " +
                $"It declares {found.Length}: {string.Join(", ", found.Select(a => a.Name).Order())}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"'{name}' is declared {matches.Length} times: " +
                string.Join(", ", matches.Select(m => $"{m.Method.DeclaringType?.FullName}.{m.Method.Name}")));
        }

        return a => Invoke(matches[0].Method, a);
    }

    /// <summary>
    /// The signatures an app may have, checked here and again at build time.
    /// </summary>
    /// <remarks>
    /// An app returns int or void, and takes nothing, an <see cref="AppArgs"/>, typed parameters
    /// (<see cref="AppParameters"/>), or typed parameters and one <see cref="AppArgs"/>. The smallest
    /// app this layer promises to support is a script that reads four files and exits, which should
    /// not have to accept arguments it will not read or return a code it has no opinion about.
    /// </remarks>
    private static void Validate(MethodInfo method)
    {
        var where = $"{method.DeclaringType?.FullName}.{method.Name}";
        var parameters = method.GetParameters();

        foreach (var parameter in parameters)
        {
            if (parameter.ParameterType.IsByRef || !AppParameters.Supported(parameter.ParameterType))
            {
                throw new InvalidOperationException(
                    $"[BlixApp] on {where}: parameter '{parameter.Name}' is a {parameter.ParameterType.Name}, " +
                    "which a command line cannot spell. An app takes AppArgs, int, long, float, double, " +
                    "bool, string, an enum, their nullable forms, or IReadOnlyList<string>.");
            }
        }

        if (parameters.Count(p => p.ParameterType == typeof(AppArgs)) > 1)
        {
            throw new InvalidOperationException($"[BlixApp] on {where}: an app takes at most one AppArgs.");
        }

        if (method.ReturnType != typeof(int) && method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"[BlixApp] on {where}: an app returns int or void, not {method.ReturnType.Name}.");
        }
    }

    private static int Invoke(MethodInfo method, AppArgs args)
    {
        var parameters = AppParameters.Bind(method, args);
        try
        {
            return method.Invoke(null, parameters) is int code ? code : 0;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            // Reflection wraps whatever the app threw. Unwrap it, so an AppArgsException reaches
            // Main's handler and anything else keeps its own stack.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>One app, as declared.</summary>
    public readonly record struct DeclaredApp(
        string Name, string? Summary, bool Headed, bool Default, MethodInfo Method);
}
