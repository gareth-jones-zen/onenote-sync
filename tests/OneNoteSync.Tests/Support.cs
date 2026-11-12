using System.Text;

namespace OneNoteSync.Tests;

/// <summary>The Python tests' fixtures: a synthetic weekly page, settings and an update.</summary>
public static class Fixtures
{
    public const string H14 = "<p id=\"{id}\" style=\"margin-top:0pt\"><span style=\"font-size:14pt;color:black;font-weight:bold\">{text}</span></p>";

    public static string Heading(string id, string text) => H14.Replace("{id}", id).Replace("{text}", text);

    public static string Page(string title, string arch) =>
        "<html><head><title>" + title + "</title></head><body><div id=\"div:1\" style=\"position:absolute\">"
        + "<p id=\"p:0\"><span style=\"font-family:Aptos;font-size:18pt;color:black;font-weight:bold;text-decoration:underline\">Executive Summary</span></p><br />"
        + Heading("p:1", "Digital") + "<ul id=\"ul:1\"><li id=\"li:1\"><span>Digital news</span></li></ul><br />"
        + Heading("p:2", "Fulfilment") + "<p id=\"p:3\"><span style=\"font-size:12pt;font-weight:bold\">Highlights:</span></p>"
        + "<ul id=\"ul:2\"><li><span>Shipped</span></li></ul>"
        + Heading("p:4", "Architecture") + arch + "<br /></div></body></html>";

    public const string Settings =
        "\nstate_file = \"state.json\"\nlog_file = \"sync.log\"\nbackups = \"backups\"\n[notebook]\nowner = \"colleague@example.com\"\n"
        + "name = \"Weekly\"\nsection = \"Updates\"\n[pull]\nfolder = \"out\"\nfile = \"Weekly update {date}.html\"\n[post]\n"
        + "section = \"Architecture\"\nheadings = [\"Executive Summary\", \"Digital\", \"Fulfilment\", \"Assurance\", \"Architecture\"]\n";

    public const string Update =
        "# Architecture\n\n- **Registry migration** - design done; *Team A* starts 11/11\n  - supplier cut-over 09/02/27\n"
        + "- **Address pool** - very low & being fixed\n\n---\n\n## For review\n- this is never posted\n";

    public static FakePage Week(string date, int sprint, string arch = "")
    {
        var title = $"{date} – Sprint {sprint}";
        return new FakePage(date, title, date, Page(title, arch));
    }
}

/// <summary>A temporary folder with the settings and the 2026-11-09 update in it, removed afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onenote-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        File.WriteAllText(Combine("config.toml"), Fixtures.Settings, new UTF8Encoding(false));
        Markdown = Combine("2026-11-09.md");
        File.WriteAllText(Markdown, Fixtures.Update, new UTF8Encoding(false));
        Config = OneNoteSync.Config.Load(Combine("config.toml"));
    }

    public string Path { get; }
    public string Markdown { get; }
    public Config Config { get; set; }
    public List<string> Lines { get; } = [];

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public PostResult Post(IOneNote notebook, bool dryRun = true, bool force = false, string? date = null) =>
        OneNoteSync.Post.Run(Config, notebook, Markdown, date, dryRun, force, Lines.Add);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}
