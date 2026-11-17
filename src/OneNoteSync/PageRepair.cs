using System.Text;
using System.Text.RegularExpressions;
using PyCompat;

namespace OneNoteSync;

/// <summary>Puts a section heading that OneNote's export left at the end of a page back where the page shows it.
///
/// Seen on the 16 Nov 2026 weekly update: Kurt pasted bullets under the "Service" heading. On screen the heading sits
/// between Assurance and Architecture with his bullets under it, but Graph's HTML keeps the bullets in place and
/// emits the heading itself outside the page's main block, after everything else. Converted as served, his text read
/// as Assurance's and Service looked empty. Editing the page fixes one week only, and it will happen again.
///
/// The repair, for each heading named in [post] headings that sits outside the block holding the others:
/// - it goes back into that block, in the headings' order: before the next heading that's there;
/// - it takes a "bare" list as its content: when two or more lists sit together just before that next heading, with
///   no paragraph between them, every list after the first is the orphan's (a section is normally a heading, then
///   its list; a list straight after another list belongs to no heading in the export);
/// - otherwise it goes just before the next heading, empty, as the page had it.
/// A page with nothing out of place is returned byte for byte as served.</summary>
public static partial class PageRepair
{
    private sealed record Element(string Tag, int Start, int End);

    [GeneratedRegex(@"<(p|ul|ol|li|table|div|img|object)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ElementStart();

    [GeneratedRegex(@"<div\b[^>]*position:\s*absolute[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Column();

    /// <summary>(the html, the headings moved). Unchanged pages come back as the same bytes.</summary>
    public static (byte[] Html, List<string> Moved) Repair(byte[] served, IReadOnlyList<string> headings)
    {
        var moved = new List<string>();
        if (headings.Count == 0)
            return (served, moved);
        var html = Encoding.UTF8.GetString(served);
        for (var guard = 0; guard < headings.Count; guard++)
        {
            var (repaired, name) = RepairOnce(html, headings);
            if (repaired is null)
                break;
            html = repaired;
            moved.Add(name!);
        }
        return moved.Count == 0 ? (served, moved) : (Encoding.UTF8.GetBytes(html), moved);
    }

    private static (string? Html, string? Name) RepairOnce(string html, IReadOnlyList<string> headings)
    {
        for (var column = Column().Match(html); column.Success; column = column.NextMatch())
        {
            var end = PageHtml.End(html, column.Index, "div");
            var innerEnd = html.LastIndexOf("</div", end - 1, StringComparison.OrdinalIgnoreCase);
            if (innerEnd < column.Index)
                continue;
            var children = Children(html, column.Index + column.Length, innerEnd);
            var containerAt = children.FindIndex(c => c.Tag is "ul" or "ol" && HeadingsIn(html, c, headings).Count > 0);
            if (containerAt < 0)
                continue;
            var container = children[containerAt];
            foreach (var orphan in children.Skip(containerAt + 1))
            {
                var name = Named(html, orphan, headings);
                if (name is null)
                    continue;
                var rank = Rank(name, headings);
                var inner = Children(html, OpeningEnd(html, container), ClosingStart(html, container));
                var next = inner.FindIndex(c => Named(html, c, headings) is { } n && Rank(n, headings) > rank);
                int insertAt;
                if (next < 0)
                    insertAt = ClosingStart(html, container);
                else
                {
                    var run = 0;
                    for (var i = next - 1; i >= 0 && inner[i].Tag is "ul" or "ol"; i--)
                        run++;
                    insertAt = run >= 2 ? inner[next - run + 1].Start : inner[next].Start;
                }
                var piece = html[orphan.Start..orphan.End];
                return (html[..insertAt] + piece + html[insertAt..orphan.Start] + html[orphan.End..], name);
            }
        }
        return (null, null);
    }

    /// <summary>The elements directly inside [from, to), skipping text and line breaks.</summary>
    private static List<Element> Children(string html, int from, int to)
    {
        var found = new List<Element>();
        var at = from;
        while (at < to)
        {
            var match = ElementStart().Match(html, at, to - at);
            if (!match.Success)
                break;
            var tag = match.Groups[1].Value.ToLowerInvariant();
            var stop = Math.Min(PageHtml.End(html, match.Index, tag), to);
            found.Add(new Element(tag, match.Index, stop));
            at = stop;
        }
        return found;
    }

    private static List<string> HeadingsIn(string html, Element list, IReadOnlyList<string> headings) =>
        Children(html, OpeningEnd(html, list), ClosingStart(html, list)).Select(c => Named(html, c, headings))
            .OfType<string>().ToList();

    /// <summary>The heading this element is, if it's a paragraph naming one of the headings.</summary>
    private static string? Named(string html, Element element, IReadOnlyList<string> headings)
    {
        if (element.Tag != "p")
            return null;
        var piece = html[element.Start..element.End];
        if (!PageHtml.IsHeading(piece))
            return null;
        var text = PyStr.Lower(PyStr.Strip(PyStr.RStrip(PyStr.Strip(PageHtml.Text(piece)), "?:")));
        return headings.FirstOrDefault(h => PyStr.Lower(PyStr.Strip(h)) == text);
    }

    private static int Rank(string name, IReadOnlyList<string> headings) =>
        headings.Select((h, i) => (h, i)).First(x => x.h == name).i;

    private static int OpeningEnd(string html, Element element) => html.IndexOf('>', element.Start) + 1;

    private static int ClosingStart(string html, Element element)
    {
        var at = html.LastIndexOf("</", element.End - 1, StringComparison.Ordinal);
        return at > element.Start ? at : element.End;
    }
}
