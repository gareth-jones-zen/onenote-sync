using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace OneNoteSync;

/// <summary>The OneNote calls, through Microsoft Graph.
///
/// Two things about OneNote's API shaped this (found on a real weekly-update notebook, 10 Nov 2026):
/// - A page's lastModifiedDateTime can't be trusted. Pages copied from a template keep the template's: 2026 pages
///   reported 18 Nov 2024. So nothing here decides by date; pull compares content.
/// - Pages are found by the date their title starts with, not by createdDateTime, which put an untitled page and a
///   May page first. Untitled pages and a "TEMPLATE" page are ignored.
///
/// Page content isn't JSON, so it's fetched (and new pages sent) as raw bytes, through M365Graph's SendRawAsync with a
/// retry policy per call: a GET is retried, a PATCH or POST only when it certainly wasn't applied.</summary>
public sealed class GraphNotebook : IOneNote
{
    public static readonly string[] Scopes = ["Notes.ReadWrite.All"];

    /// <summary>Five tries in all, as the Python's _raw made.</summary>
    private static readonly GraphRetryPolicy ReadPolicy = GraphRetryPolicy.Read with { Tries = 5 };
    private static readonly GraphRetryPolicy WritePolicy = GraphRetryPolicy.Write with { Tries = 5 };

    private readonly Func<string, IAsyncEnumerable<JsonNode?>> pages;
    private readonly GraphClient raw;
    private readonly Func<TimeSpan, Task> delay;
    private readonly string root;
    private readonly string notebookName, sectionName;
    private string? section;

    /// <summary>owner is an email, or "me".</summary>
    public GraphNotebook(GraphClient graph, string owner, string notebook, string sectionName)
        : this(url => graph.PagesAsync(url, Scopes), graph, owner, notebook, sectionName, null)
    {
        Graph = graph;
    }

    /// <summary>For tests: the paged GETs, the raw requests (a GraphClient over a fake handler) and the waits, all
    /// fakeable.</summary>
    internal GraphNotebook(Func<string, IAsyncEnumerable<JsonNode?>> pages, GraphClient raw, string owner, string notebook,
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
            var notebook = notebooks.FirstOrDefault(n => PyStr.Lower(Name(n)) == PyStr.Lower(notebookName))
                ?? throw new UsageException($"No notebook called {Py.ReprString(notebookName)} (found: {string.Join(", ", notebooks.Select(Name))})");
            var sections = All($"{root}/notebooks/{(string?)notebook["id"]}/sections?$select=id,displayName");
            var found = sections.FirstOrDefault(s => PyStr.Lower(Name(s)) == PyStr.Lower(sectionName))
                ?? throw new UsageException($"No section called {Py.ReprString(sectionName)} in {Py.ReprString(notebookName)}");
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
                return Send(HttpMethod.Get, url, null, null);
            }
            catch (OneNoteException error) when (isNew && error.Status == 404 && attempt < tries - 1)
            {
                delay(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
    }

    public void Patch(string pageId, IReadOnlyList<PatchChange> changes) =>
        Send(HttpMethod.Patch, $"{root}/pages/{pageId}/content", Encoding.UTF8.GetBytes(PatchChange.Body(changes)), "application/json");

    public string Create(string html)
    {
        var body = Send(HttpMethod.Post, $"{root}/sections/{SectionId()}/pages", Encoding.UTF8.GetBytes(html), "text/html");
        using var reply = JsonDocument.Parse(body);
        return reply.RootElement.GetProperty("id").GetString() ?? "";
    }

    /// <summary>Send, and return the reply's bytes. url is relative to Graph's v1.0 root ("/me/onenote/..."), as
    /// error messages quote it. A GET (no body) is retried as Read; a PATCH or POST as Write. Throws OneNoteException
    /// for an error answer, DeliveryUnknownException when a write may have been applied but got no answer (or a 5xx,
    /// which isn't sent again), or the network error itself for a GET that kept failing.</summary>
    private byte[] Send(HttpMethod method, string url, byte[]? body, string? contentType)
    {
        var write = method != HttpMethod.Get;
        GraphRawResponse reply;
        try
        {
            reply = raw.SendRawAsync(method, url, body, contentType, write ? WritePolicy : ReadPolicy, Scopes).GetAwaiter().GetResult();
        }
        catch (GraphDeliveryUnknownException error)
        {
            throw new DeliveryUnknownException(error.Message, error);
        }
        if (reply.IsSuccess)
            return reply.Body;
        var status = (int)reply.Status;
        var detail = PyStr.Head(Encoding.UTF8.GetString(reply.Body), 300);
        var message = $"OneNote said {status} to {method.Method} {(url.Length > 80 ? url[..80] : url)}: {detail}";
        if (write && status >= 500)
            throw new DeliveryUnknownException($"{message} (not sent again: it may have been applied)");
        throw new OneNoteException(status, message);
    }
}
