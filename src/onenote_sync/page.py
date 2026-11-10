"""Reading a section of a OneNote page, and writing one.

A weekly page is a column of headings, each followed by that team's content: usually one bulleted
list, sometimes "Highlights:" and "Challenges:" lists. A heading is a bold paragraph at 14pt or
more (the page title-level "Executive Summary" is 18pt). A section runs from its heading to the
next heading, or to the end of the column.

Markdown in, OneNote HTML out: only what an update needs. Bullets ("- "), **bold**, *italics*,
and a nested bullet indented two or more spaces. The styles copy what the pages already use, so a
posted section looks like a typed one.
"""

from __future__ import annotations

import html as htmllib
import re

HEADING = re.compile(r"font-size:(1[4-9]|[2-9]\d)(\.\d+)?pt[^\"]*font-weight:bold|font-weight:bold[^\"]*font-size:(1[4-9]|[2-9]\d)(\.\d+)?pt")
BLOCK = re.compile(r"<(p|ul|ol|table|div|img|object)\b[^>]*>", re.I)

TEXT_STYLE = "font-size:11.5pt;color:black"
BOLD_STYLE = "font-size:11.5pt;color:black;font-weight:bold"
TITLE_HEADING = '<p lang="en-GB" style="margin-top:0pt;margin-bottom:0pt"><span style="font-family:Aptos;font-size:18pt;color:black;font-weight:bold;text-decoration:underline">{}</span></p>'
TEAM_HEADING = '<p lang="en-GB" style="margin-top:0pt;margin-bottom:0pt"><span style="font-size:14pt;color:black;font-weight:bold">{}</span></p>'


def _end(html: str, start: int, tag: str) -> int:
    """Where the element starting at start ends, allowing for nesting of the same tag."""
    opening = re.compile(rf"<{tag}\b[^>]*?(/?)>", re.I)
    closing = re.compile(rf"</{tag}\s*>", re.I)
    first = opening.match(html, start)
    if first.group(1) == "/":
        return first.end()
    depth, at = 1, first.end()
    while depth:
        o, c = opening.search(html, at), closing.search(html, at)
        if c is None:
            return len(html)
        if o and o.start() < c.start():
            depth += o.group(1) != "/"
            at = o.end()
        else:
            depth -= 1
            at = c.end()
    return at


def text(fragment: str) -> str:
    """Plain text, one line per paragraph or list item."""
    fragment = re.sub(r"(?i)</(li|p)>|<br\s*/?>|<(ul|ol|li)\b[^>]*>", "\n", fragment)
    fragment = htmllib.unescape(re.sub(r"<[^>]+>", "", fragment))
    lines = (re.sub(r"\s+", " ", line).strip() for line in fragment.split("\n"))
    return "\n".join(line for line in lines if line)


def is_heading(element: str) -> bool:
    return element.lower().startswith("<p") and bool(HEADING.search(element)) and len(text(element)) < 60


def blocks(html: str) -> list:
    """[(tag, id, html)] for the top-level blocks of every column (div), in order. <br> is skipped."""
    found = []
    for column in re.finditer(r"<div\b[^>]*>", html, re.I):
        end = _end(html, column.start(), "div")
        at = column.end()
        inner_end = html.rfind("</div", at, end)
        while True:
            match = BLOCK.search(html, at, inner_end)
            if not match:
                break
            tag = match.group(1).lower()
            if tag == "div":   # a nested column is handled by the outer loop's own match
                at = _end(html, match.start(), "div")
                continue
            stop = _end(html, match.start(), tag)
            element = html[match.start():stop]
            ident = re.search(r'\bid="([^"]+)"', match.group(0))
            found.append((tag, ident.group(1) if ident else None, element))
            at = stop
    return found


def _same(heading: str, name: str) -> bool:
    return text(heading).strip().rstrip("?:").strip().lower() == name.strip().lower()


def section(html: str, name: str) -> tuple:
    """(heading block, [blocks under it]) for the heading called name; (None, []) if there's none."""
    all_blocks = blocks(html)
    for i, block in enumerate(all_blocks):
        if is_heading(block[2]) and _same(block[2], name):
            under = []
            for after in all_blocks[i + 1:]:
                if is_heading(after[2]):
                    break
                under.append(after)
            return block, under
    return None, []


def headings(html: str) -> list:
    return [text(b[2]) for b in blocks(html) if is_heading(b[2])]


# ---- markdown to OneNote ----------------------------------------------------------------------

def bullets(markdown: str, section_name: str) -> list:
    """[(depth, text)] for the bullets under "# <section_name>" (or the first heading, if none has that name).
    Stops at the next heading or a --- rule, so a draft's notes underneath are never posted."""
    lines = markdown.replace("\r\n", "\n").split("\n")
    if lines and lines[0].strip() == "---":   # frontmatter
        end = next((i for i in range(1, len(lines)) if lines[i].strip() == "---"), 0)
        lines = lines[end + 1:]
    start = next((i for i, l in enumerate(lines) if re.match(r"#+\s", l) and l.lstrip("#").strip().lower() == section_name.lower()), None)
    if start is None:
        start = next((i for i, l in enumerate(lines) if re.match(r"#+\s", l)), -1)
    found = []
    for line in lines[start + 1:]:
        if re.match(r"#+\s", line) or line.strip() == "---":
            break
        match = re.match(r"^(\s*)[-*]\s+(.*)$", line)
        if match:
            found.append((min(len(match.group(1).expandtabs(4)) // 2, 3), match.group(2).strip()))
        elif line.strip() and found:   # a wrapped continuation of the bullet above
            found[-1] = (found[-1][0], found[-1][1] + " " + line.strip())
    return found


def _inline(markdown: str) -> str:
    """**bold** and *italic* to OneNote spans; everything else as plain, escaped text."""
    out = []
    for part in re.split(r"(\*\*[^*]+\*\*|(?<!\*)\*[^*\s][^*]*\*(?!\*))", markdown):
        if not part:
            continue
        if part.startswith("**"):
            out.append(f'<span style="{BOLD_STYLE}">{htmllib.escape(part[2:-2], quote=False)}</span>')
        elif part.startswith("*") and part.endswith("*") and len(part) > 2:
            out.append(f'<span style="{TEXT_STYLE};font-style:italic">{htmllib.escape(part[1:-1], quote=False)}</span>')
        else:
            out.append(f'<span style="{TEXT_STYLE}">{htmllib.escape(part, quote=False)}</span>')
    return "".join(out)


def render(items: list) -> str:
    """A <ul> for [(depth, text)], nested lists for deeper bullets."""
    html, depth = ["<ul>"], 0
    for i, (level, line) in enumerate(items):
        while depth < level:
            html.append("<ul>")
            depth += 1
        while depth > level:
            html.append("</ul></li>")
            depth -= 1
        nxt = items[i + 1][0] if i + 1 < len(items) else 0
        html.append(f'<li><span lang="en-GB">{_inline(line)}</span>' + ("" if nxt > level else "</li>"))
    while depth > 0:
        html.append("</ul></li>")
        depth -= 1
    html.append("</ul>")
    return "".join(html)


def plain(items: list) -> str:
    """The text a rendered list will read as, for comparing with what's on the page."""
    return text(render(items))


def new_page(title: str, names: list) -> str:
    """A new week's page: the title, then each heading with a blank line under it. The first heading is the
    page-level one (Executive Summary); the rest are teams' sections."""
    body = []
    for i, name in enumerate(names):
        body.append((TITLE_HEADING if i == 0 else TEAM_HEADING).format(htmllib.escape(name, quote=False)))
        body.append("<br />")
    return (f'<!DOCTYPE html>\n<html>\n<head>\n<title>{htmllib.escape(title, quote=False)}</title>\n'
            f'</head>\n<body data-absolute-enabled="true" style="font-family:Calibri;font-size:11pt">\n'
            f'<div style="position:absolute;left:48px;top:115px;width:967px">\n{chr(10).join(body)}\n</div>\n</body>\n</html>')
