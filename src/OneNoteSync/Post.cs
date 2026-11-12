using System.Text.RegularExpressions;
using OneNoteSync.Interim;

namespace OneNoteSync;

/// <summary>post stopped before changing anything; the message says why and what to do. Exit 1.</summary>
public sealed class RefusedException(string message) : Exception(message);

/// <summary>post changed the page but couldn't confirm it (Python's RuntimeError): the message says where the
/// backup is.</summary>
public sealed class PostFailedException(string message) : Exception(message);

public sealed record PostResult(string Page, bool Created, bool Posted);

/// <summary>post, in order, stopping at the first problem:
/// 1. Refuses a markdown file still marked [DRAFT].
/// 2. Finds the week's page by the date (the file name's, or --date). If there's none, creates it (GJ, 10 Nov
///    2026): titled "yyyy-mm-dd – Sprint N", with N worked out from the last pages (sprints run sprint_weeks
///    weeks), and the [post] headings.
/// 3. Finds your heading and what's under it. Only one bulleted list, or nothing, can be replaced; anything more
///    (Highlights/Challenges, a table) is left for a person.
/// 4. Stops if the section was changed by hand since this tool last posted to it (or holds anything, if it never
///    has), showing what's there. force replaces it anyway.
/// 5. Saves the page's HTML to the backups folder (the exact bytes as fetched), then replaces only that list.
/// 6. Reads the page back: your section must read as posted, and every other section as it was.</summary>
public static partial class Post
{
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex DateInName();

    [GeneratedRegex(@"[\s\x1c-\x1f](id|data-id)=""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex IdAttributes();

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public static string WeekOf(string markdownPath, string? date)
    {
        if (!string.IsNullOrEmpty(date))
            return Sprint.ParseDate(date).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var name = Path.GetFileName(markdownPath);
        var match = DateInName().Match(name);
        if (!match.Success)
            throw new RefusedException($"{name} has no date in its name; pass --date yyyy-mm-dd.");
        return match.Value;
    }

    /// <summary>Every section's text except this one, to check a post touched nothing else.</summary>
    internal static string Outside(string html, string section)
    {
        var (_, under) = PageHtml.Section(html, section);
        var mine = under.Select(b => b.Html).ToHashSet(StringComparer.Ordinal);
        return string.Join("\n", PageHtml.Blocks(html).Where(b => !mine.Contains(b.Html)).Select(b => PageHtml.Text(b.Html)));
    }

    private static string Diff(string a, string b, string from, string to) =>
        string.Join("\n", UnifiedDiff.Lines(a.Split('\n'), b.Split('\n'), from, to, 0));

    public static PostResult Run(Config config, IOneNote notebook, string markdownPath, string? date = null, bool dryRun = true,
        bool force = false, Action<string>? log = null, Func<DateTime>? clock = null)
    {
        log ??= Console.WriteLine;
        clock ??= () => DateTime.Now;
        var settings = config.Post ?? throw new UsageException($"{config.Path} has no [post] section.");
        var name = settings.Section;
        var fileName = Path.GetFileName(markdownPath);
        var markdown = Markdown.ReadText(markdownPath);
        if (markdown.Contains("[DRAFT]", StringComparison.Ordinal))
            throw new RefusedException($"{fileName} is still marked [DRAFT]. Review it and remove the marker first.");
        var items = Markdown.Bullets(markdown, name);
        if (items.Count == 0)
            throw new RefusedException($"No bullets under a '# {name}' heading in {fileName}.");
        var week = WeekOf(markdownPath, date);
        var newText = Markdown.Plain(items);

        var found = notebook.PageFor(week);
        var created = false;
        if (found is null)
        {
            var earlier = notebook.DatedPages().Where(p => string.CompareOrdinal(p.Date, week) < 0).ToList();
            var (number, why) = Sprint.For(week, earlier, settings.SprintWeeks);
            var title = Sprint.Title(week, number);
            log($"No page for {week} yet: {(dryRun ? "would create" : "creating")} '{title}' ({why}), "
                + $"with headings {string.Join(", ", settings.Headings)}");
            if (dryRun)
            {
                log($"\n{name}, as it would be posted:\n{newText}");
                return new PostResult(title, false, false);
            }
            string id;
            try
            {
                id = notebook.Create(Markdown.NewPage(title, settings.Headings));
            }
            catch (DeliveryUnknownException error)
            {
                throw new PostFailedException(
                    $"Creating '{title}' got no answer, so it wasn't sent again: a second try could make the page twice. "
                    + $"Look in the section in OneNote: if the page is there, run this again and it will be found; if not, "
                    + $"run it again to create it. ({error.Message})");
            }
            found = new PageInfo(id, title, week);
            created = true;
            log($"created '{title}'");
        }

        var state = StateFile.Load(config.StateFile);
        var beforeBytes = notebook.Content(found.Id, ids: true, isNew: created);
        var before = StrictUtf8.GetString(beforeBytes);
        var (head, under) = PageHtml.Section(before, name);
        if (head is null)
            throw new RefusedException($"'{found.Title}' has no '{name}' heading. Its headings: {string.Join(", ", PageHtml.Headings(before))}.");
        var lists = under.Where(b => b.Tag is "ul" or "ol").ToList();
        var other = under.Where(b => b.Tag is not ("ul" or "ol") && PageHtml.Text(b.Html).Length > 0).ToList();
        if (lists.Count > 1 || other.Count > 0)
            throw new RefusedException($"'{name}' on '{found.Title}' holds more than one list ({lists.Count} lists, {other.Count} other "
                                       + "blocks), so it's left for you to edit in OneNote.");
        var current = lists.Count > 0 ? PageHtml.Text(lists[0].Html) : "";
        var last = state.TryGetValue(found.Id, out var sections) && sections.TryGetValue(name, out var posted) ? posted : null;
        if (current == newText)
        {
            log($"'{name}' on '{found.Title}' already reads as posted; nothing to do.");
            return new PostResult(found.Title, created, false);
        }
        var changedByHand = current.Length > 0 && current != last;
        var diff = Diff(current, newText, "on the page now", "to post");
        if (changedByHand && !force)
            throw new RefusedException($"'{name}' on '{found.Title}' "
                                       + (last is null ? "has content this tool didn't post" : "was changed by hand since the last post")
                                       + $". Here's the difference; run again with --force to replace it:\n{diff}");
        if (dryRun)
        {
            log($"Would replace '{name}' on '{found.Title}':\n{(diff.Length > 0 ? diff : newText)}");
            return new PostResult(found.Title, false, false);
        }

        Directory.CreateDirectory(config.Backups);
        var backup = PyPath.Pure(Path.Combine(config.Backups, $"{week}-{clock():yyyyMMdd-HHmmss}.html"));
        File.WriteAllBytes(backup, beforeBytes);   // the exact bytes (the Python's text mode wrote CR CR LF)
        var content = Markdown.Render(items);
        var change = lists.Count > 0
            ? PatchChange.Replace(lists[0].Id, content)
            : PatchChange.InsertAfter(head.Id, content);
        Patch(notebook, found.Id, change, log);

        var after = StrictUtf8.GetString(notebook.Content(found.Id, ids: true));
        if (PageHtml.Section(after, name).Heading is null)
        {
            // OneNote sometimes keeps a heading inside the list that follows it, so replacing the list takes the
            // heading too (found on a real weekly page, 10 Nov 2026; not every page does it). Put it back.
            var newList = PageHtml.Blocks(after).FirstOrDefault(b => b.Tag is "ul" or "ol" && PageHtml.Text(b.Html) == newText)
                ?? throw new PostFailedException($"Posted, but the '{name}' heading went and the new list can't be found to put it back. "
                                                 + $"The page before the post is in {backup}.");
            var heading = IdAttributes().Replace(head.Html, "");
            Patch(notebook, found.Id, PatchChange.InsertBefore(newList.Id, heading), log);
            after = StrictUtf8.GetString(notebook.Content(found.Id, ids: true));
            log($"OneNote dropped the '{name}' heading with the old list; put it back");
        }
        var (_, nowUnder) = PageHtml.Section(after, name);
        var readBack = PageHtml.Text(string.Concat(nowUnder.Select(b => b.Html)));
        if (readBack != newText)
            throw new PostFailedException($"Posted, but '{name}' reads back differently. The page before the post is in {backup}.\n"
                                          + Diff(newText, readBack, "meant", "on the page"));
        if (Outside(after, name) != Outside(before, name))
            throw new PostFailedException($"Posted, but another section of '{found.Title}' changed at the same time (perhaps someone "
                                          + $"was editing). Check the page; the version before the post is in {backup}.");
        if (!state.TryGetValue(found.Id, out var entry))
            state[found.Id] = entry = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        entry[name] = newText;
        StateFile.Save(config.StateFile, state);
        log($"posted '{name}' to '{found.Title}': {items.Count} bullets (backup: {Path.GetFileName(backup)})");
        return new PostResult(found.Title, created, true);
    }

    /// <summary>One PATCH. If it got no answer it isn't sent again; the read-back that follows decides whether it
    /// landed (and stops, with the backup's name, if it didn't, or landed twice).</summary>
    private static void Patch(IOneNote notebook, string pageId, PatchChange change, Action<string> log)
    {
        try
        {
            notebook.Patch(pageId, [change]);
        }
        catch (DeliveryUnknownException error)
        {
            log($"{error.Message}; reading the page back to see whether it was applied");
        }
    }
}
