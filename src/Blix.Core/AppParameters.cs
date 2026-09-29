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
/// <see cref="AppArgs.Int(string)"/>. <c>mapSeed</c> is <c>--map-seed</c>, which also accepts
/// <c>--mapseed</c> because names match without dashes. An <see cref="AppArgs"/> parameter is handed
/// the view itself, for whatever the app reads dynamically; the two mix freely.
/// </para>
/// <para>
/// <b>When a flag is absent</b>, one rule for every type:
/// </para>
/// <list type="bullet">
/// <item>A declared default is used, whatever it is, <c>null</c> included.</item>
/// <item>With no default, a nullable type is <c>null</c>, and a <c>bool</c> is false.</item>
/// <item>Anything else is required: a missing one exits 2, naming the flag and its type. That
/// includes <c>IReadOnlyList&lt;string&gt;</c>, which then needs at least one occurrence.</item>
/// </list>
/// <para>
/// <b>A <c>bool</c> is a flag</b>: <c>--fog</c> is true and <c>--fog=false</c> is false. Usage shows
/// the form worth typing, so a flag that defaults to true is shown as <c>--fog=false</c>, and a
/// <c>bool?</c>, which is null when absent, as <c>[--fog[=false]]</c>.
/// </para>
/// <para>
/// <b>The supported types</b> are the ones a command line can spell: <c>int</c>, <c>long</c>,
/// <c>float</c>, <c>double</c>, <c>bool</c>, <c>string</c>, any enum, the nullable form of the value
/// types, and <c>IReadOnlyList&lt;string&gt;</c> for a repeated option. The build-time indexer checks
/// the same list and writes the same usage line, so a parameter nothing can bind is a build failure.
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
            .Select(p => Describe(
                FlagFor(p.Name!),
                TypeName(p.ParameterType),
                KindOf(p.ParameterType),
                p.HasDefaultValue,
                p.HasDefaultValue ? DefaultOf(p) : null)));
    }

    /// <summary>What a parameter's type means on a command line.</summary>
    public enum Kind
    {
        /// <summary>A value that is required unless it has a default.</summary>
        Value,

        /// <summary>A nullable value: null when absent and undeclared.</summary>
        NullableValue,

        /// <summary>A <c>bool</c>: a flag, false when absent and undeclared.</summary>
        Flag,

        /// <summary>A <c>bool?</c>: a flag that is null when absent.</summary>
        NullableFlag,

        /// <summary>An <c>IReadOnlyList&lt;string&gt;</c>: every occurrence of a repeated option.</summary>
        Repeated,
    }

    /// <summary>The kind of <paramref name="type"/>.</summary>
    public static Kind KindOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(bool)) return Kind.Flag;
        if (type == typeof(bool?)) return Kind.NullableFlag;
        if (type == typeof(IReadOnlyList<string>)) return Kind.Repeated;
        return Nullable.GetUnderlyingType(type) is not null ? Kind.NullableValue : Kind.Value;
    }

    /// <summary>
    /// The usage entry for one flag. The indexer rebuilds the same entry from metadata, so this is
    /// the one statement of the rules above as they are shown.
    /// </summary>
    /// <param name="flag">The flag, without dashes in front.</param>
    /// <param name="type">What <see cref="TypeName"/> calls its type.</param>
    /// <param name="kind">Its <see cref="Kind"/>.</param>
    /// <param name="hasDefault">Whether the parameter declares a default.</param>
    /// <param name="defaultValue">That default, when it does.</param>
    public static string Describe(string flag, string type, Kind kind, bool hasDefault, object? defaultValue)
    {
        var entry = $"--{flag} <{type}>";
        return kind switch
        {
            Kind.Flag => hasDefault && defaultValue is true ? $"--{flag}=false" : $"--{flag}",
            Kind.NullableFlag => $"[--{flag}[=false]]",
            Kind.Repeated => hasDefault ? $"[{entry}]" : entry,
            _ when hasDefault && defaultValue is not null => $"{entry}={Format(defaultValue)}",
            _ when hasDefault || kind == Kind.NullableValue => $"[{entry}]",
            _ => entry,
        };
    }

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
        var kind = KindOf(type);

        if (!args.Has(flag))
        {
            if (parameter.HasDefaultValue) return DefaultOf(parameter);
            return kind switch
            {
                Kind.Flag => false,
                Kind.NullableFlag or Kind.NullableValue => null,
                _ => throw new AppArgsException(
                    $"{parameter.Member.Name} needs {Describe(flag, TypeName(type), kind, hasDefault: false, null)}."),
            };
        }

        if (kind is Kind.Flag or Kind.NullableFlag) return args.Flag(flag);
        if (kind == Kind.Repeated) return args.All(flag);

        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value == typeof(string)) return args.String(flag);
        if (value == typeof(int)) return args.Int(flag);
        if (value == typeof(long)) return args.Long(flag);
        if (value == typeof(float)) return args.Float(flag);
        if (value == typeof(double)) return args.Double(flag);
        if (value.IsEnum) return args.Enum(flag, value);

        throw new InvalidOperationException($"{type.Name} is not a type an app parameter can be.");
    }

    // A default for an enum parameter can arrive as its underlying integer.
    private static object? DefaultOf(ParameterInfo parameter)
    {
        var value = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        return value.IsEnum && parameter.DefaultValue is not null && parameter.DefaultValue.GetType() != value
            ? System.Enum.ToObject(value, parameter.DefaultValue)
            : parameter.DefaultValue;
    }

    private static string Format(object value) => value switch
    {
        string s => $"\"{s}\"",
        System.Enum e => FlagFor(e.ToString()),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
