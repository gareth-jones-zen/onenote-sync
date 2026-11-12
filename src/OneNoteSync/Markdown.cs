using System.Text;
using System.Text.RegularExpressions;
using OneNoteSync.Interim;

namespace OneNoteSync;

/// <summary>One bullet: how deeply it's nested (0 to 3) and its text.</summary>
public readonly record struct Bullet(int Depth, string Text);

/// <summary>Markdown in, OneNote HTML out: only what an update needs. Bullets ("- "), **bold**, *italics*, and a
/// nested bullet indented two or more spaces. The styles copy what the pages already use, so a posted section
/// looks like a typed one.</summary>
public static partial class Markdown
{
    public const string TextStyle = "font-size:11.5pt;color:black";
    public const string BoldStyle = "font-size:11.5pt;color:black;font-weight:bold";

    private const string TitleHeadingStart =
        "<p lang=\"en-GB\" style=\"margin-top:0pt;margin-bottom:0pt\"><span style=\"font-family:Aptos;font-size:18pt;color:black;font-weight:bold;text-decoration:underline\">";
    private const string TeamHeadingStart =
        "<p lang=\"en-GB\" style=\"margin-top:0pt;margin-bottom:0pt\"><span style=\"font-size:14pt;color:black;font-weight:bold\">";
    private const string HeadingEnd = "</span></p>";

    [GeneratedRegex(@"^#+[\s\x1c-\x1f]", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^([\s\x1c-\x1f]*)[-*][\s\x1c-\x1f]+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"(\*\*[^*]+\*\*|(?<!\*)\*[^*\s\x1c-\x1f][^*]*\*(?!\*))", RegexOptions.CultureInvariant)]
    private static partial Regex Emphasis();

    /// <summary>Text as Python's read_text() gave it: strict UTF-8 (a byte order mark is kept, as Python's "utf-8"
    /// codec keeps it) and universal newlines ("\r\n" and a lone "\r" become "\n").</summary>
    public static string ReadText(string path) =>
        new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>The bullets under "# sectionName" (or the first heading, if none has that name). Stops at the next
    /// heading or a --- rule, so a draft's notes underneath are never posted.</summary>
    public static List<Bullet> Bullets(string markdown, string sectionName)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && Py.Strip(lines[0]) == "---")   // frontmatter
        {
            var end = 0;
            for (var i = 1; i < lines.Count; i++)
            {
                if (Py.Strip(lines[i]) == "---")
                {
                    end = i;
                    break;
                }
            }
            lines = lines.Skip(end + 1).ToList();
        }
        var start = lines.FindIndex(l => HeadingLine().IsMatch(l) && Py.Lower(Py.Strip(Py.LStrip(l, "#"))) == Py.Lower(sectionName));
        if (start < 0)
            start = lines.FindIndex(l => HeadingLine().IsMatch(l));
        var found = new List<Bullet>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (HeadingLine().IsMatch(line) || Py.Strip(line) == "---")
                break;
            var match = BulletLine().Match(line);
            if (match.Success)
                found.Add(new Bullet(Math.Min(Py.Len(Py.ExpandTabs(match.Groups[1].Value, 4)) / 2, 3), Py.Strip(match.Groups[2].Value)));
            else if (Py.Strip(line).Length > 0 && found.Count > 0)   // a wrapped continuation of the bullet above
                found[^1] = found[^1] with { Text = found[^1].Text + " " + Py.Strip(line) };
        }
        return found;
    }

    /// <summary>**bold** and *italic* to OneNote spans; everything else as plain, escaped text.</summary>
    public static string Inline(string markdown)
    {
        var output = new StringBuilder();
        foreach (var part in Emphasis().Split(markdown))
        {
            if (part.Length == 0)
                continue;
            if (part.StartsWith("**", StringComparison.Ordinal))
                output.Append($"<span style=\"{BoldStyle}\">{Py.HtmlEscape(PySlice(part, 2, -2))}</span>");
            else if (part.StartsWith('*') && part.EndsWith('*') && part.Length > 2)
                output.Append($"<span style=\"{TextStyle};font-style:italic\">{Py.HtmlEscape(part[1..^1])}</span>");
            else
                output.Append($"<span style=\"{TextStyle}\">{Py.HtmlEscape(part)}</span>");
        }
        return output.ToString();
    }

    /// <summary>part[2:-2] with Python's clamping ("**" or "**x" give "").</summary>
    private static string PySlice(string text, int start, int fromEnd)
    {
        var stop = text.Length + fromEnd;
        return stop <= start ? "" : text[start..stop];
    }

    /// <summary>A &lt;ul&gt; for the bullets, with nested lists for deeper bullets.</summary>
    public static string Render(IReadOnlyList<Bullet> items)
    {
        var html = new StringBuilder("<ul>");
        var depth = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var (level, line) = items[i];
            while (depth < level)
            {
                html.Append("<ul>");
                depth++;
            }
            while (depth > level)
            {
                html.Append("</ul></li>");
                depth--;
            }
            var next = i + 1 < items.Count ? items[i + 1].Depth : 0;
            html.Append("<li><span lang=\"en-GB\">").Append(Inline(line)).Append("</span>").Append(next > level ? "" : "</li>");
        }
        while (depth > 0)
        {
            html.Append("</ul></li>");
            depth--;
        }
        return html.Append("</ul>").ToString();
    }

    /// <summary>The text a rendered list will read as, for comparing with what's on the page.</summary>
    public static string Plain(IReadOnlyList<Bullet> items) => PageHtml.Text(Render(items));

    /// <summary>A new week's page: the title, then each heading with a blank line under it. The first heading is the
    /// page-level one (Executive Summary); the rest are teams' sections. "\n" line endings, as Python built it.</summary>
    public static string NewPage(string title, IReadOnlyList<string> names)
    {
        var body = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            body.Add((i == 0 ? TitleHeadingStart : TeamHeadingStart) + Py.HtmlEscape(names[i]) + HeadingEnd);
            body.Add("<br />");
        }
        return "<!DOCTYPE html>\n<html>\n<head>\n<title>" + Py.HtmlEscape(title) + "</title>\n"
            + "</head>\n<body data-absolute-enabled=\"true\" style=\"font-family:Calibri;font-size:11pt\">\n"
            + "<div style=\"position:absolute;left:48px;top:115px;width:967px\">\n" + string.Join("\n", body) + "\n</div>\n</body>\n</html>";
    }
}
