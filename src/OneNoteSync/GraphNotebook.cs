using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M365Graph;
using OneNoteSync.Interim;

namespace OneNoteSync;

/// <summary>The OneNote calls, through Microsoft Graph.
///
/// Two things about OneNote's API shaped this (found on a real weekly-update notebook, 10 Nov 2026):
/// - A page's lastModifiedDateTime can't be trusted. Pages copied from a template keep the template's: 2026 pages
///   reported 18 Nov 2024. So nothing here decides by date; pull compares content.
/// - Pages are found by the date their title starts with, not by createdDateTime, which put an untitled page and a
///   May page first. Untitled pages and a "TEMPLATE" page are ignored.
///
/// Page content isn't JSON, so it's fetched (and new pages sent) as raw bytes with the client's token.</summary>
public sealed class GraphNotebook : IOneNote
{
    public static readonly string[] Scopes = ["Notes.ReadWrite.All"];

    private readonly Func<string, IAsyncEnumerable<JsonNode?>> pages;
    private readonly RawGraph raw;
    private readonly Func<TimeSpan, Task> delay;
    private readonly string root;
    private readonly string notebookName, sectionName;
    private string? section;

    /// <summary>owner is an email, or "me".</summary>
    public GraphNotebook(GraphClient graph, string owner, string notebook, string sectionName)
        : this(url => graph.PagesAsync(url, Scopes),
               new RawGraph(cancel => graph.TokenAsync(Scopes, null, cancel), RawGraph.CreateHttpClient()),
               owner, notebook, sectionName, null)
    {
        Graph = graph;
    }

    /// <summary>For tests: the paged GETs, the raw requests and the waits, all fakeable.</summary>
    internal GraphNotebook(Func<string, IAsyncEnumerable<JsonNode?>> pages, RawGraph raw, string owner, string notebook,
        string sectionName, Func<TimeSpan, Task>? delay)
    {
        this.pages = pages;
        this.raw = raw;
        this.delay = delay ?? (wait => Task.Delay(wait));
        root = owner == "me" ? "/me/onenote" : $"/users/{Quote(owner)}/onenote";
        notebookName = notebook;
        this.sectionName = sectionName;
    }

    public GraphClient? Graph { get; }

    /// <summary>urllib.parse.quote(text): UTF-8, everything but letters, digits, "_.-~" and "/" as %XX.</summary>
    internal static string Quote(string text)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~' or (byte)'/')
                result.Append((char)b);
            else
                result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private List<JsonNode?> All(string url) => Collect(pages(url)).GetAwaiter().GetResult();

    private static async Task<List<JsonNode?>> Collect(IAsyncEnumerable<JsonNode?> items)
    {
        var list = new List<JsonNode?>();
        await foreach (var item in items.ConfigureAwait(false))
            list.Add(item);
        return list;
    }

    private static string Name(JsonNode? item) => (string?)item?["displayName"] ?? "";

    public string SectionId()
    {
        if (section is null)
        {
            var notebooks = All($"{root}/notebooks?$select=id,displayName");
            var notebook = notebooks.FirstOrDefault(n => Py.Lower(Name(n)) == Py.Lower(notebookName))
                ?? throw new UsageException($"No notebook called {Py.Repr(notebookName)} (found: {string.Join(", ", notebooks.Select(Name))})");
            var sections = All($"{root}/notebooks/{(string?)notebook["id"]}/sections?$select=id,displayName");
            var found = sections.FirstOrDefault(s => Py.Lower(Name(s)) == Py.Lower(sectionName))
                ?? throw new UsageException($"No section called {Py.Repr(sectionName)} in {Py.Repr(notebookName)}");
            section = (string?)found["id"] ?? "";
        }
        return section;
    }

    public IReadOnlyList<PageInfo> DatedPages() =>
        Dates.Sort(All($"{root}/sections/{SectionId()}/pages?$select=id,title&$top=100")
            .Select(p => ((string?)p?["id"] ?? "", (string?)p?["title"])));

    public byte[] Content(string pageId, bool ids = false, bool isNew = false)
    {
        var url = $"{root}/pages/{pageId}/content" + (ids ? "?includeIDs=true" : "");
        var tries = isNew ? 12 : 1;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return raw.SendAsync(HttpMethod.Get, url, null, null, RetryPolicy.Get).GetAwaiter().GetResult();
            }
            catch (OneNoteException error) when (isNew && error.Status == 404 && attempt < tries - 1)
            {
                delay(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
    }

    public void Patch(string pageId, IReadOnlyList<PatchChange> changes) =>
        raw.SendAsync(HttpMethod.Patch, $"{root}/pages/{pageId}/content", Encoding.UTF8.GetBytes(PatchChange.Body(changes)),
            "application/json", RetryPolicy.Write).GetAwaiter().GetResult();

    public string Create(string html)
    {
        var body = raw.SendAsync(HttpMethod.Post, $"{root}/sections/{SectionId()}/pages", Encoding.UTF8.GetBytes(html), "text/html",
            RetryPolicy.Write).GetAwaiter().GetResult();
        using var reply = JsonDocument.Parse(body);
        return reply.RootElement.GetProperty("id").GetString() ?? "";
    }
}
