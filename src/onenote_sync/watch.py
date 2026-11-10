"""Watch mode: pull every every_minutes, in working hours, so the files are never far behind the page.

- **One watcher per settings file.** A second exits at once (code 3).
- **It never opens a browser.** A lapsed sign-in is logged and notified once, and it keeps trying.
- **It logs `alive` once an hour**, so a silent log means a stuck watcher, not a quiet page.
- **A failure is notified once, until a pull succeeds again**, not every 15 minutes.
"""

from __future__ import annotations

import datetime as dt
import time

from m365_graph import SignInNeeded
from win_background import key, logger, notify, single_instance

from . import config as cfg
from .sync import pull

ALREADY_RUNNING = 3


def due(pull_settings: cfg.Pull, now: dt.datetime) -> bool:
    if pull_settings.weekdays and now.weekday() >= 5:
        return False
    start, end = pull_settings.hours
    return start <= now.strftime("%H:%M") < end


def watch(config: cfg.Config, notebook_for) -> int:
    """notebook_for(config) -> Notebook, made fresh each time so a settings change is picked up."""
    log = logger(config.log_file)
    lock = single_instance(f"onenote-sync-watch-{key(config.path)}", cfg.LOCAL)   # held until the process ends
    if lock is None:
        log("another watcher is already running for these settings; not starting a second")
        return ALREADY_RUNNING
    log(f"watching '{config.notebook}' / '{config.section}' every {config.pull.every_minutes} min, "
        f"{'-'.join(config.pull.hours)}{' Mon-Fri' if config.pull.weekdays else ''}")
    next_pull, beat, failing = 0.0, time.time(), None
    while True:
        if time.time() >= next_pull:
            next_pull = time.time() + config.pull.every_minutes * 60
            if due(config.pull, dt.datetime.now()):
                try:
                    config = cfg.load(config.path)
                    pull(config, notebook_for(config), log=log)
                    failing = None
                except SignInNeeded as error:
                    log(f"sign-in needed: {error}")
                    if failing != "sign-in":
                        notify("onenote-sync needs a sign-in", [str(error)[:300]])
                    failing = "sign-in"
                except Exception as error:
                    log(f"pull failed: {type(error).__name__}: {error}")
                    if failing != type(error).__name__:
                        notify("onenote-sync couldn't pull", [f"{type(error).__name__}: {error}"[:300], f"Log: {config.log_file}"])
                    failing = type(error).__name__
        if time.time() - beat >= 3600:
            beat = time.time()
            log("alive")
        time.sleep(30)
