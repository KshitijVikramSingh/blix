using System.Globalization;

namespace Blix.Core;

/// <summary>
/// The command line, parsed once and read by whoever understands each part of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no schema.</b> Each layer reads the flags it knows: the window its size and
/// <c>--debug</c>, a headless host <c>--frames</c>, the app everything else. Nothing here decides
/// in advance what a flag means, because the reader that asks is the only thing that knows.
/// </para>
/// <para>
/// <b>So a token's meaning is settled when it is read, not when it is parsed.</b> Whether
/// <c>--fog on</c> is a flag and a positional or an option and its value is not something the
/// grammar can tell. <see cref="Flag"/> takes the name alone; <see cref="String(string)"/> and
/// the typed reads take the name and the token after it. Read options before
/// <see cref="Positionals"/>, which returns what nothing has taken.
/// </para>
/// <para>
/// <b>Every read is recorded.</b> <see cref="Unread"/> is what nobody asked for, and
/// <see cref="BlixApps.Main"/> prints it after the app returns. A typo like <c>--year 3</c> and a
/// <c>--frames 120</c> handed to something that never opens a window are the same failure: an
/// argument that looked accepted and changed nothing.
/// </para>
/// <para>
/// <b>Numbers are read culture-invariant, always.</b> A machine that writes one thousand as
/// <c>1.000</c> must read <c>--scale 1.5</c> as one and a half.
/// </para>
/// <para>
/// <b>Names match without dashes or case.</b> A read of <c>map-seed</c> accepts
/// <c>--map-seed</c>, <c>--mapseed</c> and <c>--MapSeed</c>. <c>--name=value</c> and
/// <c>--name value</c> are the same, the last occurrence of a repeated option wins for single
/// reads, and <see cref="All"/> returns every occurrence. Everything after a bare <c>--</c> is
/// positional.
/// </para>
/// </remarks>
public sealed class AppArgs
{
    private readonly Token[] tokens;
    private readonly int positionalFrom;

    private AppArgs(Token[] tokens, int positionalFrom)
    {
        this.tokens = tokens;
        this.positionalFrom = positionalFrom;
    }

    /// <summary>No arguments at all.</summary>
    public static AppArgs Empty => new(Array.Empty<Token>(), 0);

    /// <summary>Parse a command line. Nothing is interpreted until it is read.</summary>
    public static AppArgs Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var list = new List<Token>();
        var positionalFrom = int.MaxValue;
        foreach (var arg in args)
        {
            if (positionalFrom == int.MaxValue && arg == "--")
            {
                positionalFrom = list.Count;
                continue;
            }

            if (positionalFrom == int.MaxValue && arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var eq = arg.IndexOf('=');
                list.Add(eq < 0
                    ? new Token(arg, Key(arg[2..]), null)
                    : new Token(arg, Key(arg[2..eq]), arg[(eq + 1)..]));
            }
            else
            {
                list.Add(new Token(arg, null, null));
            }
        }

        return new AppArgs(list.ToArray(), positionalFrom);
    }

    /// <summary>True when <c>--name</c> was passed. <c>--name=false</c> reads false.</summary>
    public bool Flag(string name)
    {
        var found = false;
        foreach (var at in Occurrences(name))
        {
            var token = tokens[at];
            token.Read = true;
            found = token.Inline is null || ParseBool(name, token.Inline);
        }

        return found;
    }

    /// <summary>True when <c>--name</c> was passed at all, in either form. Reads nothing.</summary>
    public bool Has(string name) => Occurrences(name).Any();

    /// <summary>The value of <c>--name</c>, or null when it was not passed.</summary>
    public string? String(string name)
    {
        string? value = null;
        foreach (var at in Occurrences(name)) value = TakeValue(name, at);
        return value;
    }

    /// <summary>The value of <c>--name</c>, or <paramref name="fallback"/>.</summary>
    public string String(string name, string fallback) => String(name) ?? fallback;

    /// <summary>Every value given for a repeated <c>--name</c>, in order.</summary>
    public IReadOnlyList<string> All(string name)
    {
        var values = new List<string>();
        foreach (var at in Occurrences(name)) values.Add(TakeValue(name, at));
        return values;
    }

    /// <summary>
    /// The <paramref name="count"/> tokens after <c>--name</c>, as in <c>--win 1280 720</c>, or
    /// null when it was not passed.
    /// </summary>
    public IReadOnlyList<string>? Values(string name, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        IReadOnlyList<string>? result = null;
        foreach (var at in Occurrences(name))
        {
            var token = tokens[at];
            token.Read = true;
            var values = new List<string>(count);
            if (token.Inline is not null) values.Add(token.Inline);
            var next = at + 1;
            while (values.Count < count)
            {
                if (next >= tokens.Length || next >= positionalFrom || tokens[next].Name is not null)
                {
                    throw new AppArgsException(
                        $"--{name} expects {count} values, got {values.Count}.");
                }

                tokens[next].Read = true;
                values.Add(tokens[next].Text);
                next++;
            }

            result = values;
        }

        return result;
    }

    /// <summary>The value of <c>--name</c> as a whole number, or null.</summary>
    public int? Int(string name) => Number<int>(name, "a whole number",
        s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null);

    /// <summary>The value of <c>--name</c> as a whole number, or <paramref name="fallback"/>.</summary>
    public int Int(string name, int fallback) => Int(name) ?? fallback;

    /// <summary>The value of <c>--name</c> as a number, or null.</summary>
    public float? Float(string name) => Number<float>(name, "a number",
        s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (float?)null);

    /// <summary>The value of <c>--name</c> as a number, or <paramref name="fallback"/>.</summary>
    public float Float(string name, float fallback) => Float(name) ?? fallback;

    /// <summary>The value of <c>--name</c> as a number, or null.</summary>
    public double? Double(string name) => Number<double>(name, "a number",
        s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null);

    /// <summary>The value of <c>--name</c> as a number, or <paramref name="fallback"/>.</summary>
    public double Double(string name, double fallback) => Double(name) ?? fallback;

    /// <summary>
    /// The value of <c>--name</c> as one of <typeparamref name="T"/>'s members, matched without
    /// dashes or case, so <c>--view shadow-map</c> reads <c>ShadowMap</c>. Null when not passed.
    /// </summary>
    public T? Enum<T>(string name) where T : struct, System.Enum
    {
        var text = String(name);
        if (text is null) return null;

        foreach (var member in System.Enum.GetValues<T>())
        {
            if (Key(member.ToString()) == Key(text)) return member;
        }

        throw new AppArgsException(
            $"--{name} expects one of {string.Join(", ", System.Enum.GetNames<T>())}, got '{text}'.");
    }

    /// <summary>The value of <c>--name</c> as a member of <typeparamref name="T"/>, or <paramref name="fallback"/>.</summary>
    public T Enum<T>(string name, T fallback) where T : struct, System.Enum => Enum<T>(name) ?? fallback;

    /// <summary>
    /// Every token nothing has taken that is not itself an option, in order. Reading them marks
    /// them read, so read options first.
    /// </summary>
    public IReadOnlyList<string> Positionals
    {
        get
        {
            var result = new List<string>();
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.Read || (token.Name is not null && i < positionalFrom)) continue;
                token.Read = true;
                result.Add(token.Text);
            }

            return result;
        }
    }

    /// <summary>
    /// The first token, when it is not an option: the verb of a program like <c>cook batch …</c>.
    /// Marks it read, so it is not among <see cref="Positionals"/>. Null when the first token is an
    /// option or there are none.
    /// </summary>
    public string? Command()
    {
        if (tokens.Length == 0 || (tokens[0].Name is not null && positionalFrom > 0)) return null;
        tokens[0].Read = true;
        return tokens[0].Text;
    }

    /// <summary>The tokens nothing has read, in the order they were given.</summary>
    public IReadOnlyList<string> Unread => tokens.Where(t => !t.Read).Select(t => t.Text).ToArray();

    private IEnumerable<int> Occurrences(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = Key(name);
        for (var i = 0; i < Math.Min(tokens.Length, positionalFrom); i++)
        {
            if (tokens[i].Name == key) yield return i;
        }
    }

    private string TakeValue(string name, int at)
    {
        var token = tokens[at];
        token.Read = true;
        if (token.Inline is not null) return token.Inline;

        var next = at + 1;
        if (next >= tokens.Length || next >= positionalFrom || tokens[next].Name is not null)
        {
            throw new AppArgsException($"--{name} expects a value after it.");
        }

        tokens[next].Read = true;
        return tokens[next].Text;
    }

    private T? Number<T>(string name, string what, Func<string, T?> parse) where T : struct
    {
        var text = String(name);
        if (text is null) return null;
        return parse(text) ?? throw new AppArgsException($"--{name} expects {what}, got '{text}'.");
    }

    private static bool ParseBool(string name, string text) => Key(text) switch
    {
        "true" or "on" or "yes" or "1" => true,
        "false" or "off" or "no" or "0" => false,
        _ => throw new AppArgsException($"--{name} expects true or false, got '{text}'."),
    };

    // Dashes, underscores and case carry no meaning in a name, so --map-seed, --mapseed and
    // --MapSeed are one flag.
    private static string Key(string name) =>
        new string(name.Where(c => c is not ('-' or '_')).Select(char.ToLowerInvariant).ToArray());

    private sealed class Token(string text, string? name, string? inline)
    {
        public string Text { get; } = text;

        public string? Name { get; } = name;

        public string? Inline { get; } = inline;

        public bool Read { get; set; }
    }
}

/// <summary>An argument that was passed but cannot mean what its reader asked for.</summary>
/// <remarks>
/// Loud on purpose. <c>--frames abc</c> used to be skipped, which ran an unbounded session in a
/// place that asked for a bounded one. <see cref="BlixApps.Main"/> prints the message and exits 2.
/// </remarks>
public sealed class AppArgsException(string message) : Exception(message);
