using System.Text;
using System.Text.RegularExpressions;
using PyCompat;

namespace OneNoteSync;

public sealed record WeekStatus(string Date, string Title, bool Current, IReadOnlyList<string> Filled,
    IReadOnlyList<string> Empty, IReadOnlyList<string> Missing)
{
    /// <summary>"2026-11-16 (current, still updating): 4 of 5 sections filled - Service empty"</summary>
    public string Line(int total)
    {
        var gaps = Empty.Select(s => $"{s} empty").Concat(Missing.Select(s => $"{s} missing")).ToList();
        return $"{Date} ({(Current ? "current, still updating" : "final")}): {Filled.Count} of {total} sections filled"
            + (gaps.Count > 0 ? " - " + string.Join(", ", gaps) : "");
    }
}

/// <summary>Status says how complete the newest pages are: which sections have text under them, which are empty,
/// and which have no heading at all. A page is written by several people at different times and is never formally
/// finished, so the newest page is reported as current (still updating) and every older one as final.
///
/// It reads the pages from OneNote, after the same repair pull makes (PageRepair), so a heading OneNote's export
/// moved to the end still counts the text the page shows under it.
///
/// A section heading is a heading paragraph (bold, 14pt or more) whose text starts with a section's name
/// ("Digital", "Digital Tribe Update:"). Other headings, such as "Highlights:", are part of the section they're in;
/// only another section, or one of [post] headings ("Executive Summary"), ends it. A section is filled if any
/// heading for it has a word under it.</summary>
public static partial class Status
{
    [GeneratedRegex(@"<p\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Paragraph();

    [GeneratedRegex(@"[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"<span\b[^>]*font-weight:\s*bold[^>]*>.*?</span>|<(b|strong)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex BoldRun();

    /// <summary>A short paragraph whose text is all bold, at any size: older pages have a 12pt "Architecture"
    /// heading (July 2026), which PageHtml.IsHeading (14pt or more) doesn't count.</summary>
    private static bool BoldOnly(string paragraph)
    {
        var text = PageHtml.Text(paragraph);
        return text.Length > 0 && PyStr.Length(text) < 60 && PageHtml.Text(BoldRun().Replace(paragraph, "")).Length == 0;
    }

    /// <summary>The sections reported: [status] sections, else [post] headings.</summary>
    public static IReadOnlyList<string> Sections(Config config) =>
        config.StatusSections is { Count: > 0 } sections ? sections
        : config.Post?.Headings is { Count: > 0 } headings ? headings
        : throw new UsageException($"{config.Path} names no sections: add [status] sections, or [post] headings.");

    public static List<WeekStatus> Run(Config config, IOneNote notebook, int newest)
    {
        var sections = Sections(config);
        var boundaries = sections.Concat(config.Post?.Headings ?? []).Distinct().ToList();
        var pages = Recorded.DatedPages(notebook, config.StateFile);
        var latest = pages.Count > 0 ? pages[^1].Date : null;
        var found = new List<WeekStatus>();
        foreach (var page in Enumerable.Reverse(Py.From(pages, -Math.Max(newest, 1)).ToList()))
        {
            var (html, _) = PageRepair.Repair(notebook.Content(page.Id), config.Post?.Headings ?? []);
            var words = Words(Encoding.UTF8.GetString(html), boundaries);
            found.Add(new WeekStatus(page.Date, page.Title, page.Date == latest,
                sections.Where(s => words.GetValueOrDefault(s) > 0).ToList(),
                sections.Where(s => words.TryGetValue(s, out var n) && n == 0).ToList(),
                sections.Where(s => !words.ContainsKey(s)).ToList()));
        }
        return found;
    }

    /// <summary>Words of text under each boundary heading on the page, taking its fullest occurrence.</summary>
    public static Dictionary<string, int> Words(string html, IReadOnlyList<string> boundaries)
    {
        // Every heading-like paragraph: a section's own heading marks a boundary, and the rest ("Highlights:",
        // "Challenges:") are sub-headings, which aren't content: a section that has only "Highlights:" under it is
        // empty (12 Oct 2026's Architecture).
        var headings = new List<(int Start, int End, string? Name)>();
        for (var match = Paragraph().Match(html); match.Success; match = match.NextMatch())
        {
            var end = PageHtml.End(html, match.Index, "p");
            var piece = html[match.Index..end];
            if (!PageHtml.IsHeading(piece) && !BoldOnly(piece))
                continue;
            var text = PyStr.Lower(PyStr.Strip(PageHtml.Text(piece)));
            var name = boundaries.FirstOrDefault(b => text.StartsWith(PyStr.Lower(PyStr.Strip(b)), StringComparison.Ordinal));
            headings.Add((match.Index, end, name));
        }
        var marks = headings.Where(h => h.Name is not null).ToList();
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < marks.Count; i++)
        {
            var stop = i + 1 < marks.Count ? marks[i + 1].Start : html.Length;
            var count = 0;
            var at = marks[i].End;
            foreach (var sub in headings.Where(h => h.Name is null && h.Start >= at && h.End <= stop))
            {
                count += Word().Matches(PageHtml.Text(html[at..sub.Start])).Count;
                at = sub.End;
            }
            if (stop > at)
                count += Word().Matches(PageHtml.Text(html[at..stop])).Count;
            best[marks[i].Name!] = Math.Max(best.GetValueOrDefault(marks[i].Name!), count);
        }
        return best;
    }
}
