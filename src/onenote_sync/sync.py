"""pull: the newest pages to files. post: your section of the week's page.

**pull** writes each of the newest pages' HTML (as OneNote serves it, without element ids) to
`[pull] file`, only when it differs from what's there, so a watcher polling every 15 minutes
writes nothing on a quiet day. The files are exactly what Power Automate's OneNote connector
saved, byte for byte (checked 10 Nov 2026), so anything converting them carries on unchanged.

**post**, in order, stopping at the first problem:
1. Refuses a markdown file still marked [DRAFT].
2. Finds the week's page by the date (the file name's, or --date). **If there's none, creates it**
   (GJ, 10 Nov 2026): titled "yyyy-mm-dd – Sprint N", with N worked out from the last pages
   (sprints run sprint_weeks weeks), and the [post] headings.
3. Finds your heading and what's under it. Only one bulleted list, or nothing, can be replaced;
   anything more (Highlights/Challenges, a table) is left for a person.
4. **Stops if the section was changed by hand** since this tool last posted to it (or holds
   anything, if it never has), showing what's there. --force replaces it anyway.
5. Saves the page's HTML to the backups folder, then replaces only that list.
6. Reads the page back: your section must read as posted, and every other section as it was.
"""

from __future__ import annotations

import datetime as dt
import difflib
import json
import pathlib
import re

from . import page as pg
from .config import Config
from .onenote import Notebook, sprint_for


def _load(path: pathlib.Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}


def _save(path: pathlib.Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    temp.replace(path)


def pull(config: Config, notebook: Notebook, dry_run: bool = False, log=print) -> dict:
    if config.pull is None:
        raise ValueError(f"{config.path} has no [pull] section.")
    pages = notebook.dated_pages()[-config.pull.newest:]
    written, unchanged = [], 0
    for item in pages:
        target = config.pull.folder / config.pull.file.format(date=item["date"])
        html = notebook.content(item["id"])
        if target.exists() and target.read_bytes() == html:
            unchanged += 1
            continue
        if not dry_run:
            target.parent.mkdir(parents=True, exist_ok=True)
            temp = target.with_name(target.name + ".tmp")
            temp.write_bytes(html)
            temp.replace(target)
        written.append(str(target))
        log(f"{'would write' if dry_run else 'wrote'}  {target.name} ({item['title']})")
    log(f"pull: {len(written)} written, {unchanged} unchanged")
    return {"written": written, "unchanged": unchanged}


class Refused(RuntimeError):
    """post stopped before changing anything; the message says why and what to do."""


def week_of(markdown_path: pathlib.Path, date: str | None) -> str:
    if date:
        return dt.date.fromisoformat(date).isoformat()
    match = re.search(r"\d{4}-\d{2}-\d{2}", markdown_path.name)
    if not match:
        raise Refused(f"{markdown_path.name} has no date in its name; pass --date yyyy-mm-dd.")
    return match.group(0)


def _outside(html: str, section: str) -> str:
    """Every section's text except this one, to check a post touched nothing else."""
    _, under = pg.section(html, section)
    mine = {b[2] for b in under}
    return "\n".join(pg.text(b[2]) for b in pg.blocks(html) if b[2] not in mine)


def post(config: Config, notebook: Notebook, markdown_path: pathlib.Path, date: str | None = None,
         dry_run: bool = True, force: bool = False, log=print) -> dict:
    if config.post is None:
        raise ValueError(f"{config.path} has no [post] section.")
    name = config.post.section
    markdown = markdown_path.read_text(encoding="utf-8")
    if "[DRAFT]" in markdown:
        raise Refused(f"{markdown_path.name} is still marked [DRAFT]. Review it and remove the marker first.")
    items = pg.bullets(markdown, name)
    if not items:
        raise Refused(f"No bullets under a '# {name}' heading in {markdown_path.name}.")
    week = week_of(markdown_path, date)
    new_text = pg.plain(items)

    found, created = notebook.page_for(week), False
    if found is None:
        earlier = [p for p in notebook.dated_pages() if p["date"] < week]
        number, why = sprint_for(week, earlier, config.post.sprint_weeks)
        title = f"{week} – Sprint {number}"
        log(f"No page for {week} yet: {'would create' if dry_run else 'creating'} '{title}' ({why}), "
            f"with headings {', '.join(config.post.headings)}")
        if dry_run:
            log(f"\n{name}, as it would be posted:\n{new_text}")
            return {"page": title, "created": False, "posted": False}
        found = {"id": notebook.create(pg.new_page(title, config.post.headings))["id"], "title": title, "date": week}
        created = True
        log(f"created '{title}'")

    state = _load(config.state_file)
    before = notebook.content(found["id"], ids=True, new=created).decode("utf-8")
    head, under = pg.section(before, name)
    if head is None:
        raise Refused(f"'{found['title']}' has no '{name}' heading. Its headings: {', '.join(pg.headings(before))}.")
    lists = [b for b in under if b[0] in ("ul", "ol")]
    other = [b for b in under if b[0] not in ("ul", "ol") and pg.text(b[2])]
    if len(lists) > 1 or other:
        raise Refused(f"'{name}' on '{found['title']}' holds more than one list ({len(lists)} lists, {len(other)} other "
                      "blocks), so it's left for you to edit in OneNote.")
    current = pg.text(lists[0][2]) if lists else ""
    last = state.get(found["id"], {}).get(name)
    if current == new_text:
        log(f"'{name}' on '{found['title']}' already reads as posted; nothing to do.")
        return {"page": found["title"], "created": created, "posted": False}
    changed_by_hand = current and current != last
    diff = "\n".join(difflib.unified_diff(current.split("\n"), new_text.split("\n"), "on the page now", "to post", lineterm="", n=0))
    if changed_by_hand and not force:
        raise Refused(f"'{name}' on '{found['title']}' "
                      + ("has content this tool didn't post" if last is None else "was changed by hand since the last post")
                      + f". Here's the difference; run again with --force to replace it:\n{diff}")
    if dry_run:
        log(f"Would replace '{name}' on '{found['title']}':\n{diff or new_text}")
        return {"page": found["title"], "created": False, "posted": False}

    config.backups.mkdir(parents=True, exist_ok=True)
    backup = config.backups / f"{week}-{dt.datetime.now():%Y%m%d-%H%M%S}.html"
    backup.write_text(before, encoding="utf-8")
    content = pg.render(items)
    change = ({"target": lists[0][1], "action": "replace", "content": content} if lists
              else {"target": head[1], "action": "insert", "position": "after", "content": content})
    notebook.patch(found["id"], [change])

    after = notebook.content(found["id"], ids=True).decode("utf-8")
    if pg.section(after, name)[0] is None:
        # OneNote sometimes keeps a heading inside the list that follows it, so replacing the list takes the
        # heading too (found on a real weekly page, 10 Nov 2026; not every page does it). Put it back.
        new_list = next((b for b in pg.blocks(after) if b[0] in ("ul", "ol") and pg.text(b[2]) == new_text), None)
        if new_list is None:
            raise RuntimeError(f"Posted, but the '{name}' heading went and the new list can't be found to put it back. "
                               f"The page before the post is in {backup}.")
        heading = re.sub(r'\s(id|data-id)="[^"]*"', "", head[2])
        notebook.patch(found["id"], [{"target": new_list[1], "action": "insert", "position": "before", "content": heading}])
        after = notebook.content(found["id"], ids=True).decode("utf-8")
        log(f"OneNote dropped the '{name}' heading with the old list; put it back")
    _, now_under = pg.section(after, name)
    posted = pg.text("".join(b[2] for b in now_under))
    if posted != new_text:
        raise RuntimeError(f"Posted, but '{name}' reads back differently. The page before the post is in {backup}.\n"
                           + "\n".join(difflib.unified_diff(new_text.split("\n"), posted.split("\n"), "meant", "on the page", lineterm="", n=0)))
    if _outside(after, name) != _outside(before, name):
        raise RuntimeError(f"Posted, but another section of '{found['title']}' changed at the same time (perhaps someone "
                           f"was editing). Check the page; the version before the post is in {backup}.")
    state.setdefault(found["id"], {})[name] = new_text
    _save(config.state_file, state)
    log(f"posted '{name}' to '{found['title']}': {len(items)} bullets (backup: {backup.name})")
    return {"page": found["title"], "created": created, "posted": True}
