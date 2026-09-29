using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Blix.Shaders.Generator;

/// <summary>
/// Just enough JSON to read what <c>spirv-cross --reflect</c> writes.
/// </summary>
/// <remarks>
/// A generator runs inside the compiler on netstandard2.0, where System.Text.Json is a package
/// the compiler host may or may not have loaded in a compatible version. Reading one well-formed,
/// machine-written format is small enough to own, and owning it removes the question.
/// Values come back as <see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/>, string,
/// double, bool or null.
/// </remarks>
internal static class Json
{
    public static object? Parse(string text)
    {
        var at = 0;
        var value = Value(text, ref at);
        Skip(text, ref at);
        if (at != text.Length) throw new FormatException($"unexpected text at {at}");
        return value;
    }

    private static object? Value(string s, ref int at)
    {
        Skip(s, ref at);
        if (at >= s.Length) throw new FormatException("unexpected end");
        switch (s[at])
        {
            case '{': return Object(s, ref at);
            case '[': return Array(s, ref at);
            case '"': return String(s, ref at);
            case 't': Expect(s, ref at, "true"); return true;
            case 'f': Expect(s, ref at, "false"); return false;
            case 'n': Expect(s, ref at, "null"); return null;
            default: return Number(s, ref at);
        }
    }

    private static Dictionary<string, object?> Object(string s, ref int at)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        at++;
        Skip(s, ref at);
        if (s[at] == '}') { at++; return result; }
        while (true)
        {
            Skip(s, ref at);
            var key = String(s, ref at);
            Skip(s, ref at);
            if (s[at++] != ':') throw new FormatException($"expected ':' at {at - 1}");
            result[key] = Value(s, ref at);
            Skip(s, ref at);
            var c = s[at++];
            if (c == '}') return result;
            if (c != ',') throw new FormatException($"expected ',' or '}}' at {at - 1}");
        }
    }

    private static List<object?> Array(string s, ref int at)
    {
        var result = new List<object?>();
        at++;
        Skip(s, ref at);
        if (s[at] == ']') { at++; return result; }
        while (true)
        {
            result.Add(Value(s, ref at));
            Skip(s, ref at);
            var c = s[at++];
            if (c == ']') return result;
            if (c != ',') throw new FormatException($"expected ',' or ']' at {at - 1}");
        }
    }

    private static string String(string s, ref int at)
    {
        if (s[at] != '"') throw new FormatException($"expected a string at {at}");
        at++;
        var sb = new StringBuilder();
        while (s[at] != '"')
        {
            var c = s[at++];
            if (c != '\\') { sb.Append(c); continue; }
            var e = s[at++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)int.Parse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); at += 4; break;
                default: sb.Append(e); break;
            }
        }

        at++;
        return sb.ToString();
    }

    private static double Number(string s, ref int at)
    {
        var start = at;
        while (at < s.Length && "+-0123456789.eE".IndexOf(s[at]) >= 0) at++;
        if (at == start) throw new FormatException($"unexpected '{s[at]}' at {at}");
        return double.Parse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static void Expect(string s, ref int at, string word)
    {
        if (string.CompareOrdinal(s, at, word, 0, word.Length) != 0) throw new FormatException($"expected {word} at {at}");
        at += word.Length;
    }

    private static void Skip(string s, ref int at)
    {
        while (at < s.Length && char.IsWhiteSpace(s[at])) at++;
    }
}
