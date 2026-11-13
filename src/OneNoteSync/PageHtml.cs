using System.Text.RegularExpressions;
using PyCompat;

namespace OneNoteSync;

/// <summary>One top-level block of a page: its tag (lower case), its id if the page was fetched with ids, and its
/// exact HTML as it appears in the page.</summary>
public sealed record Block(string Tag, string? Id, string Html);

/// <summary>Reading a section of a OneNote page.
///
/// A weekly page is a column of headings, each followed by that team's content: usually one bulleted list,
/// sometimes "Highlights:" and "Challenges:" lists. A heading is a bold paragraph at 14pt or more (the page
/// title-level "Executive Summary" is 18pt). A section runs from its heading to the next heading, or to the end of
/// the column.
///
/// No HTML parser, on purpose: the page is sliced by regex and nesting depth, as the Python did, so a block's HTML
/// is an exact substring of the page. The read-back compares exact substrings and a dropped heading is put back
/// from its own source text; a DOM parser would reserialise and break both. Where Python's \s matched, the
/// patterns here say [\s\x1c-\x1f], because .NET's \s leaves those four out.</summary>
public static partial class PageHtml
{
    [GeneratedRegex(@"font-size:(1[4-9]|[2-9]\d)(\.\d+)?pt[^""]*font-weight:bold|font-weight:bold[^""]*font-size:(1[4-9]|[2-9]\d)(\.\d+)?pt", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<(p|ul|ol|table|div|img|object)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"<div\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Column();

    [GeneratedRegex(@"\bid=""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"</(li|p)>|<br[\s\x1c-\x1f]*/?>|<(ul|ol|li)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[\s\x1c-\x1f]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    // _end's patterns for the block tags, generated; any other tag (only the tests' fake asks) is built at run time.
    [GeneratedRegex(@"\G<p\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenP();
    [GeneratedRegex(@"</p[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseP();
    [GeneratedRegex(@"\G<ul\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenUl();
    [GeneratedRegex(@"</ul[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseUl();
    [GeneratedRegex(@"\G<ol\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenOl();
    [GeneratedRegex(@"</ol[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseOl();
    [GeneratedRegex(@"\G<table\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenTable();
    [GeneratedRegex(@"</table[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseTable();
    [GeneratedRegex(@"\G<div\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenDiv();
    [GeneratedRegex(@"</div[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseDiv();
    [GeneratedRegex(@"\G<img\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenImg();
    [GeneratedRegex(@"</img[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseImg();
    [GeneratedRegex(@"\G<object\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OpenObject();
    [GeneratedRegex(@"</object[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CloseObject();

    private static (Regex Opening, Regex Closing) Patterns(string tag) => tag switch
    {
        "p" => (OpenP(), CloseP()),
        "ul" => (OpenUl(), CloseUl()),
        "ol" => (OpenOl(), CloseOl()),
        "table" => (OpenTable(), CloseTable()),
        "div" => (OpenDiv(), CloseDiv()),
        "img" => (OpenImg(), CloseImg()),
        "object" => (OpenObject(), CloseObject()),
        _ => (new Regex($@"\G<{Regex.Escape(tag)}\b[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
              new Regex($@"</{Regex.Escape(tag)}[\s\x1c-\x1f]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    };

    /// <summary>Where the element starting at start ends, allowing for nesting of the same tag (Python's _end).</summary>
    public static int End(string html, int start, string tag)
    {
        var (opening, closing) = Patterns(tag);
        var first = opening.Match(html, start);
        if (!first.Success)
            throw new InvalidOperationException($"No <{tag}> at {start}.");
        if (first.Groups[1].Value == "/")
            return first.Index + first.Length;
        var (depth, at) = (1, first.Index + first.Length);
        while (depth > 0)
        {
            var o = Search(opening, html, at);
            var c = closing.Match(html, at);
            if (!c.Success)
                return html.Length;
            if (o is not null && o.Index < c.Index)
            {
                depth += o.Groups[1].Value != "/" ? 1 : 0;
                at = o.Index + o.Length;
            }
            else
            {
                depth--;
                at = c.Index + c.Length;
            }
        }
        return at;
    }

    /// <summary>opening.search(html, at): the \G-anchored pattern, tried at each "&lt;" from at on.</summary>
    private static Match? Search(Regex anchored, string html, int at)
    {
        for (var i = html.IndexOf('<', at); i >= 0; i = html.IndexOf('<', i + 1))
        {
            var match = anchored.Match(html, i);
            if (match.Success)
                return match;
        }
        return null;
    }

    /// <summary>Plain text, one line per paragraph or list item.</summary>
    public static string Text(string fragment)
    {
        fragment = LineBreaks().Replace(fragment, "\n");
        fragment = PyHtml.Unescape(AnyTag().Replace(fragment, ""));
        var lines = fragment.Split('\n').Select(line => PyStr.Strip(Spaces().Replace(line, " "))).Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    public static bool IsHeading(string element) =>
        element.Length >= 2 && element[0] == '<' && PyStr.Lower(element[1].ToString()).StartsWith('p')
        && Heading().IsMatch(element) && PyStr.Length(Text(element)) < 60;

    /// <summary>The top-level blocks of every column (div), in order. &lt;br&gt; is skipped.</summary>
    public static List<Block> Blocks(string html)
    {
        var found = new List<Block>();
        for (var column = Column().Match(html); column.Success; column = column.NextMatch())
        {
            var end = End(html, column.Index, "div");
            var at = column.Index + column.Length;
            var innerEnd = end - at >= 5 ? html.LastIndexOf("</div", end - 1, end - at, StringComparison.Ordinal) : -1;
            if (innerEnd >= 0 && innerEnd + 5 > end)
                innerEnd = -1;
            while (true)
            {
                if (innerEnd < at)
                    break;
                var match = BlockStart().Match(html, at, innerEnd - at);
                if (!match.Success)
                    break;
                var tag = match.Groups[1].Value.ToLowerInvariant();
                if (tag == "div")   // a nested column is handled by the outer loop's own match
                {
                    at = End(html, match.Index, "div");
                    continue;
                }
                var stop = End(html, match.Index, tag);
                var ident = IdAttribute().Match(match.Value);
                found.Add(new Block(tag, ident.Success ? ident.Groups[1].Value : null, html[match.Index..stop]));
                at = stop;
            }
        }
        return found;
    }

    private static bool Same(string heading, string name) =>
        PyStr.Lower(PyStr.Strip(PyStr.RStrip(PyStr.Strip(Text(heading)), "?:"))) == PyStr.Lower(PyStr.Strip(name));

    /// <summary>The heading called name and the blocks under it; (null, []) if there's none.</summary>
    public static (Block? Heading, List<Block> Under) Section(string html, string name)
    {
        var all = Blocks(html);
        for (var i = 0; i < all.Count; i++)
        {
            if (IsHeading(all[i].Html) && Same(all[i].Html, name))
            {
                var under = new List<Block>();
                foreach (var after in all.Skip(i + 1))
                {
                    if (IsHeading(after.Html))
                        break;
                    under.Add(after);
                }
                return (all[i], under);
            }
        }
        return (null, []);
    }

    public static List<string> Headings(string html) =>
        Blocks(html).Where(b => IsHeading(b.Html)).Select(b => Text(b.Html)).ToList();
}
