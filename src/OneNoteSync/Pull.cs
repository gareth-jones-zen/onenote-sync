using OneNoteSync.Interim;

namespace OneNoteSync;

public sealed record PullResult(IReadOnlyList<string> Written, int Unchanged);

/// <summary>Pull writes each of the newest pages' HTML (as OneNote serves it, without element ids) to [pull] file,
/// only when it differs from what's there, so a watcher polling every 15 minutes writes nothing on a quiet day. The
/// bytes are never decoded: they're exactly what Power Automate's OneNote connector saved (checked 10 Nov 2026),
/// so anything converting them carries on unchanged.</summary>
public static class Pull
{
    public static PullResult Run(Config config, IOneNote notebook, bool dryRun = false, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var settings = config.Pull ?? throw new UsageException($"{config.Path} has no [pull] section.");
        var pages = Py.From(notebook.DatedPages(), -settings.Newest);
        var written = new List<string>();
        var unchanged = 0;
        foreach (var item in pages)
        {
            var target = PyPath.Pure(Path.Combine(settings.Folder, FileName(settings.File, item.Date)));
            var html = notebook.Content(item.Id);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(html))
            {
                unchanged++;
                continue;
            }
            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".tmp";
                File.WriteAllBytes(temp, html);
                File.Move(temp, target, overwrite: true);
            }
            written.Add(target);
            log($"{(dryRun ? "would write" : "wrote")}  {Path.GetFileName(target)} ({item.Title})");
        }
        log($"pull: {written.Count} written, {unchanged} unchanged");
        return new PullResult(written, unchanged);
    }

    /// <summary>file.format(date=date): "{date}" filled in, "{{" and "}}" for a literal brace, and any other field
    /// an error, as Python's str.format raised one.</summary>
    public static string FileName(string template, string date)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                result.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                result.Append('}');
                i++;
            }
            else if (c == '{')
            {
                var close = template.IndexOf('}', i);
                var field = close < 0 ? null : template[(i + 1)..close];
                if (field != "date")
                    throw new UsageException($"[pull] file {Py.Repr(template)} can only use {{date}}.");
                result.Append(date);
                i = close;
            }
            else if (c == '}')
                throw new UsageException("Single '}' encountered in format string");
            else
                result.Append(c);
        }
        return result.ToString();
    }
}
