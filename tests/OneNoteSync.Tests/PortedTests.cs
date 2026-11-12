using static OneNoteSync.Tests.Fixtures;

namespace OneNoteSync.Tests;

// The Python's 14 tests (tests/test_sync.py), with the same names.

public class PageTests
{
    [Fact]
    public void test_finds_sections_and_their_blocks()
    {
        var html = Page("t", "<ul id=\"ul:9\"><li>Old</li></ul>");
        Assert.Equal(["Executive Summary", "Digital", "Fulfilment", "Architecture"], PageHtml.Headings(html));
        var (_, under) = PageHtml.Section(html, "fulfilment");
        Assert.Equal(["p", "ul"], under.Select(b => b.Tag));   // Highlights: isn't a heading
        Assert.Equal("ul:9", PageHtml.Section(html, "Architecture").Under[0].Id);
    }

    [Fact]
    public void test_bullets_stop_at_the_rule_and_keep_nesting()
    {
        var items = Markdown.Bullets(Update, "Architecture");
        Assert.Equal([0, 1, 0], items.Select(i => i.Depth));
        Assert.DoesNotContain("never posted", Markdown.Plain(items));
    }

    [Fact]
    public void test_render_reads_back_as_the_markdown_says()
    {
        var rendered = Markdown.Render(Markdown.Bullets(Update, "Architecture"));
        Assert.Contains("font-weight:bold\">Registry migration</span>", rendered);
        Assert.Contains("font-style:italic\">Team A</span>", rendered);
        Assert.Contains("very low &amp; being fixed", rendered);
        Assert.Equal(2, rendered.Split("<ul>").Length - 1);
        Assert.Equal("supplier cut-over 09/02/27", PageHtml.Text(rendered).Split('\n')[1]);
    }
}

public class SprintTests
{
    private static PageInfo P(string date, string title) => new(date, title, date);

    [Fact]
    public void test_two_week_sprints()
    {
        var both = new[] { P("2026-11-02", "2026-11-02 – Sprint 3"), P("2026-11-09", "2026-11-09 – Sprint 3") };
        Assert.Equal(4, Sprint.For("2026-11-16", both).Number);
        var first = new[] { P("2026-10-26", "2026-10-26 – Sprint 2"), P("2026-11-02", "2026-11-02 – Sprint 3") };
        Assert.Equal(3, Sprint.For("2026-11-09", first).Number);
    }

    [Fact]
    public void test_a_missing_week_is_refused_not_guessed() =>
        Assert.Throws<UsageException>(() => Sprint.For("2026-11-23", [P("2026-11-09", "2026-11-09 – Sprint 3")]));
}

public class PostTests
{
    [Fact]
    public void test_refuses_a_draft()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Markdown, "# [DRAFT] Architecture\n- x\n");
        Assert.Throws<RefusedException>(() => temp.Post(new FakeNotebook([Week("2026-11-09", 3)])));
    }

    [Fact]
    public void test_dry_run_changes_nothing()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3)]);
        temp.Post(notebook);
        Assert.Equal(0, notebook.Patches);
    }

    [Fact]
    public void test_posts_into_an_empty_section_and_reads_back()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3)]);
        var result = temp.Post(notebook, dryRun: false);
        Assert.True(result.Posted);
        var html = notebook.Pages[0].Html;
        Assert.Equal(Markdown.Plain(Markdown.Bullets(Update, "Architecture")),
            PageHtml.Text(string.Concat(PageHtml.Section(html, "Architecture").Under.Select(b => b.Html))));
        Assert.Contains("Digital news", html);
        Assert.Single(Directory.GetFiles(temp.Combine("backups")));
    }

    [Fact]
    public void test_a_heading_onenote_drops_is_put_back()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3, "<ul id=\"ul:9\"><li><span>Old</span></li></ul>")], dropsHeading: true);
        temp.Post(notebook, dryRun: false, force: true);
        Assert.Equal(["Executive Summary", "Digital", "Fulfilment", "Architecture"], PageHtml.Headings(notebook.Pages[0].Html));
        Assert.Contains("put it back", string.Join(" ", temp.Lines));
    }

    [Fact]
    public void test_a_section_changed_by_hand_needs_force()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3, "<ul id=\"ul:9\"><li><span>Typed by hand</span></li></ul>")]);
        var refused = Assert.Throws<RefusedException>(() => temp.Post(notebook, dryRun: false));
        Assert.Contains("Typed by hand", refused.Message);
        temp.Post(notebook, dryRun: false, force: true);
        Assert.DoesNotContain("Typed by hand", notebook.Pages[0].Html);
    }

    [Fact]
    public void test_reposting_after_our_own_post_needs_no_force()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3)]);
        temp.Post(notebook, dryRun: false);
        File.WriteAllText(temp.Markdown, Update.Replace("very low", "recovering"));
        Assert.True(temp.Post(notebook, dryRun: false).Posted);
    }

    [Fact]
    public void test_creates_the_weeks_page_when_missing()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-10-26", 2), Week("2026-11-02", 3)]);
        var result = temp.Post(notebook, dryRun: false);
        Assert.True(result.Created);
        var created = notebook.Pages[^1];
        Assert.Equal("2026-11-09 – Sprint 3", created.Title);
        Assert.Equal(["Executive Summary", "Digital", "Fulfilment", "Assurance", "Architecture"], PageHtml.Headings(created.Html));
        Assert.Contains("Registry migration", PageHtml.Text(created.Html));
    }

    [Fact]
    public void test_a_section_with_more_than_one_list_is_left_alone()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3, "<ul id=\"ul:8\"><li>a</li></ul><ul id=\"ul:9\"><li>b</li></ul>")]);
        Assert.Throws<RefusedException>(() => temp.Post(notebook, dryRun: false, force: true));
    }
}

public class PullTests
{
    [Fact]
    public void test_writes_only_what_changed()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-02", 3), Week("2026-11-09", 3)]);
        Assert.Equal(2, Pull.Run(temp.Config, notebook, log: temp.Lines.Add).Written.Count);
        Assert.Equal(2, Pull.Run(temp.Config, notebook, log: temp.Lines.Add).Unchanged);
        notebook.Pages[1].Html = notebook.Pages[1].Html.Replace("Digital news", "Digital update");
        Assert.Single(Pull.Run(temp.Config, notebook, log: temp.Lines.Add).Written);
        Assert.DoesNotContain(" id=\"", File.ReadAllText(temp.Combine("out", "Weekly update 2026-11-09.html")));
    }
}
