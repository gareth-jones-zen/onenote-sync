using System.Text;
using System.Text.RegularExpressions;

namespace OneNoteSync.Tests;

/// <summary>A page held by the fake: its HTML is changed by each PATCH, as OneNote would change it.</summary>
public sealed class FakePage(string id, string title, string date, string html)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Date { get; } = date;
    public string Html { get; set; } = html;
}

/// <summary>A fake notebook. It applies each PATCH to its page's HTML, gives a new list the id "ul:new", and can drop a heading along with
/// the list it replaces, as OneNote did on a real page. It also records every PATCH body and created page.</summary>
public sealed partial class FakeNotebook(List<FakePage> pages, bool dropsHeading = false) : IOneNote
{
    public List<FakePage> Pages { get; } = pages;
    public int Patches { get; private set; }
    public List<string> PatchBodies { get; } = [];
    public List<string> Created { get; } = [];

    /// <summary>Thrown by the next PATCH after it's applied, as a reply lost on the way back would be.</summary>
    public bool LoseNextPatchReply { get; set; }

    [GeneratedRegex(@" id=""[^""]+""")]
    private static partial Regex Ids();

    [GeneratedRegex(@"<(\w+)")]
    private static partial Regex TagName();

    [GeneratedRegex(@"<title>(.*?)</title>")]
    private static partial Regex Title();

    [GeneratedRegex(@"<p (?!id)")]
    private static partial Regex UnnamedParagraph();

    /// <summary>Pages listed without a title, as OneNote lists a page made through Graph until it's opened.</summary>
    public HashSet<string> Untitled { get; } = [];

    public IReadOnlyList<PageInfo> DatedPages() =>
        Pages.Where(p => !Untitled.Contains(p.Id))
            .Select(p => new PageInfo(p.Id, p.Title, p.Date)).OrderBy(p => p.Date, StringComparer.Ordinal).ToList();

    private FakePage Page(string id) => Pages.FirstOrDefault(p => p.Id == id) ?? throw new OneNoteException(404, $"no page {id} (fake)");

    public byte[] Content(string pageId, bool ids = false, bool isNew = false)
    {
        var html = Page(pageId).Html;
        return Encoding.UTF8.GetBytes(ids ? html : Ids().Replace(html, ""));
    }

    public void Patch(string pageId, IReadOnlyList<PatchChange> changes)
    {
        Patches++;
        PatchBodies.Add(PatchChange.Body(changes));
        var page = Page(pageId);
        foreach (var change in changes)
        {
            var at = page.Html.IndexOf($"id=\"{change.Target}\"", StringComparison.Ordinal);
            if (at < 0)
                throw new InvalidOperationException($"substring not found: id=\"{change.Target}\"");
            var start = page.Html.LastIndexOf('<', at - 1);
            var tag = TagName().Match(page.Html, start).Groups[1].Value;
            var end = PageHtml.End(page.Html, start, tag);
            var replacement = ReplaceFirst(change.Content, "<ul>", "<ul id=\"ul:new\">");
            if (change.Action == "replace")
            {
                if (dropsHeading && tag == "ul")
                    start = page.Html.LastIndexOf("<p ", start - 1, StringComparison.Ordinal);
                page.Html = page.Html[..start] + replacement + page.Html[end..];
            }
            else if (change.Position == "before")
                page.Html = page.Html[..start] + change.Content + page.Html[start..];
            else
                page.Html = page.Html[..end] + replacement + page.Html[end..];
        }
        if (LoseNextPatchReply)
        {
            LoseNextPatchReply = false;
            throw new DeliveryUnknownException("No answer to PATCH (fake)");
        }
    }

    public string Create(string html)
    {
        Created.Add(html);
        var number = Pages.Count + 1;
        var title = Title().Match(html).Groups[1].Value;
        var body = ReplaceFirst(html, "<p ", $"<p id=\"p:n{number}\" ");
        var counter = 0;
        body = UnnamedParagraph().Replace(body, _ => $"<p id=\"p:n{number}-{counter++}\" ");
        Pages.Add(new FakePage($"page{number}", title, title[..10], body));
        return $"page{number}";
    }

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + replacement + text[(at + old.Length)..];
    }
}
