using System.Text;

namespace OneNoteSync.Tests;

/// <summary>PageRepair, on the shape Graph served for the 16 Nov 2026 weekly update (trimmed).</summary>
public class RepairTests
{
    private static readonly string[] Headings = ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Service", "Architecture"];

    private static string H(string name) => $"<p style=\"margin-top:0pt\"><span style=\"font-size:14pt;font-weight:bold\">{name}</span></p>";

    private static string Page(string inside, string after = "") =>
        "<html><body>\n<div style=\"position:absolute;left:48px;top:114px;width:720px\">\n<ul>\n"
        + inside + "\n</ul>\n" + after + "\n<br />\n</div>\n</body></html>";

    private static string Order(string html, params string[] words) =>
        string.Join(",", words.OrderBy(w => html.IndexOf(w, StringComparison.Ordinal)));

    private static (string Html, List<string> Moved) Fix(string html)
    {
        var (bytes, moved) = PageRepair.Repair(Encoding.UTF8.GetBytes(html), Headings);
        return (Encoding.UTF8.GetString(bytes), moved);
    }

    [Fact]
    public void An_orphaned_heading_goes_back_and_takes_the_bare_list_before_the_next_heading()
    {
        var html = Page(
            H("Assurance") + "<p>Challenges:</p><ul><li>OFNL scope</li></ul>\n<br />\n<ul><li>Salesforce AI</li></ul>" + H("Architecture") + "<ul><li>Arch work</li></ul>",
            "<p><span style=\"font-weight:bold\">Highlights: </span></p>" + H("Service"));
        var (fixedHtml, moved) = Fix(html);
        Assert.Equal(["Service"], moved);
        Assert.Equal("Assurance,OFNL scope,Service,Salesforce AI,Architecture,Arch work,Highlights",
            Order(fixedHtml, "Assurance", "OFNL scope", "Service", "Salesforce AI", "Architecture", "Arch work", "Highlights"));
        Assert.Equal(1, CountOf(fixedHtml, ">Service<"));
    }

    [Fact]
    public void Without_a_bare_list_it_goes_back_empty_before_the_next_heading()
    {
        var html = Page(H("Assurance") + "<ul><li>OFNL scope</li></ul>" + H("Architecture") + "<ul><li>Arch work</li></ul>", H("Service"));
        var (fixedHtml, moved) = Fix(html);
        Assert.Equal(["Service"], moved);
        Assert.Equal("OFNL scope,Service,Architecture", Order(fixedHtml, "OFNL scope", "Service", "Architecture"));
    }

    [Fact]
    public void With_no_later_heading_it_goes_to_the_end_of_the_block()
    {
        var html = Page(H("Digital") + "<ul><li>Digital work</li></ul>" + H("Assurance") + "<ul><li>OFNL scope</li></ul>", H("Architecture"));
        var (fixedHtml, _) = Fix(html);
        var block = fixedHtml.IndexOf("</ul>\n", fixedHtml.IndexOf("OFNL scope", StringComparison.Ordinal) + 20, StringComparison.Ordinal);
        Assert.True(fixedHtml.IndexOf(">Architecture<", StringComparison.Ordinal) < block);
    }

    [Fact]
    public void A_page_with_nothing_out_of_place_is_the_same_bytes()
    {
        var served = Encoding.UTF8.GetBytes(Page(H("Assurance") + "<ul><li>a</li></ul>" + H("Service") + "<ul><li>b</li></ul>", "<p>Highlights: </p>"));
        var (bytes, moved) = PageRepair.Repair(served, Headings);
        Assert.Same(served, bytes);
        Assert.Empty(moved);
    }

    [Fact]
    public void A_page_without_a_heading_block_or_without_headings_settings_is_untouched()
    {
        var flat = Encoding.UTF8.GetBytes("<html><body><div style=\"position:absolute\">" + H("Digital") + "<ul><li>x</li></ul>" + H("Service") + "</div></body></html>");
        Assert.Same(flat, PageRepair.Repair(flat, Headings).Html);
        var orphaned = Encoding.UTF8.GetBytes(Page(H("Assurance") + "<ul><li>a</li></ul>", H("Service")));
        Assert.Same(orphaned, PageRepair.Repair(orphaned, []).Html);
    }

    private static int CountOf(string text, string what)
    {
        var n = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal))
            n++;
        return n;
    }
}
