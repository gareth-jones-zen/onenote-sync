# onenote-sync

For a team that writes a shared OneNote page each week, one heading per team:

- **pull** keeps the newest pages as files, refreshed within minutes of an edit, for anything that
  reads them (a script, an assistant, a converter to markdown);
- **post** puts your team's section onto the week's page from a markdown file, replacing only your
  section, and creates the week's page if nobody has yet.

```
onenote-sync pull
  wrote  Weekly update 2026-11-09.html (2026-11-09 – Sprint 3)
onenote-sync post 2026-11-09.md          # shows what would change; nothing is posted
onenote-sync post 2026-11-09.md --yes
  posted 'Architecture' to '2026-11-09 – Sprint 3': 7 bullets
```

## Install

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/m365-graph --preinstall git+https://github.com/gareth-jones-zen/win-background git+https://github.com/gareth-jones-zen/onenote-sync
```

[m365-graph](https://github.com/gareth-jones-zen/m365-graph) does the sign-in and Graph requests;
[win-background](https://github.com/gareth-jones-zen/win-background) does the watcher's task,
notifications, the locks and the log. Neither is on PyPI, so both are installed first.

**It needs Notes.ReadWrite.All** on Microsoft's "Graph Command Line Tools" app for a notebook in
someone else's OneDrive (Notes.ReadWrite reaches only your own). Many organisations need an admin
to approve it: the first `sign-in` shows an "Approval required" screen, where you can ask.

## Settings

`%APPDATA%\onenote-sync\config.toml` by default (`--config` or `ONENOTE_SYNC_CONFIG` for another).

```toml
login_hint = "you@example.com"

[notebook]
owner = "colleague@example.com"     # whose OneDrive holds it; "me" for your own
name = "Weekly Team Updates"
section = "Weekly Updates"

[pull]
folder = "~/OneDrive/Weekly updates"
file = "Weekly update {date}.html"  # {date}: the yyyy-mm-dd the page title starts with
newest = 2
every_minutes = 15                  # watch mode, between these hours, Monday to Friday
hours = "07:00-19:00"
weekdays = true

[post]
section = "My team"                 # your heading, on the page and in your markdown
headings = ["Executive Summary", "Digital", "My team"]   # for a page this tool creates
sprint_weeks = 2
```

Also: `msal_cache` (share another tool's sign-in), `tenant`, `state_file`, `log_file`, `backups`.

## Pull

Each of the newest pages is written as OneNote serves its HTML, **only when it differs from the
file already there**, so a watcher polling every 15 minutes writes nothing on a quiet day. The files
are byte for byte what Power Automate's OneNote connector saves, so a flow can be swapped for this
without changing anything downstream. `onenote-sync autostart on` runs the watcher at every logon.

**Pages are found by the date their title starts with** (`2026-11-09 – Sprint 3`), never by
OneNote's dates: a page copied from a template keeps the template's `lastModifiedDateTime` (pages
from November 2026 reported November 2024), and ordering by creation date put an untitled page
first. Untitled pages and a `TEMPLATE` page are ignored.

## Post

Your markdown needs a `# <section>` heading with bullets under it: `- **Topic** - text`, with
*italics*, and nested bullets indented two spaces. Anything after the next heading or a `---` rule
is never posted, so review notes can sit underneath.

**Without `--yes` it only shows what would change.** With it, in order, stopping at the first problem:

1. **It refuses a file still marked `[DRAFT]`.**
2. **It finds the week's page** by the date in the file name (or `--date`). **If there isn't one, it
   creates it**: titled `yyyy-mm-dd – Sprint N`, with your `headings`. N comes from the pages before
   it: a sprint that has had `sprint_weeks` weekly pages is over, so the new page starts the next one.
   If the previous week's page is missing it stops rather than guess. **Check the title in the
   dry run when sprint numbering restarts** (a new year, say): it can't know that.
3. **It finds your heading and what's under it.** One list, or nothing, can be replaced. A section
   with more (Highlights / Challenges lists, a table) is left for you to edit in OneNote.
4. **It stops if your section was changed by hand** since it last posted there (or holds anything,
   if it never has), and shows the difference. `--force` replaces it anyway.
5. **It saves the page's HTML to `backups`**, then replaces only your list.
6. **It reads the page back:** your section must read exactly as posted, and every other section as
   it was. Otherwise it says so, and where the backup is.

**OneNote sometimes takes the heading with the list it replaces.** It keeps some headings inside the
list that follows them. That happened on a real weekly page and not on others, so after replacing,
post checks for your heading and puts it back if it's gone. OneNote's API has no delete, and an
empty replacement is refused, so this is the only way to replace a list wholesale.

A page the tool has just created can answer "not found" for a few seconds; it waits.

## Tests

```
python -m unittest discover -s tests
```

A fake notebook applies PATCHes as OneNote does, including dropping a heading.
