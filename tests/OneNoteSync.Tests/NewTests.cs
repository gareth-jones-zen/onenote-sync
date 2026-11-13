using System.Net;
using System.Text;
using M365Graph;
using PyCompat;
using WinBackground;
using static OneNoteSync.Tests.Fixtures;

namespace OneNoteSync.Tests;

public class TextTests
{
    [Theory]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("&#163;5 &#x2013; &pound;", "\u00a35 \u2013 \u00a3")]
    [InlineData("&amp &lt", "& <")]                       // no ";" for the legacy names
    [InlineData("&notit; &notin;", "\u00acit; \u2209")]    // the longest-prefix rule
    [InlineData("&#128; &#x9d; &#0; &#xD800; &#1114112;", "\u20ac \x9d \ufffd \ufffd \ufffd")]   // cp1252, kept, NUL, surrogate, too big
    [InlineData("&#1;&#xfffe;x", "x")]                    // forbidden code points vanish
    [InlineData("&#128522;", "\U0001F60A")]
    [InlineData("&bogus; & &;", "&bogus; & &;")]
    [InlineData("&#39", "'")]
    public void Unescape_is_pythons(string html, string expected) => Assert.Equal(expected, PyHtml.Unescape(html));

    [Fact]
    public void Every_html5_name_is_known() => Assert.Equal(2231, PyHtml.NamedCount);

    [Fact]
    public void Escape_touches_only_amp_lt_gt() =>
        Assert.Equal("\u00a3 &amp; &lt;b&gt; \"q\" 'a' \u2013", PyHtml.Escape("\u00a3 & <b> \"q\" 'a' \u2013", quote: false));

    [Theory]
    [InlineData("\t", 4, "    ")]
    [InlineData("  \t", 4, "    ")]
    [InlineData(" \t \t", 4, "        ")]
    [InlineData("ab\tc", 8, "ab      c")]
    public void ExpandTabs_works_by_column(string text, int size, string expected) => Assert.Equal(expected, PyStr.ExpandTabs(text, size));

    [Fact]
    public void A_tab_indent_nests_as_python_did()
    {
        var items = Markdown.Bullets("# Architecture\n- a\n\t- b\n \t- c\n\t\t- d\n", "Architecture");
        Assert.Equal([0, 2, 2, 3], items.Select(i => i.Depth));
    }

    [Fact]
    public void Python_whitespace_includes_x1c_to_x1f()
    {
        Assert.Equal("a b", PageHtml.Text("<p>\x1c a\x1f\x1d b \x1e</p>"));
        Assert.Equal("x", PyStr.Strip("\x1cx\u00a0"));
    }

    [Fact]
    public void Heading_length_counts_code_points()
    {
        var name = new string('a', 58) + "\U0001F60A";   // 59 code points, 60 UTF-16 units
        Assert.True(PageHtml.IsHeading(Heading("p:1", name)));
    }

    [Fact]
    public void Inline_markup_and_odd_cases()
    {
        Assert.Equal("<span style=\"font-size:11.5pt;color:black\">a &lt;b&gt; \u00a35</span>", Markdown.Inline("a <b> \u00a35"));
        Assert.Contains("font-weight:bold\"></span>", Markdown.Inline("**"));   // part[2:-2] of "**" is ""
        Assert.Contains("italic\">x y</span>", Markdown.Inline("*x y*"));
        Assert.Contains("italic\"> x</span>", Markdown.Inline("* x*"));   // as Python: a whole part in stars is italic
    }

    [Fact]
    public void Frontmatter_and_wrapped_lines()
    {
        var md = "---\ntitle: x\n---\n# Other\n- no\n# Architecture\n- one\n  wrapped\n- two\n";
        var items = Markdown.Bullets(md, "architecture");
        Assert.Equal(["one wrapped", "two"], items.Select(i => i.Text));
    }

    [Fact]
    public void A_byte_order_mark_is_kept_so_frontmatter_isnt_seen()
    {
        var items = Markdown.Bullets("\ufeff---\n- x\n---\n# Architecture\n- y\n", "Architecture");
        Assert.Equal(["y"], items.Select(i => i.Text));
    }

    [Fact]
    public void A_new_page_has_lf_line_endings_and_an_en_dash()
    {
        var html = Markdown.NewPage(Sprint.Title("2026-11-16", 4), ["Executive Summary", "R&D"]);
        Assert.DoesNotContain("\r", html);
        Assert.Contains("<title>2026-11-16 \u2013 Sprint 4</title>", html);
        Assert.Contains(">R&amp;D</span>", html);
        Assert.StartsWith("<!DOCTYPE html>\n<html>\n<head>\n", html);
    }

    [Fact]
    public void Patch_bodies_are_pythons_json_dumps()
    {
        var body = PatchChange.Body([PatchChange.InsertAfter("p:{1}", "<li>\u00a3 \u2013 \"q\"\U0001F60A</li>"), PatchChange.Replace(null, "x")]);
        Assert.Equal("[{\"target\": \"p:{1}\", \"action\": \"insert\", \"position\": \"after\", \"content\": "
                     + "\"<li>\\u00a3 \\u2013 \\\"q\\\"\\ud83d\\ude0a</li>\"}, {\"target\": null, \"action\": \"replace\", \"content\": \"x\"}]", body);
    }

    [Fact]
    public void Owner_is_quoted_as_urllib_did() =>
        Assert.Equal("first.last%40example.com", GraphNotebook.Quote("first.last@example.com"));

    [Fact]
    public void Dated_pages_sort_by_date_then_title_and_skip_the_rest()
    {
        var sorted = Dates.Sort([("a", "2026-11-09 b"), ("b", "TEMPLATE"), ("c", null), ("d", "2026-11-09 a"), ("e", "2026-11-02 \u2013 Sprint 3")]);
        Assert.Equal(["e", "d", "a"], sorted.Select(p => p.Id));
    }
}

public class SprintMoreTests
{
    private static PageInfo P(string date, string title) => new(date, title, date);

    [Fact]
    public void Zero_padded_numbers_and_a_hyphen_title_still_count()
    {
        var pages = new[] { P("2026-03-23", "2026-03-23 - Sprint 13"), P("2026-03-30", "2026-03-30 - Sprint 13") };
        Assert.Equal(14, Sprint.For("2026-04-06", pages).Number);
        Assert.Equal(8, Sprint.For("2026-01-19", [P("2026-01-05", "x Sprint 07"), P("2026-01-12", "x Sprint 07")]).Number);
    }

    [Fact]
    public void A_restart_at_sprint_1_is_followed() =>
        Assert.Equal(1, Sprint.For("2026-10-12", [P("2026-09-28", "Sprint 26"), P("2026-10-05", "Sprint 1")]).Number);

    [Fact]
    public void A_date_must_be_yyyy_mm_dd() => Assert.Throws<UsageException>(() => Sprint.ParseDate("20261109"));
}

public class ConfigTests
{
    [Fact]
    public void Paths_expand_like_python()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("config.toml"), Settings.Replace("state_file = \"state.json\"", "state_file = \"%ONS_TEST%/s.json\"")
            .Replace("log_file = \"sync.log\"", "log_file = \"${ONS_TEST}/l.log\""));
        var env = (string name) => name == "ONS_TEST" ? @"C:\x" : Config.Environment(name);
        var config = Config.Load(temp.Combine("config.toml"), env);
        Assert.Equal(@"C:\x\s.json", config.StateFile);
        Assert.Equal(@"C:\x\l.log", config.LogFile);
        Assert.Equal(temp.Combine("backups"), config.Backups);
        Assert.Equal(temp.Combine("out"), config.Pull!.Folder);
    }

    [Fact]
    public void Missing_and_bad_settings_are_usage_errors()
    {
        using var temp = new TempDir();
        Assert.Throws<NoSettingsException>(() => Config.Load(temp.Combine("nope.toml")));
        File.WriteAllText(temp.Combine("config.toml"), Settings.Replace("file = \"Weekly update {date}.html\"", "file = \"x.html\""));
        Assert.Contains("must contain {date}", Assert.Throws<UsageException>(() => Config.Load(temp.Combine("config.toml"))).Message);
        File.WriteAllText(temp.Combine("config.toml"), Settings.Replace("\"Assurance\", \"Architecture\"", "\"Assurance\""));
        Assert.Contains("'Architecture'", Assert.Throws<UsageException>(() => Config.Load(temp.Combine("config.toml"))).Message);
    }

    [Theory]
    [InlineData("\"\"", "''")]
    [InlineData("\"  \"", "'  '")]
    [InlineData("\"2 x\"", "'2 x'")]
    public void A_number_setting_that_isnt_one_is_refused_as_pythons_int_refuses_it(string value, string repr)
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("config.toml"), Settings.Replace("[pull]\n", $"[pull]\nnewest = {value}\n"));
        Assert.EndsWith($"invalid literal for int() with base 10: {repr}",
            Assert.Throws<UsageException>(() => Config.Load(temp.Combine("config.toml"))).Message);
    }

    [Theory]
    [InlineData("\" 3 \"", 3)]
    [InlineData("\"+1_0\"", 10)]
    [InlineData("\"\u0663\"", 3)]
    public void A_number_setting_as_text_is_read_as_pythons_int_reads_it(string value, int expected)
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("config.toml"), Settings.Replace("[pull]\n", $"[pull]\nnewest = {value}\n"));
        Assert.Equal(expected, Config.Load(temp.Combine("config.toml")).Pull!.Newest);
    }

    [Fact]
    public void The_file_name_takes_only_date() =>
        Assert.Equal("{x} 2026-11-09.html", Pull.FileName("{{x}} {date}.html", "2026-11-09"));
}

public class PullAndPostMoreTests
{
    [Fact]
    public void Pulled_bytes_are_never_decoded()
    {
        using var temp = new TempDir();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', 0xFF, 0xFE, (byte)'\r', (byte)'\n', (byte)'x', (byte)'\n' };
        var notebook = new BytesNotebook(bytes);
        Pull.Run(temp.Config, notebook, log: temp.Lines.Add);
        Assert.Equal(bytes, File.ReadAllBytes(temp.Combine("out", "Weekly update 2026-11-09.html")));
        Assert.Equal(1, Pull.Run(temp.Config, notebook, log: temp.Lines.Add).Unchanged);
        Assert.Equal("pull: 0 written, 1 unchanged", temp.Lines[^1]);
    }

    private sealed class BytesNotebook(byte[] bytes) : IOneNote
    {
        public IReadOnlyList<PageInfo> DatedPages() => [new PageInfo("p", "2026-11-09 \u2013 Sprint 3", "2026-11-09")];
        public byte[] Content(string pageId, bool ids = false, bool isNew = false) => bytes;
        public void Patch(string pageId, IReadOnlyList<PatchChange> changes) => throw new NotSupportedException();
        public string Create(string html) => throw new NotSupportedException();
    }

    [Fact]
    public void A_backup_is_the_exact_bytes_fetched()
    {
        using var temp = new TempDir();
        var page = Week("2026-11-09", 3);
        page.Html = page.Html.Replace("<body>", "<body>\r\n");
        var notebook = new FakeNotebook([page]);
        var before = notebook.Content("2026-11-09", ids: true);
        Post.Run(temp.Config, notebook, temp.Markdown, null, false, false, temp.Lines.Add, () => new DateTime(2026, 11, 13, 9, 5, 7));
        Assert.Equal(before, File.ReadAllBytes(temp.Combine("backups", "2026-11-09-20261113-090507.html")));
    }

    [Fact]
    public void State_is_written_as_python_wrote_it_and_keeps_other_pages()
    {
        using var temp = new TempDir();
        File.WriteAllBytes(temp.Config.StateFile, Encoding.UTF8.GetBytes("{\r\n  \"other\": {\r\n    \"Digital\": \"x \u00a3\"\r\n  },\r\n  \"empty\": {}\r\n}"));
        var notebook = new FakeNotebook([Week("2026-11-09", 3)]);
        temp.Post(notebook, dryRun: false);
        var expected = "{\r\n  \"other\": {\r\n    \"Digital\": \"x \u00a3\"\r\n  },\r\n  \"empty\": {},\r\n  \"2026-11-09\": {\r\n"
                       + "    \"Architecture\": \"Registry migration - design done; Team A starts 11/11\\nsupplier cut-over 09/02/27\\n"
                       + "Address pool - very low & being fixed\"\r\n  }\r\n}";
        Assert.Equal(expected, File.ReadAllText(temp.Config.StateFile));
        var round = StateFile.Load(temp.Config.StateFile);
        Assert.Equal(File.ReadAllBytes(temp.Config.StateFile), StateFile.Bytes(round));
        Assert.Equal("{}", Encoding.UTF8.GetString(StateFile.Bytes(new())));
    }

    [Fact]
    public void A_lost_patch_reply_is_not_resent_and_the_read_back_decides()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-11-09", 3)]) { LoseNextPatchReply = true };
        Assert.True(temp.Post(notebook, dryRun: false).Posted);
        Assert.Equal(1, notebook.Patches);
        Assert.Contains(temp.Lines, line => line.Contains("reading the page back"));
    }

    [Fact]
    public void Dry_run_diff_is_pythons()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Config.StateFile, "{\"2026-11-09\": {\"Architecture\": \"Old\"}}");
        var notebook = new FakeNotebook([Week("2026-11-09", 3, "<ul id=\"ul:9\"><li><span>Old</span></li></ul>")]);
        temp.Post(notebook);
        Assert.Equal("Would replace 'Architecture' on '2026-11-09 \u2013 Sprint 3':\n--- on the page now\n+++ to post\n@@ -1 +1,3 @@\n-Old\n"
                     + "+Registry migration - design done; Team A starts 11/11\n+supplier cut-over 09/02/27\n+Address pool - very low & being fixed",
            temp.Lines[^1]);
    }

    [Fact]
    public void A_dry_run_for_a_missing_week_says_what_it_would_create()
    {
        using var temp = new TempDir();
        var notebook = new FakeNotebook([Week("2026-10-26", 2), Week("2026-11-02", 3)]);
        var result = temp.Post(notebook);
        Assert.Equal("2026-11-09 \u2013 Sprint 3", result.Page);
        Assert.Empty(notebook.Created);
        Assert.StartsWith("No page for 2026-11-09 yet: would create '2026-11-09 \u2013 Sprint 3' (2026-11-02 was week 1 of 2", temp.Lines[0]);
    }
}

public class GraphNotebookTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Method, string Url, string? ContentType, byte[]? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancel);
            var type = request.Content?.Headers.TryGetValues("Content-Type", out var values) == true ? string.Join(",", values) : null;
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), type, body));
            return answer(request, Requests.Count);
        }
    }

    private static (GraphNotebook, Handler, List<TimeSpan>) Make(Func<HttpRequestMessage, int, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var waits = new List<TimeSpan>();
        var raw = Client(handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
            () => new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var notebook = new GraphNotebook(_ => Empty(), raw, "a@b.c", "N", "S", wait => { waits.Add(wait); return Task.CompletedTask; });
        return (notebook, handler, waits);
    }

    /// <summary>M365Graph's client over a fake handler, with a fake token (no MSAL, no cache file) and recorded waits.</summary>
    private static M365Graph.GraphClient Client(HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null) =>
        new(new M365Graph.GraphClientOptions { Log = _ => { } }, handler, (_, _) => Task.FromResult("token"),
            delay ?? ((_, _) => Task.CompletedTask), clock);

    private static async IAsyncEnumerable<System.Text.Json.Nodes.JsonNode?> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, byte[]? body = null) =>
        new(status) { Content = new ByteArrayContent(body ?? []) };

    [Fact]
    public void Content_is_the_bytes_as_served()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0xFF, 13, 10 };
        var (notebook, handler, _) = Make((_, _) => Answer(HttpStatusCode.OK, bytes));
        Assert.Equal(bytes, notebook.Content("1-abc", ids: true));
        Assert.Equal("https://graph.microsoft.com/v1.0/users/a%40b.c/onenote/pages/1-abc/content?includeIDs=true", handler.Requests[0].Url);
    }

    [Fact]
    public void A_get_is_retried_and_retry_after_can_be_a_date()
    {
        var (notebook, handler, waits) = Make((_, n) =>
        {
            if (n == 1)
            {
                var busy = Answer(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(new DateTimeOffset(2026, 11, 13, 9, 0, 7, TimeSpan.Zero));
                return busy;
            }
            return n == 2 ? Answer(HttpStatusCode.BadGateway) : Answer(HttpStatusCode.OK, [1]);
        });
        Assert.Equal([1], notebook.Content("p"));
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10)], waits);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public void A_get_is_tried_five_times_then_says_what_onenote_said()
    {
        var (notebook, handler, waits) = Make((_, _) => Answer(HttpStatusCode.ServiceUnavailable, Encoding.UTF8.GetBytes("busy")));
        Assert.Equal(503, Assert.Throws<OneNoteException>(() => notebook.Content("p")).Status);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal([5, 10, 15, 20], waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public void A_create_that_got_a_5xx_isnt_sent_again()
    {
        var handler = new Handler((_, _) => Answer(HttpStatusCode.GatewayTimeout));
        var notebook = new GraphNotebook(
            url => Items(url.Contains("/sections?") ? """[{"id":"s1","displayName":"S"}]""" : """[{"id":"n1","displayName":"n"}]"""),
            Client(handler), "me", "N", "s", null);
        Assert.EndsWith("(not sent again: it may have been applied)",
            Assert.Throws<DeliveryUnknownException>(() => notebook.Create("<html></html>")).Message);
        Assert.Single(handler.Requests);
        Assert.Equal("text/html", handler.Requests[0].ContentType);
    }

    [Fact]
    public void A_new_page_can_answer_404_for_a_while()
    {
        var (notebook, handler, waits) = Make((_, n) => n < 4 ? Answer(HttpStatusCode.NotFound) : Answer(HttpStatusCode.OK, [2]));
        Assert.Equal([2], notebook.Content("p", ids: true, isNew: true));
        Assert.Equal(3, waits.Count);
        var (old, _, _) = Make((_, _) => Answer(HttpStatusCode.NotFound));
        Assert.Equal(404, Assert.Throws<OneNoteException>(() => old.Content("p")).Status);
    }

    [Fact]
    public void A_patch_is_sent_once_with_the_exact_content_type()
    {
        var (notebook, handler, _) = Make((_, _) => Answer(HttpStatusCode.BadGateway));
        Assert.Throws<DeliveryUnknownException>(() => notebook.Patch("p", [PatchChange.Replace("ul:1", "<ul>\u00a3</ul>")]));
        Assert.Single(handler.Requests);
        Assert.Equal("application/json", handler.Requests[0].ContentType);
        Assert.Equal("[{\"target\": \"ul:1\", \"action\": \"replace\", \"content\": \"<ul>\\u00a3</ul>\"}]", Encoding.UTF8.GetString(handler.Requests[0].Body!));
    }

    [Fact]
    public void A_write_is_retried_after_a_429_but_not_after_a_timeout()
    {
        var (notebook, handler, _) = Make((_, n) => n == 1 ? Answer(HttpStatusCode.TooManyRequests) : Answer(HttpStatusCode.NoContent));
        notebook.Patch("p", [PatchChange.Replace("ul:1", "x")]);
        Assert.Equal(2, handler.Requests.Count);
        var (timedOut, again, _) = Make((_, _) => throw new TaskCanceledException("timed out", new TimeoutException()));
        Assert.Throws<DeliveryUnknownException>(() => timedOut.Patch("p", [PatchChange.Replace("ul:1", "x")]));
        Assert.Single(again.Requests);
    }

    [Fact]
    public void A_write_that_never_connected_is_retried()
    {
        var (notebook, handler, _) = Make((_, n) => n == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
            : Answer(HttpStatusCode.NoContent));
        notebook.Patch("p", [PatchChange.Replace("ul:1", "x")]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void A_bad_request_says_what_onenote_said()
    {
        var (notebook, _, _) = Make((_, _) => Answer(HttpStatusCode.Forbidden, Encoding.UTF8.GetBytes("denied")));
        Assert.Equal("OneNote said 403 to GET /users/a%40b.c/onenote/pages/p/content: denied",
            Assert.Throws<OneNoteException>(() => notebook.Content("p")).Message);
    }

    [Fact]
    public void Create_posts_html_and_reads_the_id()
    {
        var (notebook, handler, _) = Make((_, _) => Answer(HttpStatusCode.Created, Encoding.UTF8.GetBytes("{\"id\": \"0-new\"}")));
        var withSection = new GraphNotebook(
            url => Items(url.Contains("/sections?") ? """[{"id":"s1","displayName":"S"}]""" : """[{"id":"n1","displayName":"n"}]"""),
            Client(new Handler((_, _) => Answer(HttpStatusCode.Created, Encoding.UTF8.GetBytes("{\"id\": \"0-new\"}")))),
            "me", "N", "s", null);
        Assert.Equal("0-new", withSection.Create("<html>\u00a3</html>"));
        _ = notebook;
        _ = handler;
    }

    private static async IAsyncEnumerable<System.Text.Json.Nodes.JsonNode?> Items(string json)
    {
        await Task.CompletedTask;
        foreach (var item in System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray())
            yield return item;
    }
}

public class LockAndWatcherTests
{
    [Fact]
    public void The_lock_key_is_the_pythons() =>
        // hashlib.sha256(r"C:\Users\x\tools\onenote-sync.toml".lower().encode()).hexdigest()[:16]
        Assert.Equal(Locks.Key(@"C:\Users\x\tools\onenote-sync.toml"), Locks.Key(@"c:\users\X\TOOLS\onenote-sync.toml"));

    [Fact]
    public async Task A_run_lock_is_released_after_an_await()
    {
        // The run lock is a mutex. WinBackground's NamedLock owns it on a dedicated thread (since a1e4d97), so it's
        // released even when the release runs on another thread after an await; before that the lock stayed held
        // until the process exited. Acquire, await (yielding, so the continuation may move thread), release, then
        // check another thread can take it.
        var name = "onenote-sync-test-" + Guid.NewGuid().ToString("N");
        var host = new CliHost();
        using (host.RunLock(name, 0))
        {
            await Task.Yield();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        var acquired = false;
        var other = new Thread(() =>
        {
            using var again = new NamedLock(name);
            acquired = again.Acquire(0);
        });
        other.Start();
        other.Join();
        Assert.True(acquired);
    }

    private sealed class Clock
    {
        public double Time = 1_000_000;
        public DateTime Now = new(2026, 11, 13, 9, 0, 0);   // a Friday
        public void Sleep(TimeSpan span)
        {
            Time += span.TotalSeconds;
            Now += span;
        }
    }

    private static (List<string> Log, List<string> Notes, int Pulls) RunWatcher(Func<int, Exception?> fail, int ticks, Clock? clock = null)
    {
        using var temp = new TempDir();
        clock ??= new Clock();
        var log = new List<string>();
        var notes = new List<string>();
        var pulls = 0;
        var tick = 0;
        var host = new WatchHost
        {
            Time = () => clock.Time,
            Now = () => clock.Now,
            Sleep = clock.Sleep,
            Stop = () => tick++ >= ticks,
            Notify = (title, _) => notes.Add(title),
            Lock = name => new NamedLock(name + Guid.NewGuid().ToString("N")) is var l && l.Acquire(0) ? l : null,
            Reload = _ => temp.Config,
        };
        Watcher.Run(temp.Config, _ =>
        {
            pulls++;
            if (fail(pulls) is { } error)
                throw error;
            return new FakeNotebook([Week("2026-11-09", 3)]);
        }, log.Add, host);
        return (log, notes, pulls);
    }

    [Fact]
    public void It_pulls_every_15_minutes_and_says_alive_hourly()
    {
        var (log, notes, pulls) = RunWatcher(_ => null, 2 * 60 * 2 + 1);   // two hours of 30-second ticks
        Assert.Equal(9, pulls);
        Assert.Equal(2, log.Count(l => l == "alive"));
        Assert.Empty(notes);
        Assert.StartsWith("watching 'Weekly' / 'Updates' every 15 min, 07:00-19:00 Mon-Fri", log[0]);
    }

    [Fact]
    public void A_failure_is_notified_on_the_third_in_a_row_and_a_sign_in_at_once()
    {
        var (log, notes, _) = RunWatcher(n => n <= 4 ? new HttpRequestException("No such host is known.") : null, 2 * 6 * 15);
        Assert.Equal(["onenote-sync couldn't pull"], notes);
        Assert.Contains("pull failed: HttpRequestException: No such host is known.", log);
        var (_, signIn, _) = RunWatcher(_ => new SignInNeededException("Sign in again"), 2 * 60);
        Assert.Equal(["onenote-sync needs a sign-in"], signIn);
    }

    [Fact]
    public void Nothing_is_pulled_at_the_weekend_or_out_of_hours()
    {
        var (_, _, weekend) = RunWatcher(_ => null, 60, new Clock { Now = new DateTime(2026, 11, 14, 10, 0, 0) });
        Assert.Equal(0, weekend);
        var (_, _, evening) = RunWatcher(_ => null, 60, new Clock { Now = new DateTime(2026, 11, 13, 19, 0, 0) });
        Assert.Equal(0, evening);
    }

    [Fact]
    public void A_second_watcher_exits_3()
    {
        using var temp = new TempDir();
        var log = new List<string>();
        Assert.Equal(3, Watcher.Run(temp.Config, _ => throw new InvalidOperationException(), log.Add, new WatchHost { Lock = _ => null }));
        Assert.Equal(["another watcher is already running for these settings; not starting a second"], log);
    }
}
