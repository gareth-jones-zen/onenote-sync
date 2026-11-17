namespace OneNoteSync.Tests;

/// <summary>status: which sections of a page have text under them. Shapes from the real 2026 pages.</summary>
public class StatusTests
{
    private static readonly string[] Boundaries = ["Executive Summary", "Digital", "Fulfilment", "Service", "Architecture"];

    private static string H(string text, int size = 14) =>
        $"<p><span style=\"font-size:{size}pt;color:black;font-weight:bold\">{text}</span></p>";

    private static string Bullets(params string[] items) =>
        "<ul>" + string.Concat(items.Select(i => $"<li><span>{i}</span></li>")) + "</ul>";

    private static Dictionary<string, int> Words(string body) =>
        Status.Words("<html><body><div style=\"position:absolute\">" + body + "</div></body></html>", Boundaries);

    [Fact]
    public void Counts_the_words_under_each_section_and_leaves_out_missing_ones()
    {
        var words = Words(H("Executive Summary", 18) + Bullets("Overview here") + H("Digital") + Bullets("Shipped the portal")
            + H("Architecture") + "<br />");
        Assert.Equal(3, words["Digital"]);
        Assert.Equal(0, words["Architecture"]);
        Assert.False(words.ContainsKey("Fulfilment"));
    }

    [Fact]
    public void A_12pt_bold_heading_still_counts()   // July 2026's "Architecture"
    {
        var words = Words(H("Service") + Bullets("Service news") + H("Architecture", 12) + Bullets("Arch news"));
        Assert.Equal(2, words["Service"]);
        Assert.Equal(2, words["Architecture"]);
    }

    [Fact]
    public void A_sub_heading_on_its_own_is_not_content()   // 12 Oct 2026's Architecture: "Highlights:" and nothing else
    {
        var words = Words(H("Service") + Bullets("Service news") + H("Architecture") + H("Highlights:", 12));
        Assert.Equal(0, words["Architecture"]);
    }

    [Fact]
    public void A_sub_heading_with_bullets_is_part_of_its_section()
    {
        var words = Words(H("Fulfilment") + H("Highlights:", 12) + Bullets("Orders flowing") + H("Challenges:", 12)
            + Bullets("Late kit") + H("Service") + Bullets("Calm"));
        Assert.Equal(4, words["Fulfilment"]);
        Assert.Equal(1, words["Service"]);
    }

    [Fact]
    public void The_fullest_occurrence_counts_and_a_named_title_still_matches()   // early 2026: a summary, then "… Team Update:" detail
    {
        var words = Words(H("Architecture") + H("Digital Tribe Update:", 18) + H("Asimov:", 12) + Bullets("Shipped it")
            + H("Architecture Team Update:", 18) + Bullets("Design agreed for billing"));
        Assert.Equal(4, words["Architecture"]);
        Assert.Equal(2, words["Digital"]);
    }

    [Fact]
    public void A_bold_phrase_inside_body_text_is_not_a_heading()
    {
        var words = Words(H("Digital") + "<p><span style=\"font-weight:bold\">Service</span> desk calls dropped</p>"
            + H("Service") + Bullets("Calm"));
        Assert.Equal(4, words["Digital"]);
        Assert.Equal(1, words["Service"]);
    }

    [Fact]
    public void Run_reports_the_newest_first_and_only_the_newest_as_current()
    {
        using var dir = new TempDir();
        var notebook = new FakeNotebook([Fixtures.Week("2026-11-02", 3, Bullets("Old work")), Fixtures.Week("2026-11-09", 3)]);
        var weeks = Status.Run(dir.Config, notebook, 2);
        Assert.Equal(["2026-11-09", "2026-11-02"], weeks.Select(w => w.Date));
        Assert.True(weeks[0].Current);
        Assert.False(weeks[1].Current);
        Assert.Contains("Architecture", weeks[0].Empty);
        Assert.Contains("Architecture", weeks[1].Filled);
        Assert.Contains("Assurance", weeks[1].Missing);   // [post] headings name it; the fixture page has none
        // With no [status] sections, every [post] heading is reported, "Executive Summary" included.
        Assert.Equal("2026-11-02 (final): 3 of 5 sections filled - Executive Summary empty, Assurance missing", weeks[1].Line(5));
    }

    [Fact]
    public void Status_sections_override_the_post_headings()
    {
        using var dir = new TempDir();
        File.AppendAllText(dir.Combine("config.toml"), "[status]\nsections = [\"Digital\", \"Architecture\"]\n");
        dir.Config = OneNoteSync.Config.Load(dir.Combine("config.toml"));
        Assert.Equal(["Digital", "Architecture"], Status.Sections(dir.Config));
    }
}
