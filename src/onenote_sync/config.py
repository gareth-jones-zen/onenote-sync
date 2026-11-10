"""The settings file.

    login_hint = "you@example.com"          # optional
    tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
    msal_cache = "..."                      # optional: share another tool's sign-in
    state_file = "..."                      # optional: what was last posted to each page
    log_file = "..."                        # optional
    backups = "..."                         # optional: each page's HTML, saved before every post

    [notebook]
    owner = "colleague@example.com"         # whose OneDrive the notebook is in; "me" for your own
    name = "Weekly Team Updates"
    section = "Weekly Updates"

    [pull]                                  # optional: the newest pages, to files
    folder = "~/OneDrive/Weekly updates"
    file = "Weekly update {date}.html"      # {date} is the yyyy-mm-dd the page's title starts with
    newest = 2                              # how many of the newest pages
    every_minutes = 15                      # watch mode
    hours = "07:00-19:00"                   # watch mode: only between these times...
    weekdays = true                         # ...and only Monday to Friday

    [post]                                  # optional: your section of the week's page
    section = "Architecture"                # the heading on the page, and in your markdown file
    headings = ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Architecture"]
                                            # for a page this tool creates, in order
    sprint_weeks = 2                        # for a new page's "Sprint N"

Pages are found by the date their title starts with, "yyyy-mm-dd – Sprint N", one per week.
Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.
"""

from __future__ import annotations

import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "onenote-sync"
DEFAULT_CONFIG = APPDATA / "onenote-sync" / "config.toml"

TEMPLATE = """\
# onenote-sync settings. See the README.
# login_hint = "you@example.com"

[notebook]
owner = "me"                  # or the email of whoever's OneDrive holds the notebook
name = "My Notebook"
section = "Weekly Updates"

# [pull]
# folder = "~/Documents/Weekly updates"
# file = "Weekly update {date}.html"

# [post]
# section = "My team"
# headings = ["Summary", "My team"]
"""


@dataclass
class Pull:
    folder: pathlib.Path
    file: str = "Weekly update {date}.html"
    newest: int = 2
    every_minutes: int = 15
    hours: tuple = ("07:00", "19:00")
    weekdays: bool = True


@dataclass
class Post:
    section: str
    headings: list = field(default_factory=list)
    sprint_weeks: int = 2


@dataclass
class Config:
    path: pathlib.Path
    owner: str
    notebook: str
    section: str
    pull: Pull | None = None
    post: Post | None = None
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    state_file: pathlib.Path = LOCAL / "state.json"
    log_file: pathlib.Path = LOCAL / "onenote-sync.log"
    backups: pathlib.Path = LOCAL / "backups"


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return path if path.is_absolute() else (base / path).resolve()


def load(path: pathlib.Path | str | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("ONENOTE_SYNC_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `onenote-sync config` to create one.")
    raw = tomllib.loads(path.read_text(encoding="utf-8"))
    base = path.parent
    notebook = raw.get("notebook") or {}
    missing = [k for k in ("owner", "name", "section") if not notebook.get(k)]
    if missing:
        raise ValueError(f"[notebook] in {path} needs {', '.join(missing)}.")
    config = Config(
        path=path, owner=notebook["owner"], notebook=notebook["name"], section=notebook["section"],
        login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
        msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
        state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / "state.json",
        log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "onenote-sync.log",
        backups=_path(raw["backups"], base) if raw.get("backups") else LOCAL / "backups")
    if pull := raw.get("pull"):
        if not pull.get("folder"):
            raise ValueError(f"[pull] in {path} needs a folder.")
        if "{date}" not in pull.get("file", "{date}"):
            raise ValueError(f"[pull] file in {path} must contain {{date}}, or every page would be written to one file.")
        hours = str(pull.get("hours", "07:00-19:00"))
        if not re.fullmatch(r"\d\d:\d\d-\d\d:\d\d", hours):
            raise ValueError(f"[pull] hours in {path} should look like \"07:00-19:00\".")
        config.pull = Pull(folder=_path(pull["folder"], base), file=pull.get("file", "Weekly update {date}.html"),
                           newest=int(pull.get("newest", 2)), every_minutes=int(pull.get("every_minutes", 15)),
                           hours=tuple(hours.split("-")), weekdays=bool(pull.get("weekdays", True)))
    if post := raw.get("post"):
        if not post.get("section"):
            raise ValueError(f"[post] in {path} needs a section.")
        headings = list(post.get("headings", []))
        if headings and post["section"].lower() not in [h.lower() for h in headings]:
            raise ValueError(f"[post] headings in {path} don't include your section, {post['section']!r}.")
        config.post = Post(section=post["section"], headings=headings, sprint_weeks=int(post.get("sprint_weeks", 2)))
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
