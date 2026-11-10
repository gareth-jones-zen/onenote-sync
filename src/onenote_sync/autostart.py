"""The watcher at logon, through Windows Task Scheduler (win-background).

It runs `pythonw -m onenote_sync --config <settings> watch` directly, so there's no window, and
stopping the task stops the watcher itself. Restarted if it crashes; never stopped for time.
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "onenote-sync watch"
_task = Task(TASK, "Pulls a shared OneNote section's newest pages to files (onenote-sync watch).",
             results={"3": "another watcher was already running"})


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("onenote_sync", "--config", str(config_path.resolve()), "watch")


def enable(config_path: pathlib.Path) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
