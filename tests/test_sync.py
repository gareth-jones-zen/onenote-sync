"""Run with: python -m unittest discover -s tests"""

import pathlib
import re
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from onenote_sync import config as cfg, page as pg  # noqa: E402
from onenote_sync.onenote import sprint_for  # noqa: E402
from onenote_sync.sync import Refused, post, pull  # noqa: E402

H14 = '<p id="{id}" style="margin-top:0pt"><span style="font-size:14pt;color:black;font-weight:bold">{text}</span></p>'
PAGE = ('<html><head><title>{title}</title></head><body><div id="div:1" style="position:absolute">'
        '<p id="p:0"><span style="font-family:Aptos;font-size:18pt;color:black;font-weight:bold;text-decoration:underline">Executive Summary</span></p><br />'
        + H14.format(id="p:1", text="Digital") + '<ul id="ul:1"><li id="li:1"><span>Digital news</span></li></ul><br />'
        + H14.format(id="p:2", text="Fulfilment") + '<p id="p:3"><span style="font-size:12pt;font-weight:bold">Highlights:</span></p>'
        '<ul id="ul:2"><li><span>Shipped</span></li></ul>'
        + H14.format(id="p:4", text="Architecture") + '{arch}<br /></div></body></html>')

SETTINGS = """
state_file = "state.json"
log_file = "sync.log"
backups = "backups"
[notebook]
owner = "colleague@example.com"
name = "Weekly"
section = "Updates"
[pull]
folder = "out"
file = "Weekly update {date}.html"
[post]
section = "Architecture"
headings = ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Architecture"]
"""

UPDATE = """# Architecture

- **Registry migration** - design done; *Team A* starts 11/11
  - supplier cut-over 09/02/27
- **Address pool** - very low & being fixed

---

## For review
- this is never posted
"""


class FakeNotebook:
    def __init__(self, pages, drops_heading=False):
        self.pages = pages   # [{id, title, date, html}]
        self.patches = 0
        self.drops_heading = drops_heading   # as OneNote did on a real page: replacing a list took its heading

    def dated_pages(self):
        return sorted([{k: p[k] for k in ("id", "title", "date")} for p in self.pages], key=lambda p: p["date"])

    def page_for(self, date):
        return next((p for p in self.dated_pages() if p["date"] == date), None)

    def _page(self, page_id):
        return next(p for p in self.pages if p["id"] == page_id)

    def content(self, page_id, ids=False, new=False):
        html = self._page(page_id)["html"]
        return (html if ids else re.sub(r' id="[^"]+"', "", html)).encode("utf-8")

    def patch(self, page_id, changes):
        self.patches += 1
        page = self._page(page_id)
        for change in changes:
            at = page["html"].index(f'id="{change["target"]}"')
            start = page["html"].rindex("<", 0, at)
            tag = re.match(r"<(\w+)", page["html"][start:]).group(1)
            end = pg._end(page["html"], start, tag)
            new = change["content"].replace("<ul>", '<ul id="ul:new">', 1)
            if change["action"] == "replace":
                if self.drops_heading and tag == "ul":
                    start = page["html"].rindex("<p ", 0, start)
                page["html"] = page["html"][:start] + new + page["html"][end:]
            elif change.get("position") == "before":
                page["html"] = page["html"][:start] + change["content"] + page["html"][start:]
            else:
                page["html"] = page["html"][:end] + new + page["html"][end:]

    def create(self, html):
        number = len(self.pages) + 1
        title = re.search(r"<title>(.*?)</title>", html).group(1)
        body = html.replace("<p ", '<p id="p:n{}" '.format(number), 1)
        body = re.sub(r"<p (?!id)", lambda m, c=iter(range(100)): f'<p id="p:n{number}-{next(c)}" ', body)
        self.pages.append({"id": f"page{number}", "title": title, "date": title[:10], "html": body})
        return {"id": f"page{number}"}


def week(date, sprint, arch=""):
    title = f"{date} – Sprint {sprint}"
    return {"id": date, "title": title, "date": date, "html": PAGE.format(title=title, arch=arch)}


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "config.toml").write_text(SETTINGS, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.md = self.dir / "2026-11-09.md"
        self.md.write_text(UPDATE, encoding="utf-8")
        self.lines = []

    def tearDown(self):
        self.temp.cleanup()

    def post(self, notebook, **kwargs):
        return post(self.config, notebook, self.md, log=self.lines.append, **kwargs)


class Page(unittest.TestCase):
    def test_finds_sections_and_their_blocks(self):
        html = PAGE.format(title="t", arch='<ul id="ul:9"><li>Old</li></ul>')
        self.assertEqual(pg.headings(html), ["Executive Summary", "Digital", "Fulfilment", "Architecture"])
        head, under = pg.section(html, "fulfilment")
        self.assertEqual([b[0] for b in under], ["p", "ul"])   # Highlights: isn't a heading
        self.assertEqual(pg.section(html, "Architecture")[1][0][1], "ul:9")

    def test_bullets_stop_at_the_rule_and_keep_nesting(self):
        items = pg.bullets(UPDATE, "Architecture")
        self.assertEqual([d for d, _ in items], [0, 1, 0])
        self.assertNotIn("never posted", pg.plain(items))

    def test_render_reads_back_as_the_markdown_says(self):
        rendered = pg.render(pg.bullets(UPDATE, "Architecture"))
        self.assertIn("font-weight:bold\">Registry migration</span>", rendered)
        self.assertIn("font-style:italic\">Team A</span>", rendered)
        self.assertIn("very low &amp; being fixed", rendered)
        self.assertEqual(rendered.count("<ul>"), 2)
        self.assertEqual(pg.text(rendered).split("\n")[1], "supplier cut-over 09/02/27")


class Sprints(unittest.TestCase):
    def test_two_week_sprints(self):
        both = [{"date": "2026-11-02", "title": "2026-11-02 – Sprint 3"}, {"date": "2026-11-09", "title": "2026-11-09 – Sprint 3"}]
        self.assertEqual(sprint_for("2026-11-16", both)[0], 4)
        first = [{"date": "2026-10-26", "title": "2026-10-26 – Sprint 2"}, {"date": "2026-11-02", "title": "2026-11-02 – Sprint 3"}]
        self.assertEqual(sprint_for("2026-11-09", first)[0], 3)

    def test_a_missing_week_is_refused_not_guessed(self):
        with self.assertRaises(ValueError):
            sprint_for("2026-11-23", [{"date": "2026-11-09", "title": "2026-11-09 – Sprint 3"}])


class Post(Base):
    def test_refuses_a_draft(self):
        self.md.write_text("# [DRAFT] Architecture\n- x\n", encoding="utf-8")
        with self.assertRaises(Refused):
            self.post(FakeNotebook([week("2026-11-09", 3)]))

    def test_dry_run_changes_nothing(self):
        notebook = FakeNotebook([week("2026-11-09", 3)])
        self.post(notebook)
        self.assertEqual(notebook.patches, 0)

    def test_posts_into_an_empty_section_and_reads_back(self):
        notebook = FakeNotebook([week("2026-11-09", 3)])
        result = self.post(notebook, dry_run=False)
        self.assertTrue(result["posted"])
        html = notebook.pages[0]["html"]
        self.assertEqual(pg.text("".join(b[2] for b in pg.section(html, "Architecture")[1])), pg.plain(pg.bullets(UPDATE, "Architecture")))
        self.assertIn("Digital news", html)
        self.assertEqual(len(list((self.dir / "backups").iterdir())), 1)

    def test_a_heading_onenote_drops_is_put_back(self):
        notebook = FakeNotebook([week("2026-11-09", 3, '<ul id="ul:9"><li><span>Old</span></li></ul>')], drops_heading=True)
        self.post(notebook, dry_run=False, force=True)
        html = notebook.pages[0]["html"]
        self.assertEqual(pg.headings(html), ["Executive Summary", "Digital", "Fulfilment", "Architecture"])
        self.assertIn("put it back", " ".join(self.lines))

    def test_a_section_changed_by_hand_needs_force(self):
        notebook = FakeNotebook([week("2026-11-09", 3, '<ul id="ul:9"><li><span>Typed by hand</span></li></ul>')])
        with self.assertRaises(Refused) as refused:
            self.post(notebook, dry_run=False)
        self.assertIn("Typed by hand", str(refused.exception))
        self.post(notebook, dry_run=False, force=True)
        self.assertNotIn("Typed by hand", notebook.pages[0]["html"])

    def test_reposting_after_our_own_post_needs_no_force(self):
        notebook = FakeNotebook([week("2026-11-09", 3)])
        self.post(notebook, dry_run=False)
        self.md.write_text(UPDATE.replace("very low", "recovering"), encoding="utf-8")
        self.assertTrue(self.post(notebook, dry_run=False)["posted"])

    def test_creates_the_weeks_page_when_missing(self):
        notebook = FakeNotebook([week("2026-10-26", 2), week("2026-11-02", 3)])
        result = self.post(notebook, dry_run=False)
        self.assertTrue(result["created"])
        new = notebook.pages[-1]
        self.assertEqual(new["title"], "2026-11-09 – Sprint 3")
        self.assertEqual(pg.headings(new["html"]), ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Architecture"])
        self.assertIn("Registry migration", pg.text(new["html"]))

    def test_a_section_with_more_than_one_list_is_left_alone(self):
        notebook = FakeNotebook([week("2026-11-09", 3, '<ul id="ul:8"><li>a</li></ul><ul id="ul:9"><li>b</li></ul>')])
        with self.assertRaises(Refused):
            self.post(notebook, dry_run=False, force=True)


class Pull(Base):
    def test_writes_only_what_changed(self):
        notebook = FakeNotebook([week("2026-11-02", 3), week("2026-11-09", 3)])
        self.assertEqual(len(pull(self.config, notebook, log=self.lines.append)["written"]), 2)
        self.assertEqual(pull(self.config, notebook, log=self.lines.append)["unchanged"], 2)
        notebook.pages[1]["html"] = notebook.pages[1]["html"].replace("Digital news", "Digital update")
        self.assertEqual(len(pull(self.config, notebook, log=self.lines.append)["written"]), 1)
        self.assertNotIn(b' id="', (self.dir / "out" / "Weekly update 2026-11-09.html").read_bytes())


if __name__ == "__main__":
    unittest.main()
