using System.Reflection;
using System.Text;

namespace Blix.Core;

/// <summary>
/// An app's typed parameters, bound from <see cref="AppArgs"/>: sugar over the view, not a second
/// way of reading the command line.
/// </summary>
/// <remarks>
/// <para>
/// <b>A parameter is a flag with a type.</b> <c>int years = 1</c> is <c>--years</c>, read with
/// <see cref="AppArgs.Int(string)"/>, and 1 when it is not passed. <c>mapSeed</c> is
/// <c>--map-seed</c>, which also accepts <c>--mapseed</c> because names match without dashes. A
/// parameter with no default is required, and a missing one is an error naming it. An
/// <see cref="AppArgs"/> parameter is handed the view itself, for whatever the app reads
/// dynamically; the two mix freely.
/// </para>
/// <para>
/// <b>The supported types</b> are the ones a command line can spell: <c>int</c>, <c>long</c>,
/// <c>float</c>, <c>double</c>, <c>bool</c> (a flag), <c>string</c>, any enum, the nullable form of
/// the value types (null when not passed), and <c>IReadOnlyList&lt;string&gt;</c> for a repeated
/// option. The build-time indexer checks the same list, so a parameter nothing can bind is a build
/// failure rather than a tool that throws when first run.
/// </para>
/// </remarks>
public static class AppParameters
{
    /// <summary>True when an app parameter may have this type.</summary>
    public static bool Supported(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(AppArgs) || type == typeof(string) || type == typeof(IReadOnlyList<string>)) return true;

        var value = Nullable.GetUnderlyingType(type) ?? type;
        return value.IsEnum
            || value == typeof(int) || value == typeof(long)
            || value == typeof(float) || value == typeof(double)
            || value == typeof(bool);
    }

    /// <summary><c>mapSeed</c> becomes <c>map-seed</c>: the flag a parameter is read from.</summary>
    public static string FlagFor(string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        var sb = new StringBuilder();
        for (var i = 0; i < parameter.Length; i++)
        {
            var c = parameter[i];
            if (char.IsUpper(c) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Bind every parameter of <paramref name="method"/> from <paramref name="args"/>.</summary>
    /// <exception cref="AppArgsException">A required flag is missing, or a value is malformed.</exception>
    public static object?[] Bind(MethodInfo method, AppArgs args)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);

        var parameters = method.GetParameters();
        var bound = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++) bound[i] = Bind(parameters[i], args);
        return bound;
    }

    /// <summary>One line of usage, as <c>--years &lt;int&gt;=1 --fog</c>; empty for no flags.</summary>
    public static string Usage(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return string.Join(' ', method.GetParameters()
            .Where(p => p.ParameterType != typeof(AppArgs))
            .Select(p => Describe(FlagFor(p.Name!), TypeName(p.ParameterType),
                p.HasDefaultValue ? p.DefaultValue : Missing, p.ParameterType == typeof(bool))));
    }

    /// <summary>The usage entry for one flag, shared with the indexer so both print the same thing.</summary>
    /// <param name="flag">The flag, without dashes in front.</param>
    /// <param name="type">What <see cref="TypeName"/> calls its type.</param>
    /// <param name="defaultValue">Its default, or <see cref="Missing"/> when it is required.</param>
    /// <param name="isFlag">True for a bool, which takes no value.</param>
    public static string Describe(string flag, string type, object? defaultValue, bool isFlag)
    {
        if (isFlag) return $"--{flag}";
        var entry = $"--{flag} <{type}>";
        if (ReferenceEquals(defaultValue, Missing)) return entry;
        return defaultValue is null ? $"[{entry}]" : $"{entry}={Format(defaultValue)}";
    }

    /// <summary>Marks a parameter with no default, in <see cref="Describe"/>.</summary>
    public static readonly object Missing = new();

    /// <summary>The short name usage gives a parameter type: int, text, one of an enum's members.</summary>
    public static string TypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(IReadOnlyList<string>)) return "text, repeatable";
        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) return string.Join('|', System.Enum.GetNames(value).Select(FlagFor));
        return value == typeof(string) ? "text"
            : value == typeof(int) || value == typeof(long) ? "int"
            : value == typeof(bool) ? "bool"
            : "number";
    }

    private static object? Bind(ParameterInfo parameter, AppArgs args)
    {
        var type = parameter.ParameterType;
        if (type == typeof(AppArgs)) return args;

        var flag = FlagFor(parameter.Name!);
        if (type == typeof(bool))
        {
            return args.Has(flag) ? args.Flag(flag) : parameter.HasDefaultValue && parameter.DefaultValue is true;
        }

        if (type == typeof(IReadOnlyList<string>)) return args.All(flag);

        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (!args.Has(flag))
        {
            if (parameter.HasDefaultValue) return Default(parameter, value);
            if (Nullable.GetUnderlyingType(type) is not null) return null;

            throw new AppArgsException(
                $"{parameter.Member.Name} needs {Describe(flag, TypeName(type), Missing, isFlag: false)}.");
        }

        if (value == typeof(string)) return args.String(flag);
        if (value == typeof(int)) return args.Int(flag);
        if (value == typeof(long)) return args.Long(flag);
        if (value == typeof(bool)) return args.Flag(flag);
        if (value == typeof(float)) return args.Float(flag);
        if (value == typeof(double)) return args.Double(flag);
        if (value.IsEnum) return args.Enum(flag, value);

        throw new InvalidOperationException($"{type.Name} is not a type an app parameter can be.");
    }

    // A default for an enum parameter can arrive as its underlying integer.
    private static object? Default(ParameterInfo parameter, Type value) =>
        value.IsEnum && parameter.DefaultValue is not null && parameter.DefaultValue.GetType() != value
            ? System.Enum.ToObject(value, parameter.DefaultValue)
            : parameter.DefaultValue;

    private static string Format(object value) => value switch
    {
        string s => $"\"{s}\"",
        System.Enum e => FlagFor(e.ToString()),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
