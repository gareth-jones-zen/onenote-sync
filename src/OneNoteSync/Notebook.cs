using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace OneNoteSync;

/// <summary>A page in the section whose title starts with a date.</summary>
public sealed record PageInfo(string Id, string Title, string Date);

/// <summary>One change in a PATCH to a page's content: replace or insert (before or after) the target element.</summary>
public sealed record PatchChange(string? Target, string Action, string? Position, string Content)
{
    public static PatchChange Replace(string? target, string content) => new(target, "replace", null, content);
    public static PatchChange InsertAfter(string? target, string content) => new(target, "insert", "after", content);
    public static PatchChange InsertBefore(string? target, string content) => new(target, "insert", "before", content);

    /// <summary>The PATCH body, byte for byte what the Python's json.dumps(changes) sent: keys in the order
    /// target, action, (position), content; ", " and ": "; everything past ASCII as \uXXXX.</summary>
    public static string Body(IEnumerable<PatchChange> changes) =>
        PyJson.Dump(new JsonArray(changes.Select(c =>
        {
            var change = new JsonObject { ["target"] = c.Target, ["action"] = c.Action };
            if (c.Position is not null)
                change["position"] = c.Position;
            change["content"] = c.Content;
            return (JsonNode?)change;
        }).ToArray()));
}

/// <summary>The OneNote calls the tool makes: the seam the tests' fake notebook stands in for.</summary>
public interface IOneNote
{
    /// <summary>Every page whose title starts with a date, oldest first (by date, then title).</summary>
    IReadOnlyList<PageInfo> DatedPages();

    PageInfo? PageFor(string date) => DatedPages().FirstOrDefault(p => p.Date == date);

    /// <summary>The page's HTML, as served. ids adds each element's id, which a PATCH needs as its target. isNew:
    /// the page was just created, and OneNote can answer 404 for some seconds.</summary>
    byte[] Content(string pageId, bool ids = false, bool isNew = false);

    void Patch(string pageId, IReadOnlyList<PatchChange> changes);

    /// <summary>Create a page from HTML; returns its id.</summary>
    string Create(string html);
}

/// <summary>A PATCH or POST was sent, but no answer came back (a timeout, a dropped connection or a 5xx), so it may
/// or may not have been applied. It isn't retried: a retried insert could put the list in twice, and a retried
/// create could make the week's page twice (a deliberate change from the Python, which retried).</summary>
public sealed class DeliveryUnknownException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>OneNote answered a request with an error.</summary>
public sealed class OneNoteException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static partial class Dates
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})\b", RegexOptions.CultureInvariant)]
    public static partial Regex Dated();

    /// <summary>The dated pages among (id, title) pairs, sorted as the Python sorted them: (date, title), by code point.</summary>
    public static List<PageInfo> Sort(IEnumerable<(string Id, string? Title)> pages)
    {
        var found = new List<PageInfo>();
        foreach (var (id, title) in pages)
        {
            var match = Dated().Match(title ?? "");
            if (match.Success)
                found.Add(new PageInfo(id, title!, match.Groups[1].Value));
        }
        // List.Sort isn't stable; Python's sort is, so ties keep their order.
        return found.Select((page, index) => (page, index))
            .OrderBy(x => x.page.Date, StringComparer.Ordinal).ThenBy(x => x.page.Title, StringComparer.Ordinal).ThenBy(x => x.index)
            .Select(x => x.page).ToList();
    }
}

/// <summary>Pages the tool knows by id but OneNote doesn't list by title. A page this tool creates through Graph
/// shows as untitled, in the page list and in OneNote itself, until someone opens it (seen 14 Nov 2026), so a
/// search by title alone would miss it and a second post would create the week's page twice. The ids come from the
/// state file (every page posted to, and every page created, is recorded there); each one OneNote doesn't list with
/// a dated title is read, and its HTML's title gives its date. A page that's gone (404) is skipped.</summary>
public static partial class Recorded
{
    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    /// <summary>The recorded pages missing from listed, with their titles read from their HTML.</summary>
    public static List<PageInfo> Unlisted(IOneNote notebook, string stateFile, IReadOnlyList<PageInfo> listed)
    {
        var known = listed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var found = new List<(string, string?)>();
        foreach (var id in StateFile.Load(stateFile).Keys)
        {
            if (known.Contains(id))
                continue;
            byte[] html;
            try
            {
                html = notebook.Content(id);
            }
            catch (OneNoteException error) when (error.Status == 404)
            {
                continue;
            }
            var match = TitleTag().Match(Encoding.UTF8.GetString(html));
            if (match.Success)
                found.Add((id, WebUtility.HtmlDecode(match.Groups[1].Value).Trim()));
        }
        return Dates.Sort(found);
    }

    /// <summary>The dated pages OneNote lists, plus any recorded page for a date none of them has.</summary>
    public static IReadOnlyList<PageInfo> DatedPages(IOneNote notebook, string stateFile)
    {
        var listed = notebook.DatedPages();
        var dates = listed.Select(p => p.Date).ToHashSet(StringComparer.Ordinal);
        var extra = Unlisted(notebook, stateFile, listed).Where(p => !dates.Contains(p.Date)).ToList();
        return extra.Count == 0 ? listed : Dates.Sort(listed.Concat(extra).Select(p => (p.Id, (string?)p.Title)));
    }
}
