// Interim: Python string behaviour the port has to match exactly. Belongs in background-tools-lib (beside
// ai-keep-awake's Py.cs, which has ReprString too), shared by every port; delete this copy once the library has it.
using System.Globalization;
using System.Text;

namespace OneNoteSync.Interim;

/// <summary>Python's str methods where .NET's differ: whitespace (Python counts \x1c-\x1f as space), strip,
/// expandtabs, len (code points), repr, slices and int() of any Unicode digits.</summary>
public static class Py
{
    /// <summary>The characters Python's str.isspace() accepts, and so what \s, strip() and split() mean in
    /// Python. .NET's char.IsWhiteSpace and Regex \s leave out \x1c-\x1f.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>A Regex character class body matching what Python's \s matches: use as [\s\x1c-\x1f].</summary>
    public const string SpaceClass = @"\s\x1c-\x1f";

    /// <summary>str.strip().</summary>
    public static string Strip(string text) => RStrip(LStrip(text));

    public static string LStrip(string text)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i]))
            i++;
        return text[i..];
    }

    public static string RStrip(string text)
    {
        var i = text.Length;
        while (i > 0 && IsSpace(text[i - 1]))
            i--;
        return text[..i];
    }

    /// <summary>str.rstrip(chars) and str.lstrip(chars), for chars of the Basic Multilingual Plane.</summary>
    public static string RStrip(string text, string chars)
    {
        var i = text.Length;
        while (i > 0 && chars.Contains(text[i - 1]))
            i--;
        return text[..i];
    }

    public static string LStrip(string text, string chars)
    {
        var i = 0;
        while (i < text.Length && chars.Contains(text[i]))
            i++;
        return text[i..];
    }

    /// <summary>len(text): code points, not UTF-16 units (an emoji is 1, not 2).</summary>
    public static int Len(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            count++;
        }
        return count;
    }

    /// <summary>str.expandtabs(tabsize): a tab moves to the next multiple of tabsize; \n and \r start a new column.
    /// Columns count code points, as Python's do.</summary>
    public static string ExpandTabs(string text, int tabSize = 8)
    {
        var result = new StringBuilder();
        var column = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\t')
            {
                if (tabSize > 0)
                {
                    var spaces = tabSize - column % tabSize;
                    result.Append(' ', spaces);
                    column += spaces;
                }
            }
            else
            {
                result.Append(c);
                if (c is '\n' or '\r')
                    column = 0;
                else if (!(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1])))
                    column++;
            }
        }
        return result.ToString();
    }

    /// <summary>str.lower(), as far as it matters here: .NET's invariant lower case, plus Python's full mapping of
    /// U+0130 (capital I with dot) to "i" and a combining dot. (Python's final-sigma rule isn't ported.)</summary>
    public static string Lower(string text) =>
        (text.Contains('\u0130') ? text.Replace("\u0130", "i\u0307") : text).ToLowerInvariant();

    /// <summary>repr(str), for messages.</summary>
    public static string Repr(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var result = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': result.Append(@"\\"); break;
                case '\n': result.Append(@"\n"); break;
                case '\r': result.Append(@"\r"); break;
                case '\t': result.Append(@"\t"); break;
                default:
                    if (c == quote)
                        result.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f)
                        result.Append(@"\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else
                        result.Append(c);
                    break;
            }
        }
        return result.Append(quote).ToString();
    }

    /// <summary>items[start:] with Python's meaning of a negative start: items[-2:] is the last two, and
    /// items[-0:] (start 0) is all of them.</summary>
    public static List<T> From<T>(IReadOnlyList<T> items, int start)
    {
        if (start < 0)
            start = Math.Max(0, items.Count + start);
        return items.Skip(Math.Min(start, items.Count)).ToList();
    }

    /// <summary>int(text) for a run of Unicode decimal digits (Python's int() takes any script's digits).</summary>
    public static int Int(string digits)
    {
        long value = 0;
        foreach (var c in digits)
        {
            var digit = CharUnicodeInfo.GetDecimalDigitValue(c);
            if (digit < 0)
                throw new FormatException($"invalid literal for int() with base 10: {Repr(digits)}");
            value = checked(value * 10 + digit);
        }
        return checked((int)value);
    }

    /// <summary>html.escape(text, quote=False): only &amp;, &lt; and &gt;. (WebUtility.HtmlEncode would also
    /// turn £ and other characters into numeric references.)</summary>
    public static string HtmlEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Text as Python's text-mode files and print() wrote it on Windows: each "\n" as "\r\n".</summary>
    public static string WindowsLines(string text) => text.Replace("\n", "\r\n");
}
