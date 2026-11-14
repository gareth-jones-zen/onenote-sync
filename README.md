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

Windows (x64) only; nothing else is needed.

1. Download `onenote-sync-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/onenote-sync/releases).
2. Unzip it to `%LOCALAPPDATA%\Programs\onenote-sync`.
3. Run the commands below from that folder, or add it to your `PATH`.

It holds two programs: `onenote-sync.exe`, for a console, and `onenote-sync-background.exe`, the
same program with no window, which the logon task runs. To upgrade, run `onenote-sync autostart off`,
unzip the newer release over the same folder, then `onenote-sync autostart on`.

**It needs Notes.ReadWrite.All** on Microsoft's "Graph Command Line Tools" app for a notebook in
someone else's OneDrive (Notes.ReadWrite reaches only your own). Many organisations need an admin
to approve it: the first `sign-in` shows an "Approval required" screen, where you can ask.

## Commands

```
onenote-sync config                 create the settings file (if missing) and say where it is
onenote-sync sign-in                sign in to Microsoft (once; opens a browser)
onenote-sync pages                  the dated pages in the section, newest last
onenote-sync pull [--dry-run]       write the newest pages to files, where they've changed
onenote-sync post <file.md>         show what would be posted to your section (changes nothing)
onenote-sync post <file.md> --yes   post it (--force replaces a section changed by hand)
onenote-sync watch                  pull on a timer; what autostart runs
onenote-sync autostart on|off|status
onenote-sync --version
```

`--config <file>`, before the command, uses another settings file. `post --date yyyy-mm-dd` names
the week's Monday when the file name has no date. A `pull` or `post` run by hand waits for another
one already in progress (up to 10 minutes).

**Exit codes:** 0 done; 1 refused or failed; 2 settings, arguments or sign-in; 3 another watcher is
already running.

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

Also:
- `msal_cache`: another tool's token cache, to share its sign-in;
- `tenant`: a tenant id, to pin sign-in to one organisation;
- `state_file`, `log_file`, `backups`: by default under `%LOCALAPPDATA%\onenote-sync\`.

Paths may use `~` and `%VARIABLES%`; a relative path is relative to the settings file. The file is
read strictly: a key or table given twice is an error, not a silent override.

## Pull

Each of the newest pages is written as OneNote serves its HTML, **only when it differs from the
file already there**, so a watcher polling every 15 minutes writes nothing on a quiet day. The files
are byte for byte what Power Automate's OneNote connector saves, so a flow can be swapped for this
without changing anything downstream.

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
   dry run when sprint numbering restarts** (a new year, say): it can't know that. **A page it
   creates goes at the bottom of the section**, because Graph adds new pages at the end and can't
   move them, so drag it to the top in OneNote. The tool says so each time.
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

**A change is never sent twice.** Reads are retried after a busy or failed answer, but a change (a
PATCH, or a new page) is sent again only after a 429, which is refused before it's looked at, or a
connection that never opened. After a 5xx, a timeout or a dropped connection it may have been
applied, so it isn't resent: a lost reply to a PATCH is settled by reading the page back (step 6), and
a lost reply to a new page stops the post and says to look in OneNote before running it again. A page
the tool has just created can answer "not found" for a few seconds; it waits.

## Keeping it current: `watch` and `autostart`

```
onenote-sync autostart on       # watch at every logon, and start it now
onenote-sync autostart status
onenote-sync autostart off
onenote-sync watch              # the same loop, in a console
```

`watch` pulls every `every_minutes`, within `hours`, and on weekdays only unless `weekdays = false`.

- **One watcher per settings file.** A second one exits at once, with code 3.
- **It never opens a browser.** A lapsed sign-in is logged and notified at once, and it keeps trying
  until someone runs `sign-in`.
- **A failure is notified only after three in a row**, and once until a pull succeeds: a single
  dropped connection that the next run gets past isn't something to act on.
- **It logs `alive` hourly.** A silent log means a stuck watcher, not a quiet page.

**`autostart on`** copies both programs to `%LOCALAPPDATA%\Programs\onenote-sync` (unless they're
running from there already) and registers a Task Scheduler task, `onenote-sync watch`:
- it runs as you, only while you're logged on, with no admin rights;
- it catches up a missed start, restarts the watcher after a crash, and is never stopped for
  running too long;
- it runs `onenote-sync-background.exe --config <settings> watch` directly, so there's no window,
  and stopping the task stops the watcher.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/OneNoteSync.Console -c Release -r win-x64 -o out
dotnet publish src/OneNoteSync.Background -c Release -r win-x64 -o out
```

The tests use a fake notebook that applies PATCHes as OneNote does, including dropping a heading.
A tag `v*` builds and publishes a release.
