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
