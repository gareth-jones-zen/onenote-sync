// onenote-sync-parity PARITY_DIR: the .NET side of parity/parity.py. Reads manifest.json (and python.json, for the
// state round trip and lock keys) from the parity folder and writes dotnet.json beside them, in the same shape as
// the Python's results. Everything runs against the fake notebook; nothing touches Graph.
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneNoteSync;
using PyCompat;
using OneNoteSync.Tests;

var parity = args.Length > 0 ? args[^1] : throw new ArgumentException("usage: onenote-sync-parity PARITY_DIR");
using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(parity, "manifest.json")));
var root = manifest.RootElement;
string[] headings = root.GetProperty("headings").EnumerateArray().Select(h => h.GetString()!).ToArray();
const string CreateUpdate = "# Architecture\n- created **here** – £1\n";
var utf8 = new UTF8Encoding(false, true);

var output = new MemoryStream();
using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
{
    json.WriteStartObject();

    // ---- parser dumps
    json.WriteStartObject("dumps");
    foreach (var page in root.GetProperty("pages").EnumerateArray())
    {
        foreach (var key in new[] { "file", "ids" })
        {
            var file = page.GetProperty(key).GetString()!;
            var html = utf8.GetString(File.ReadAllBytes(Path.Combine(parity, file)));
            json.WritePropertyName(file);
            Dump(json, html);
        }
    }
    json.WriteEndObject();

    // ---- markdown
    json.WriteStartObject("markdown");
    foreach (var update in root.GetProperty("updates").EnumerateArray())
    {
        var text = Markdown.ReadText(Path.Combine(parity, update.GetProperty("file").GetString()!));
        var items = Markdown.Bullets(text, "Architecture");
        json.WriteStartObject(update.GetProperty("name").GetString()!);
        json.WriteStartArray("bullets");
        foreach (var item in items)
        {
            json.WriteStartArray();
            json.WriteNumberValue(item.Depth);
            json.WriteStringValue(item.Text);
            json.WriteEndArray();
        }
        json.WriteEndArray();
        json.WriteString("render", Markdown.Render(items));
        json.WriteString("plain", Markdown.Plain(items));
        json.WriteBoolean("draft", text.Contains("[DRAFT]", StringComparison.Ordinal));
        json.WriteEndObject();
    }
    json.WriteEndObject();

    // ---- sprints
    var dated = root.GetProperty("pages").EnumerateArray()
        .Where(p => p.GetProperty("name").GetString() != "synthetic")
        .Select(p => new PageInfo(p.GetProperty("ids").GetString()!, p.GetProperty("title").GetString()!, p.GetProperty("date").GetString()!))
        .OrderBy(p => p.Date, StringComparer.Ordinal).ThenBy(p => p.Title, StringComparer.Ordinal).ToList();
    json.WriteStartObject("sprints");
    foreach (var date in dated.Select(p => p.Date).Concat(["2026-11-16", "2026-11-23"]))
    {
        var earlier = dated.Where(p => string.CompareOrdinal(p.Date, date) < 0).ToList();
        foreach (var weeks in new[] { 2, 1, 3, 0 })
        {
            json.WriteStartArray($"{date}/{weeks}");
            try
            {
                var (number, why) = Sprint.For(date, earlier, weeks);
                json.WriteNumberValue(number);
                json.WriteStringValue(why);
            }
            catch (UsageException error)
            {
                json.WriteStringValue("error");
                json.WriteStringValue(error.Message);
            }
            json.WriteEndArray();
        }
    }
    json.WriteEndObject();

    // ---- posts
    json.WriteStartObject("posts");
    var count = 0;
    foreach (var c in root.GetProperty("cases").EnumerateArray())
    {
        foreach (var update in root.GetProperty("updates").EnumerateArray())
        {
            foreach (var dryRun in new[] { true, false })
            {
                var id = $"{c.GetProperty("id").GetString()}|{update.GetProperty("name").GetString()}|{(dryRun ? "dry" : "yes")}";
                var page = new FakePage("2026-11-09", c.GetProperty("title").GetString()!, "2026-11-09",
                    utf8.GetString(File.ReadAllBytes(Path.Combine(parity, c.GetProperty("html").GetString()!))));
                var state = c.GetProperty("state").ValueKind == JsonValueKind.String ? c.GetProperty("state").GetString() : null;
                RunPost(json, id, [page], c.GetProperty("drops").GetBoolean(), c.GetProperty("force").GetBoolean(), dryRun, state,
                    File.ReadAllBytes(Path.Combine(parity, update.GetProperty("file").GetString()!)), "2026-11-09");
                count++;
            }
        }
    }
    var named = root.GetProperty("pages").EnumerateArray().Where(p => p.GetProperty("name").GetString() != "synthetic")
        .Select(p => (Name: p.GetProperty("name").GetString()!, Info: new PageInfo(p.GetProperty("ids").GetString()!,
            p.GetProperty("title").GetString()!, p.GetProperty("date").GetString()!)))
        .OrderBy(p => p.Info.Date, StringComparer.Ordinal).ThenBy(p => p.Info.Title, StringComparer.Ordinal).ToList();
    foreach (var (name, info) in named.Skip(1))
    {
        foreach (var dryRun in new[] { true, false })
        {
            var pages = named.Where(p => string.CompareOrdinal(p.Info.Date, info.Date) < 0)
                .Select(p => new FakePage(p.Info.Date, p.Info.Title, p.Info.Date, utf8.GetString(File.ReadAllBytes(Path.Combine(parity, p.Info.Id)))))
                .ToList();
            RunPost(json, $"create|{name}|{(dryRun ? "dry" : "yes")}", pages, false, false, dryRun, null, utf8.GetBytes(CreateUpdate), info.Date);
            count++;
        }
    }
    json.WriteEndObject();

    // ---- state: the Python's file, read and rewritten
    using var python = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(parity, "python.json")));
    var stateBytes = Convert.FromHexString(python.RootElement.GetProperty("state").GetString()!);
    var statePath = Path.Combine(Path.GetTempPath(), "onenote-sync-parity-state-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllBytes(statePath, stateBytes);
    json.WriteString("state_rewritten", Convert.ToHexStringLower(StateFile.Bytes(StateFile.Load(statePath))));
    File.Delete(statePath);

    // ---- lock keys
    json.WriteStartObject("keys");
    foreach (var item in python.RootElement.GetProperty("keys").EnumerateObject())
        json.WriteString(item.Name, WinBackground.Locks.Key(PyPath.Resolve(item.Name)));
    json.WriteEndObject();

    json.WriteEndObject();
    Console.WriteLine($"dotnet: {count} posts");
}
File.WriteAllBytes(Path.Combine(parity, "dotnet.json"), output.ToArray());
return 0;

static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

void Dump(Utf8JsonWriter json, string html)
{
    json.WriteStartObject();
    json.WriteStartArray("blocks");
    foreach (var block in PageHtml.Blocks(html))
    {
        json.WriteStartArray();
        json.WriteStringValue(block.Tag);
        if (block.Id is null) json.WriteNullValue(); else json.WriteStringValue(block.Id);
        json.WriteStringValue(Sha(block.Html));
        json.WriteStringValue(PageHtml.Text(block.Html));
        json.WriteBooleanValue(PageHtml.IsHeading(block.Html));
        json.WriteEndArray();
    }
    json.WriteEndArray();
    var found = PageHtml.Headings(html);
    json.WriteStartArray("headings");
    foreach (var heading in found)
        json.WriteStringValue(heading);
    json.WriteEndArray();
    json.WriteStartArray("sections");
    foreach (var name in found.Concat(headings).Concat(["architecture", "Missing"]).Distinct(StringComparer.Ordinal))
    {
        var (head, under) = PageHtml.Section(html, name);
        json.WriteStartArray();
        json.WriteStringValue(name);
        if (head is null) json.WriteNullValue(); else json.WriteStringValue(Sha(head.Html));
        json.WriteStartArray();
        foreach (var block in under)
            json.WriteStringValue(Sha(block.Html));
        json.WriteEndArray();
        json.WriteStringValue(Sha(Post.Outside(html, name)));
        json.WriteEndArray();
    }
    json.WriteEndArray();
    json.WriteEndObject();
}

void RunPost(Utf8JsonWriter json, string id, List<FakePage> pages, bool drops, bool force, bool dryRun, string? state, byte[] markdown, string week)
{
    var temp = PyPath.Resolve(Path.Combine(Path.GetTempPath(), "onenote-sync-parity-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(temp);
    try
    {
        File.WriteAllBytes(Path.Combine(temp, "config.toml"), File.ReadAllBytes(Path.Combine(parity, "settings.toml")));
        var config = Config.Load(Path.Combine(temp, "config.toml"));
        if (state is not null)
            File.WriteAllText(Path.Combine(temp, "state.json"), state, new UTF8Encoding(false));
        var md = Path.Combine(temp, $"{week}.md");
        File.WriteAllBytes(md, markdown);
        var notebook = new FakeNotebook(pages, drops);
        var lines = new List<string>();
        string[] outcome;
        PostResult? result = null;
        try
        {
            result = Post.Run(config, notebook, md, null, dryRun, force, lines.Add);
            outcome = [];
        }
        catch (RefusedException error) { outcome = ["refused", error.Message]; }
        catch (UsageException error) { outcome = ["usage", error.Message]; }
        catch (PostFailedException error) { outcome = ["failed", error.Message]; }
        catch (Exception error) { outcome = ["error", error.GetType().Name, error.Message]; }

        json.WriteStartObject(id);
        json.WriteStartArray("outcome");
        if (result is not null)
        {
            json.WriteStringValue("result");
            json.WriteStartObject();
            json.WriteString("page", result.Page);
            json.WriteBoolean("created", result.Created);
            json.WriteBoolean("posted", result.Posted);
            json.WriteEndObject();
        }
        foreach (var part in outcome)
            json.WriteStringValue(Normalise(part, temp));
        json.WriteEndArray();
        json.WriteStartArray("log");
        foreach (var line in lines)
            json.WriteStringValue(Normalise(line, temp));
        json.WriteEndArray();
        json.WriteStartArray("patches");
        foreach (var body in notebook.PatchBodies)
            json.WriteStringValue(body);
        json.WriteEndArray();
        json.WriteStartArray("created");
        foreach (var html in notebook.Created)
            json.WriteStringValue(html);
        json.WriteEndArray();
        json.WriteStartArray("pages");
        foreach (var page in notebook.Pages)
            json.WriteStringValue(Sha(page.Html));
        json.WriteEndArray();
        var stateFile = Path.Combine(temp, "state.json");
        json.WritePropertyName("state");
        if (File.Exists(stateFile))
            JsonDocument.Parse(File.ReadAllBytes(stateFile)).RootElement.WriteTo(json);
        else
            json.WriteNullValue();
        json.WriteString("state_hex", File.Exists(stateFile) ? Convert.ToHexStringLower(File.ReadAllBytes(stateFile)) : null);
        var backups = Path.Combine(temp, "backups");
        json.WriteNumber("backups", Directory.Exists(backups) ? Directory.GetFiles(backups, "*.html").Length : 0);
        json.WriteEndObject();
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static string Normalise(string text, string temp) =>
    Regex.Replace(text.Replace(temp, "<TMP>"), @"\d{4}-\d{2}-\d{2}-\d{8}-\d{6}\.html", "<BACKUP>");
