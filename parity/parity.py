"""Side-by-side checks of the .NET port against the Python onenote-sync, offline: recorded pages and fakes only,
never Graph. No page content is kept in this repo; everything recorded or derived goes to the parity folder
(default %LOCALAPPDATA%\\claude-ea\\onenote-sync-parity), which is deleted afterwards.

    python -I parity/parity.py prepare --src PY_SRC --pages DIR [DIR ...] --updates DIR     # id-bearing copies, the manifest
    python -I parity/parity.py python --src PY_SRC                              # the Python's results
    <onenote-sync-parity.exe> all PARITY_DIR                                    # the .NET's results
    python -I parity/parity.py compare                                          # differences, with numbers

PY_SRC is a checkout of the Python version (src/ and tests/), e.g. a git worktree of the tag v0.1.0.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import sys
import tempfile

PARITY = pathlib.Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "claude-ea" / "onenote-sync-parity"
CREATE_UPDATE = "# Architecture\n- created **here** – £1\n"
HEADINGS = ["Executive Summary", "Digital", "Fulfilment", "Assurance", "Service", "Architecture"]
VARIANTS = ["recorded", "emptied", "by-hand", "two-lists", "dropped-heading"]
SETTINGS = """state_file = "state.json"
log_file = "sync.log"
backups = "backups"
[notebook]
owner = "someone@example.com"
name = "Weekly"
section = "Updates"
[pull]
folder = "out"
file = "Weekly update {date}.html"
[post]
section = "Architecture"
headings = %s
sprint_weeks = 2
""" % json.dumps(HEADINGS)

# Synthetic edge cases: written by this script, never real content.
EDGE_UPDATES = {
    "edge-entities": "# Architecture\n\n- R&D <b> costs £5 – \"quoted\" 'single' \U0001F60A\n- a\ttab and &amp; literal\n",
    "edge-nesting": "# Architecture\n- top\n  - two\n    - three\n      - four\n        - five\n\t- tabbed\n- back\n",
    "edge-markup": "# Architecture\n- **bold** and *italic* and ***both***\n- *x y* and * not* and **\n- trailing *star\n",
    "edge-wrapped": "---\ntitle: x\n---\n# Other\n- not this\n# Architecture\n- one\n  continues here\n- two\n---\n- never\n",
    "edge-crlf": "# Architecture\r\n- crlf line\r\n- second\r\n",
    "edge-cr": "# Architecture\r- lone cr\r- second\r",
    "edge-first-heading": "## Some other title\n- falls back to the first heading\n",
    "edge-no-bullets": "# Architecture\n\nNo bullets here.\n",
    "edge-draft": "# Architecture\n- [DRAFT] still marked\n",
    "edge-spaces": "# Architecture\n-    spaced   \n*  star bullet\n- x\x1cy\n",
}


def sha(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8", "surrogatepass")).hexdigest()


# ---- prepare ------------------------------------------------------------------------------------

def add_ids(html: str) -> str:
    """Give every element that a PATCH could target an id, as OneNote's includeIDs=true does."""
    counter = iter(range(1, 1_000_000))
    return re.sub(r"<(p|ul|ol|li|table|div|img|object|span)\b(?![^>]*\bid=)",
                  lambda m: f'<{m.group(1)} id="{m.group(1)}:{{{next(counter)}}}"', html)


def prepare(args) -> None:
    if PARITY.exists():
        shutil.rmtree(PARITY)
    (PARITY / "pages").mkdir(parents=True)
    (PARITY / "updates").mkdir()
    pages = []
    for folder in args.pages:
        for path in sorted(pathlib.Path(folder).glob("*.html")):
            match = re.search(r"(\d{4}-\d{2}-\d{2})", path.name)
            if not match:
                continue
            raw = path.read_bytes()
            html = raw.decode("utf-8")
            title = re.search(r"<title>(.*?)</title>", html).group(1)
            name = f"{pathlib.Path(folder).name[:8]}-{match.group(1)}"
            (PARITY / "pages" / f"{name}.html").write_bytes(raw)
            (PARITY / "pages" / f"{name}.ids.html").write_bytes(add_ids(html).encode("utf-8"))
            pages.append({"name": name, "date": match.group(1), "title": title,
                          "file": f"pages/{name}.html", "ids": f"pages/{name}.ids.html"})
    updates = []
    for path in sorted(pathlib.Path(args.updates).glob("*.md")):
        shutil.copyfile(path, PARITY / "updates" / path.name)
        updates.append({"name": path.stem, "file": f"updates/{path.name}"})
    for name, text in EDGE_UPDATES.items():
        (PARITY / "updates" / f"{name}.md").write_bytes(text.encode("utf-8"))
        updates.append({"name": name, "file": f"updates/{name}.md"})
    # A synthetic page, so the edge cases also run against something with nothing real in it.
    synthetic = ('<html><head><title>2026-11-09 – Sprint 3</title></head><body><div style="position:absolute">'
                 '<p><span style="font-size:18pt;font-weight:bold">Executive Summary</span></p><br />'
                 '<p><span style="font-size:14pt;color:black;font-weight:bold">Digital</span></p><ul><li><span>D &amp; &#163;</span></li></ul><br />'
                 '<p><span style="font-weight:bold;font-size:14.5pt">Architecture:</span></p><ul><li><span>Old – one</span><ul><li>nested</li></ul></li></ul>'
                 '<table><tr><td>t</td></tr></table><div><p>inner</p></div></div></body></html>')
    (PARITY / "pages" / "synthetic.html").write_bytes(synthetic.encode("utf-8"))
    (PARITY / "pages" / "synthetic.ids.html").write_bytes(add_ids(synthetic).encode("utf-8"))
    pages.append({"name": "synthetic", "date": "2026-11-09", "title": "2026-11-09 – Sprint 3",
                  "file": "pages/synthetic.html", "ids": "pages/synthetic.ids.html"})
    (PARITY / "settings.toml").write_text(SETTINGS, encoding="utf-8")
    cases = make_cases(args.src, pages)
    manifest = {"pages": pages, "updates": updates, "variants": VARIANTS, "headings": HEADINGS, "cases": cases}
    (PARITY / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    print(f"{len(pages)} pages, {len(updates)} updates -> {PARITY}")


def make_cases(src: str, pages: list) -> list:
    """Each page in each variant, written out, so both sides post into exactly the same bytes."""
    _, pg, _, _, _ = load_python(pathlib.Path(src).resolve())
    (PARITY / "cases").mkdir()
    cases = []
    week = "2026-11-09"
    for page in pages:
        for variant in VARIANTS:
            html = (PARITY / page["ids"]).read_bytes().decode("utf-8")
            head, under = pg.section(html, "Architecture")
            state, drops, force = None, False, False
            if variant == "emptied":
                for block in under:
                    html = html.replace(block[2], "", 1)
            elif variant == "two-lists" and head is not None:
                html = html.replace(head[2], head[2] + '<ul id="ul:extra"><li>extra</li></ul>', 1)
            elif variant == "by-hand":
                current = pg.text("".join(b[2] for b in under if b[0] in ("ul", "ol")))
                state = json.dumps({week: {"Architecture": current + " (earlier)"}})
                force = True
            elif variant == "dropped-heading":
                drops, force = True, True
            name = f"cases/{page['name']}.{variant}.html"
            (PARITY / name).write_bytes(html.encode("utf-8"))
            cases.append({"id": f"{page['name']}|{variant}", "html": name, "title": page["title"], "state": state,
                          "drops": drops, "force": force})
    return cases


# ---- the Python's results -----------------------------------------------------------------------

def load_python(src: pathlib.Path):
    sys.path.insert(0, str(src / "src"))
    sys.path.insert(0, str(src / "tests"))
    from onenote_sync import config as cfg, page as pg, sync  # noqa: E402
    from onenote_sync.onenote import sprint_for  # noqa: E402
    import test_sync  # noqa: E402
    return cfg, pg, sync, sprint_for, test_sync.FakeNotebook


def dump(pg, html: str) -> dict:
    blocks = pg.blocks(html)
    out = {"blocks": [[b[0], b[1], sha(b[2]), pg.text(b[2]), pg.is_heading(b[2])] for b in blocks],
           "headings": pg.headings(html), "sections": []}
    names = list(dict.fromkeys(pg.headings(html) + HEADINGS + ["architecture", "Missing"]))
    from onenote_sync.sync import _outside
    for name in names:
        head, under = pg.section(html, name)
        out["sections"].append([name, None if head is None else sha(head[2]), [sha(b[2]) for b in under],
                                sha(_outside(html, name))])
    return out


def run_python(args) -> None:
    cfg, pg, sync, sprint_for, Fake = load_python(pathlib.Path(args.src).resolve())
    manifest = json.loads((PARITY / "manifest.json").read_text(encoding="utf-8"))
    results = {"dumps": {}, "sprints": {}, "posts": {}, "markdown": {}, "state": None}

    for page in manifest["pages"]:
        for key in ("file", "ids"):
            html = (PARITY / page[key]).read_bytes().decode("utf-8")
            results["dumps"][page[key]] = dump(pg, html)

    for update in manifest["updates"]:
        text = (PARITY / update["file"]).read_text(encoding="utf-8")
        items = pg.bullets(text, "Architecture")
        results["markdown"][update["name"]] = {"bullets": items, "render": pg.render(items), "plain": pg.plain(items),
                                               "draft": "[DRAFT]" in text}

    dated = sorted([p for p in manifest["pages"] if p["name"] != "synthetic"], key=lambda p: (p["date"], p["title"]))
    for i, page in enumerate(dated + [{"date": "2026-11-16"}, {"date": "2026-11-23"}]):
        earlier = [{"date": p["date"], "title": p["title"]} for p in dated if p["date"] < page["date"]]
        for weeks in (2, 1, 3, 0):
            try:
                results["sprints"][f"{page['date']}/{weeks}"] = list(sprint_for(page["date"], earlier, weeks))
            except ValueError as error:
                results["sprints"][f"{page['date']}/{weeks}"] = ["error", str(error)]

    for scenario in scenarios(manifest):
        results["posts"][scenario["id"]] = run_post(cfg, pg, sync, Fake, scenario)

    # state: a round trip through the Python's own writer, for .NET to read and rewrite
    state = {"page-b": {"Architecture": "x £ – \U0001F60A\n\"q\"\t\\"}, "page-a": {}, "page-c": {"Digital": "", "Architecture": "y"}}
    with tempfile.TemporaryDirectory() as temp:
        path = pathlib.Path(temp) / "state.json"
        sync._save(path, state)
        results["state"] = path.read_bytes().hex()
    # lock keys, as watch.py and cli.py make them: key(config.path), config.path resolved
    try:
        from win_background import key
    except ImportError:   # its key(), as win_background/locks.py has it
        def key(text):
            return hashlib.sha256(str(text).lower().encode()).hexdigest()[:16]
    ea = pathlib.Path.home() / "OneDrive - Zen Internet" / "claude-executive-assistant" / "tools" / "onenote-sync.toml"
    results["keys"] = {str(path): key(pathlib.Path(path).resolve())
                       for path in (str(ea), str(ea).upper(), str(ea).replace("\\", "/"), "relative-settings.toml")}
    (PARITY / "python.json").write_text(json.dumps(results, ensure_ascii=True), encoding="utf-8")
    print(f"python: {len(results['dumps'])} dumps, {len(results['sprints'])} sprints, {len(results['posts'])} posts")


def scenarios(manifest: dict):
    """Every update against every page case (the synthetic page too), dry run and --yes (force only where the case
    says); plus each page's next week created from the pages before it."""
    for case in manifest["cases"]:
        for update in manifest["updates"]:
            for dry_run in (True, False):
                yield {"id": f"{case['id']}|{update['name']}|{'dry' if dry_run else 'yes'}", "case": case,
                       "update": update, "dry_run": dry_run}
    dated = sorted([p for p in manifest["pages"] if p["name"] != "synthetic"], key=lambda p: (p["date"], p["title"]))
    for page in dated[1:]:
        for dry_run in (True, False):
            yield {"id": f"create|{page['name']}|{'dry' if dry_run else 'yes'}", "create": page["date"],
                   "earlier": [p for p in dated if p["date"] < page["date"]], "dry_run": dry_run}


def normalise(text: str, temp: str) -> str:
    text = text.replace(temp, "<TMP>")
    return re.sub(r"\d{4}-\d{2}-\d{2}-\d{8}-\d{6}\.html", "<BACKUP>", text)


def run_post(cfg, pg, sync, Fake, scenario: dict) -> dict:
    with tempfile.TemporaryDirectory() as temp:
        temp_path = pathlib.Path(temp).resolve()
        (temp_path / "config.toml").write_text(SETTINGS, encoding="utf-8")
        config = cfg.load(temp_path / "config.toml")
        lines = []
        drops, force = False, False
        if "create" in scenario:
            week = scenario["create"]
            pages = [{"id": p["date"], "title": p["title"], "date": p["date"],
                      "html": (PARITY / p["ids"]).read_bytes().decode("utf-8")} for p in scenario["earlier"]]
            md = temp_path / f"{week}.md"
            md.write_bytes(CREATE_UPDATE.encode("utf-8"))
        else:
            case, week = scenario["case"], "2026-11-09"
            drops, force = case["drops"], case["force"]
            pages = [{"id": week, "title": case["title"], "date": week,
                      "html": (PARITY / case["html"]).read_bytes().decode("utf-8")}]
            if case["state"] is not None:
                (temp_path / "state.json").write_text(case["state"], encoding="utf-8")
            md = temp_path / f"{week}.md"
            shutil.copyfile(PARITY / scenario["update"]["file"], md)
        notebook = Fake(pages, drops_heading=drops)
        bodies, created = [], []
        original_patch, original_create = notebook.patch, notebook.create
        notebook.patch = lambda page_id, changes: (bodies.append(json.dumps(changes)), original_patch(page_id, changes))[1]
        notebook.create = lambda html: (created.append(html), original_create(html))[1]
        try:
            outcome = ["result", sync.post(config, notebook, md, dry_run=scenario["dry_run"], force=force, log=lines.append)]
        except sync.Refused as error:
            outcome = ["refused", str(error)]
        except ValueError as error:
            outcome = ["usage", str(error)]
        except RuntimeError as error:
            outcome = ["failed", str(error)]
        except Exception as error:   # noqa: BLE001
            outcome = ["error", type(error).__name__, str(error)]
        state_file = temp_path / "state.json"
        backups = sorted((temp_path / "backups").glob("*.html")) if (temp_path / "backups").exists() else []
        return {
            "outcome": [normalise(str(part), str(temp_path)) if isinstance(part, str) else part for part in outcome],
            "log": [normalise(line, str(temp_path)) for line in lines],
            "patches": bodies,
            "created": created,
            "pages": [sha(p["html"]) for p in notebook.pages],
            "state": json.loads(state_file.read_text(encoding="utf-8")) if state_file.exists() else None,
            "state_hex": state_file.read_bytes().hex() if state_file.exists() else None,
            "backups": len(backups),
        }


# ---- compare ------------------------------------------------------------------------------------

def compare(args) -> None:
    py = json.loads((PARITY / "python.json").read_text(encoding="utf-8"))
    net = json.loads((PARITY / "dotnet.json").read_text(encoding="utf-8"))
    failures = 0
    for part in ("dumps", "sprints", "markdown", "posts"):
        a, b = py[part], net[part]
        missing = sorted(set(a) ^ set(b))
        differ = sorted(k for k in set(a) & set(b) if a[k] != b[k])
        for key in differ[:5]:
            print(f"  {part} {key}:")
            for field in (a[key] if isinstance(a[key], dict) else {"value": a[key]}):
                x = a[key][field] if isinstance(a[key], dict) else a[key]
                y = b[key].get(field) if isinstance(b[key], dict) else b[key]
                if x != y:
                    print(f"    {field}: python {str(x)[:300]!r}\n    {' ' * len(field)}  dotnet {str(y)[:300]!r}")
        print(f"{part}: {len(set(a) & set(b)) - len(differ)} of {len(a)} identical, {len(differ)} differ, {len(missing)} missing")
        failures += len(differ) + len(missing)
    if py["state"] != net["state_rewritten"]:
        print("state: the .NET rewrite of the Python's state file differs")
        failures += 1
    else:
        print("state: the .NET read and rewrote the Python's state file byte for byte")
    same_keys = sum(py["keys"][k] == net["keys"].get(k) for k in py["keys"])
    print(f"lock keys: {same_keys} of {len(py['keys'])} identical")
    failures += len(py["keys"]) - same_keys
    outcomes = {}
    for result in py["posts"].values():
        outcomes[result["outcome"][0]] = outcomes.get(result["outcome"][0], 0) + 1
    print(f"post outcomes (python): {outcomes}")
    print(f"patch bodies compared byte for byte: {sum(len(r['patches']) for r in py['posts'].values())}; "
          f"pages created: {sum(len(r['created']) for r in py['posts'].values())}")
    sys.exit(1 if failures else 0)


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("prepare")
    p.add_argument("--src", required=True)
    p.add_argument("--pages", nargs="+", required=True)
    p.add_argument("--updates", required=True)
    p = sub.add_parser("python")
    p.add_argument("--src", required=True)
    sub.add_parser("compare")
    args = parser.parse_args()
    {"prepare": prepare, "python": run_python, "compare": compare}[args.command](args)


if __name__ == "__main__":
    main()
