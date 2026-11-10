"""onenote-sync: a shared OneNote section's newest pages, pulled to files; and your section of the week's page, posted.

    onenote-sync config                 create the settings file (if missing) and say where it is
    onenote-sync sign-in                sign in to Microsoft (once; opens a browser)
    onenote-sync pages                  the dated pages in the section, newest last
    onenote-sync pull [--dry-run]       write the newest pages to files, where they've changed
    onenote-sync post <file.md>         show what would be posted to your section (changes nothing)
    onenote-sync post <file.md> --yes   post it (--force replaces a section changed by hand)
    onenote-sync watch                  pull on a timer; what autostart runs
    onenote-sync autostart on           the watcher at every logon, and now (off, status)
"""

from __future__ import annotations

import argparse
import pathlib
import sys

from m365_graph import Client, SignInNeeded
from win_background import exclusive, key, logger

from . import __version__, autostart, config as cfg
from .onenote import SCOPES, Notebook
from .sync import Refused, post, pull
from .watch import watch


def notebook_for(config: cfg.Config, interactive: bool | None = None) -> Notebook:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    graph = Client(tenant=config.tenant, login_hint=config.login_hint, scopes=SCOPES, interactive=interactive,
                   sign_in_hint="At a console, run: onenote-sync sign-in", **kwargs)
    return Notebook(graph, config.owner, config.notebook, config.section)


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="onenote-sync", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\onenote-sync\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    sub.add_parser("sign-in", help="sign in to Microsoft (opens a browser)")
    sub.add_parser("pages", help="the dated pages in the section")
    p = sub.add_parser("pull", help="write the newest pages to files")
    p.add_argument("--dry-run", action="store_true")
    p = sub.add_parser("post", help="post your section of the week's page")
    p.add_argument("file", type=pathlib.Path, help="markdown with a '# <section>' heading and bullets under it")
    p.add_argument("--date", help="the week's Monday, if the file name has no date")
    p.add_argument("--yes", action="store_true", help="post it; without this, only show what would change")
    p.add_argument("--force", action="store_true", help="replace the section even if it was changed by hand")
    sub.add_parser("watch", help="pull on a timer")
    p = sub.add_parser("autostart", help="the watcher at logon (Task Scheduler)")
    p.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    try:
        config = cfg.load(args.config)
    except (FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
    if args.command == "autostart":
        return _autostart(args.action, config)
    if args.command == "watch":
        if config.pull is None:
            print(f"{config.path} has no [pull] section, so there's nothing to watch.", file=sys.stderr)
            return 2
        return watch(config, lambda c: notebook_for(c, interactive=False))

    log = logger(config.log_file, stamp_echo=False)
    try:
        notebook = notebook_for(config)
        if args.command == "sign-in":
            notebook.graph.token(SCOPES, interactive=True)
            print("Signed in.")
            return 0
        if args.command == "pages":
            for item in notebook.dated_pages():
                print(item["title"])
            return 0
        with exclusive(f"onenote-sync-run-{key(config.path)}", 600, cfg.LOCAL, what="onenote-sync run"):
            if args.command == "pull":
                pull(config, notebook, args.dry_run, log)
            else:
                post(config, notebook, args.file, args.date, dry_run=not args.yes, force=args.force, log=log)
                if not args.yes:
                    print("\nNothing has changed. Run again with --yes to post.")
        return 0
    except Refused as error:
        log(f"not posted: {error}")
        return 1
    except (SignInNeeded, LookupError, ValueError) as error:
        log(str(error))
        return 2


def _autostart(action: str, config: cfg.Config) -> int:
    if action == "on":
        if config.pull is None:
            print(f"{config.path} has no [pull] section, so there's nothing to watch.", file=sys.stderr)
            return 2
        print(f"Autostart is on: '{autostart.TASK}' runs at every logon, and has started now.\n  {autostart.enable(config.path)}")
    elif action == "off":
        print("Autostart is off." if autostart.disable() else "Autostart was already off.")
    else:
        info = autostart.status()
        if not info:
            print("Autostart is off. `onenote-sync autostart on` turns it on.")
        else:
            print(f"Autostart is on ('{autostart.TASK}').")
            for name in ("state", "last_run", "last_result", "command"):
                print(f"  {name.replace('_', ' '):<12} {info.get(name, '')}")
    return 0
