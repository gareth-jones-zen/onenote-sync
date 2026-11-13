"""The OneNote calls, through Microsoft Graph.

Two things about OneNote's API shaped this (found on a real weekly-update notebook, 10 Nov 2026):
- **A page's lastModifiedDateTime can't be trusted.** Pages copied from a template keep the
  template's: 2026 pages reported 18 Nov 2024. So nothing here decides by date; pull compares content.
- **Pages are found by the date their title starts with**, not by createdDateTime, which put an
  untitled page and a May page first. Untitled pages and a "TEMPLATE" page are ignored.

Page content isn't JSON, so it's fetched (and new pages sent) as raw HTML with the client's token.
"""

from __future__ import annotations

import datetime as dt
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request

from m365_graph import resend_ok

GRAPH = "https://graph.microsoft.com/v1.0"
SCOPES = ["Notes.ReadWrite.All"]
DATED = re.compile(r"^(\d{4}-\d{2}-\d{2})\b")


class Notebook:
    def __init__(self, graph, owner: str, notebook: str, section: str):
        """graph is an m365_graph.Client. owner is an email, or "me"."""
        self.graph = graph
        self.root = "/me/onenote" if owner == "me" else f"/users/{urllib.parse.quote(owner)}/onenote"
        self.notebook_name, self.section_name = notebook, section
        self._section = None

    def section_id(self) -> str:
        if self._section is None:
            notebooks = list(self.graph.pages(f"{self.root}/notebooks?$select=id,displayName", scopes=SCOPES))
            notebook = next((n for n in notebooks if n["displayName"].lower() == self.notebook_name.lower()), None)
            if notebook is None:
                raise LookupError(f"No notebook called {self.notebook_name!r} (found: {', '.join(n['displayName'] for n in notebooks)})")
            sections = list(self.graph.pages(f"{self.root}/notebooks/{notebook['id']}/sections?$select=id,displayName", scopes=SCOPES))
            section = next((s for s in sections if s["displayName"].lower() == self.section_name.lower()), None)
            if section is None:
                raise LookupError(f"No section called {self.section_name!r} in {self.notebook_name!r}")
            self._section = section["id"]
        return self._section

    def dated_pages(self) -> list:
        """[{id, title, date}] for every page whose title starts with a date, oldest first."""
        pages = self.graph.pages(f"{self.root}/sections/{self.section_id()}/pages?$select=id,title&$top=100", scopes=SCOPES)
        found = []
        for page in pages:
            match = DATED.match(page.get("title") or "")
            if match:
                found.append({"id": page["id"], "title": page["title"], "date": match.group(1)})
        return sorted(found, key=lambda p: (p["date"], p["title"]))

    def page_for(self, date: str) -> dict | None:
        return next((p for p in self.dated_pages() if p["date"] == date), None)

    # ---- raw HTML ---------------------------------------------------------------------------

    def _raw(self, url: str, method: str = "GET", data: bytes | None = None, content_type: str | None = None) -> bytes:
        for attempt in range(1, 6):
            headers = {"Authorization": f"Bearer {self.graph.token(SCOPES)}"}
            if content_type:
                headers["Content-Type"] = content_type
            request = urllib.request.Request(GRAPH + url, data=data, method=method, headers=headers)
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    return response.read()
            except urllib.error.HTTPError as error:
                # A PATCH or POST that may have landed isn't resent: it would insert the list twice, or
                # create the week's page twice (13 Nov 2026). The next post sees what actually happened.
                landed = "" if resend_ok(method, url, error) else " (may have been applied; not resent)"
                if error.code not in (429, 500, 502, 503, 504) or attempt == 5 or landed:
                    detail = error.read().decode("utf-8", "replace")[:300]
                    raise RuntimeError(f"OneNote said {error.code} to {method} {url[:80]}{landed}: {detail}") from error
                time.sleep(int(error.headers.get("Retry-After") or 5 * attempt))
            except (urllib.error.URLError, TimeoutError) as error:
                if attempt == 5 or not resend_ok(method, url, error):
                    raise
                time.sleep(5 * attempt)

    def content(self, page_id: str, ids: bool = False, new: bool = False) -> bytes:
        """The page's HTML. ids=True adds each element's id, which a PATCH needs as its target.
        new=True: the page was just created, and OneNote can answer 404 for some seconds (10 Nov 2026)."""
        url = f"{self.root}/pages/{page_id}/content" + ("?includeIDs=true" if ids else "")
        for attempt in range(12 if new else 1):
            try:
                return self._raw(url)
            except RuntimeError as error:
                if not new or "said 404" not in str(error) or attempt == 11:
                    raise
                time.sleep(5)

    def patch(self, page_id: str, changes: list) -> None:
        self._raw(f"{self.root}/pages/{page_id}/content", "PATCH", json.dumps(changes).encode("utf-8"), "application/json")

    def create(self, html: str) -> dict:
        body = self._raw(f"{self.root}/sections/{self.section_id()}/pages", "POST", html.encode("utf-8"), "text/html")
        return json.loads(body)


def sprint_for(date: str, earlier: list, sprint_weeks: int = 2) -> tuple:
    """The sprint number for a new week's page, from the pages before it: (number, how it was worked out).

    Sprints run sprint_weeks weeks, so a sprint that has already had that many weekly pages is over and
    the new page is the next one; otherwise it's the same sprint. Raises if the earlier titles don't say."""
    numbered = []
    for page in earlier:
        match = re.search(r"Sprint\s+(\d+)", page["title"], re.I)
        if match and page["date"] < date:
            numbered.append((page["date"], int(match.group(1))))
    if not numbered:
        raise ValueError("No earlier page has a sprint number in its title, so the new one can't be worked out.")
    numbered.sort()
    last_date, last = numbered[-1]
    run = [n for d, n in numbered[-sprint_weeks:] if n == last]
    gap = (dt.date.fromisoformat(date) - dt.date.fromisoformat(last_date)).days
    if gap != 7:
        raise ValueError(f"The last page is {last_date}, {gap} days before {date}, so the sprint can't be worked out "
                         "from it. Create the page by hand, or the missing week's first.")
    if len(run) >= sprint_weeks:
        return last + 1, f"{last_date} was week {len(run)} of {sprint_weeks} of Sprint {last}, so this is Sprint {last + 1}"
    return last, f"{last_date} was week {len(run)} of {sprint_weeks} of Sprint {last}, so this is week {len(run) + 1}"
