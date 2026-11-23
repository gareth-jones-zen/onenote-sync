using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace OneNoteSync;

/// <summary>What was last posted to each page: {page id: {section: posted text}}. Written exactly as the Python
/// wrote it (json.dumps with indent=2 and ensure_ascii=False, in text mode, so with CRLF), so a Python and a .NET
/// onenote-sync can take turns with one file. Every other page's entries are kept as they are.</summary>
public static class StateFile
{
    public static OrderedDictionary<string, OrderedDictionary<string, string>> Load(string path)
    {
        string text;
        try
        {
            // read_text(): strict UTF-8, universal newlines (a byte order mark is kept, and then fails to parse,
            // as Python's json.loads refused one).
            text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new();
        }
        if (text.StartsWith('﻿'))
            throw new UsageException($"{path}: Unexpected UTF-8 BOM (decode using utf-8-sig)");
        var state = new OrderedDictionary<string, OrderedDictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new UsageException($"{path} isn't a JSON object.");
            foreach (var page in document.RootElement.EnumerateObject())
            {
                if (page.Value.ValueKind != JsonValueKind.Object)
                    throw new UsageException($"{path}: page {page.Name} isn't an object of sections.");
                var sections = new OrderedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in page.Value.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String)
                        throw new UsageException($"{path}: {page.Name} / {entry.Name} isn't text.");
                    sections[entry.Name] = entry.Value.GetString()!;   // a repeated key: the last wins, as in Python
                }
                state[page.Name] = sections;
            }
        }
        catch (JsonException error)
        {
            throw new UsageException($"{path}: {error.Message}");
        }
        return state;
    }

    /// <summary>The file's bytes for this state, as Python wrote them.</summary>
    public static byte[] Bytes(OrderedDictionary<string, OrderedDictionary<string, string>> state) =>
        new UTF8Encoding(false).GetBytes(PyStr.WindowsNewlines(PyJson.Dump(ToJson(state), 2, ensureAscii: false)));

    private static JsonObject ToJson(OrderedDictionary<string, OrderedDictionary<string, string>> state)
    {
        var json = new JsonObject();
        foreach (var (page, sections) in state)
        {
            var entries = new JsonObject();
            foreach (var (section, text) in sections)
                entries[section] = text;
            json[page] = entries;
        }
        return json;
    }

    /// <summary>Replace the file through a .tmp beside it, so a crash never leaves half a file.</summary>
    public static void Save(string path, OrderedDictionary<string, OrderedDictionary<string, string>> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Bytes(state));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Weeks whose page post tried to create but couldn't confirm (no answer, or a 5xx): {week: when}, in a
/// file beside the state file ("state.json" → "state.unconfirmed-pages.json"). A page made through Graph is listed
/// untitled until someone opens it, and its id never came back, so a later post couldn't find it; while its week is
/// here, post stops rather than create the page a second time. Cleared when the week's page is found or created.</summary>
public static class Unconfirmed
{
    public static string PathFor(string stateFile) => Path.ChangeExtension(stateFile, ".unconfirmed-pages.json");

    public static SortedDictionary<string, string> Load(string stateFile)
    {
        var path = PathFor(stateFile);
        var weeks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return weeks;
        }
        try
        {
            if (JsonNode.Parse(text) is not JsonObject json)
                throw new UsageException($"{path} isn't a JSON object of weeks.");
            foreach (var (week, when) in json)
                weeks[week] = when is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
        }
        catch (JsonException error)
        {
            throw new UsageException($"{path}: {error.Message}");
        }
        return weeks;
    }

    /// <summary>Write the weeks, or remove the file when there are none.</summary>
    public static void Save(string stateFile, SortedDictionary<string, string> weeks)
    {
        var path = PathFor(stateFile);
        if (weeks.Count == 0)
        {
            File.Delete(path);
            return;
        }
        var json = new JsonObject();
        foreach (var (week, when) in weeks)
            json[week] = when;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
