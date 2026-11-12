using System.Text;
using System.Text.Json;
using OneNoteSync.Interim;

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
        new UTF8Encoding(false).GetBytes(Py.WindowsLines(PyJson.NestedIndented(state)));

    /// <summary>Replace the file through a .tmp beside it, so a crash never leaves half a file.</summary>
    public static void Save(string path, OrderedDictionary<string, OrderedDictionary<string, string>> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Bytes(state));
        File.Move(temp, path, overwrite: true);
    }
}
