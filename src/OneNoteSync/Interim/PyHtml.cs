// Interim: Python's html.unescape, ported exactly. Belongs in background-tools-lib, shared by every port that
// reads HTML as Python did; delete this copy once the library has it.
using System.Globalization;
using System.Text.RegularExpressions;

namespace OneNoteSync.Interim;

/// <summary>html.unescape, as CPython's html/__init__.py does it: every HTML5 named reference (with or without
/// the ";" where HTML5 allows it, and the longest-prefix rule for the rest), numeric references with the
/// Windows-1252 fix-ups for 0x80-0x9F, U+FFFD for NUL, surrogates and anything past U+10FFFF, and nothing for the
/// code points HTML forbids. WebUtility.HtmlDecode knows only HTML 4's names and none of these rules.</summary>
public static partial class PyHtml
{
    private static readonly Lazy<Dictionary<string, string>> Html5 = new(() =>
    {
        var pairs = PyHtmlEntities.Pairs;
        var table = new Dictionary<string, string>(pairs.Length / 2, StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
            table[pairs[i]] = pairs[i + 1];
        return table;
    });

    private static readonly Dictionary<int, string> InvalidCharrefs = new()
    {
        [0x00] = "�", [0x0d] = "\r", [0x80] = "€", [0x81] = "\x81", [0x82] = "‚", [0x83] = "ƒ",
        [0x84] = "„", [0x85] = "…", [0x86] = "†", [0x87] = "‡", [0x88] = "ˆ", [0x89] = "‰",
        [0x8a] = "Š", [0x8b] = "‹", [0x8c] = "Œ", [0x8d] = "\x8d", [0x8e] = "Ž", [0x8f] = "\x8f",
        [0x90] = "\x90", [0x91] = "‘", [0x92] = "’", [0x93] = "“", [0x94] = "”", [0x95] = "•",
        [0x96] = "–", [0x97] = "—", [0x98] = "˜", [0x99] = "™", [0x9a] = "š", [0x9b] = "›",
        [0x9c] = "œ", [0x9d] = "\x9d", [0x9e] = "ž", [0x9f] = "Ÿ",
    };

    private static bool InvalidCodepoint(long n) =>
        n is >= 0x1 and <= 0x8 or 0xb or >= 0xe and <= 0x1f or >= 0x7f and <= 0x9f or >= 0xfdd0 and <= 0xfdef
        || (n <= 0x10ffff && (n & 0xfffe) == 0xfffe);

    [GeneratedRegex(@"&(#[0-9]+;?|#[xX][0-9a-fA-F]+;?|[^\t\n\f <&#;]{1,32};?)", RegexOptions.CultureInvariant)]
    private static partial Regex Charref();

    /// <summary>html.unescape(text).</summary>
    public static string Unescape(string text) =>
        !text.Contains('&') ? text : Charref().Replace(text, match => Replace(match.Groups[1].Value));

    private static string Replace(string s)
    {
        if (s[0] == '#')
        {
            var hex = s[1] is 'x' or 'X';
            var digits = (hex ? s[2..] : s[1..]).TrimEnd(';');
            var num = Number(digits, hex);
            if (num <= int.MaxValue && InvalidCharrefs.TryGetValue((int)num, out var fixedUp))
                return fixedUp;
            if (num is >= 0xD800 and <= 0xDFFF || num > 0x10FFFF)
                return "�";
            if (InvalidCodepoint(num))
                return "";
            return char.ConvertFromUtf32((int)num);
        }
        var html5 = Html5.Value;
        if (html5.TryGetValue(s, out var value))
            return value;
        for (var x = s.Length - 1; x > 1; x--)
        {
            if (html5.TryGetValue(s[..x], out var prefix))
                return prefix + s[x..];
        }
        return "&" + s;
    }

    /// <summary>The number, or past U+10FFFF (Python's int has no limit; only "too big" matters here).</summary>
    private static long Number(string digits, bool hex)
    {
        long value = 0;
        foreach (var c in digits)
        {
            var digit = hex ? int.Parse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : c - '0';
            value = value * (hex ? 16 : 10) + digit;
            if (value > 0x10FFFF)
                return 0x110000;
        }
        return value;
    }

    /// <summary>Every named reference Python knows, for tests.</summary>
    internal static int NamedCount => Html5.Value.Count;
}
