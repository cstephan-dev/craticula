#!/usr/bin/env python3
"""Build the Craticula docs site from the repository's own Markdown (story #1666, task #2832).

Standard library only, so the build needs no new dependency. The pages are listed in `site/site.json`; each page is a
Markdown file of the repository (a page written for the site under `site/pages/`, or a public document such as `README.md`
or `spec/README.md` that the site renders as it is). The output is a folder of static HTML files.

The build also checks every link of every page, and fails (exit 1) on:
  * a link to a file that does not exist in the repository;
  * a link to a heading anchor that does not exist (in a page of the site, or in another Markdown file of the repository);
  * a link into the private part of the repository (the PRIVATE list of `scripts/publish-github.sh`, which the public mirror
    does not carry, written out below), as a file or as a bare directory;
  * a link that leaves the repository, or uses a scheme other than http, https or mailto;
  * the same for an image (`![alt](path)`), whose relative path is also rewritten to its public address.
A link to an existing repository file that has no page on the site is rewritten to its public GitHub address.
External `http(s)` links are not fetched (the build must work offline).

    python3 scripts/build_site.py [--out DIR] [--root DIR]

Exit codes: 0 built and every link resolves, 1 a link is dead or private, 2 bad usage or an unreadable configuration.
"""
from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

# The private paths of scripts/publish-github.sh (its PRIVATE array): what the public mirror does not carry, so a link to one is
# dead there. test_build_site.py pins this tuple to the real array where that script exists (the private repository).
PRIVATE_PATHS = ("docs", ".claude", "CLAUDE.md", ".mcp.json", ".githooks", ".azuredevops", ".sonarlint", ".vscode",
                 "scripts/publish-github.sh", "scripts/sync-release-commit.sh", "scripts/apply-public-change.sh",
                 "scripts/test_apply_public_change.py", "scripts/test_publish_mirror_real.py")
EXTERNAL = re.compile(r"^(?:[a-zA-Z][a-zA-Z0-9+.-]*:)")
ALLOWED_SCHEMES = ("http:", "https:", "mailto:")
SLUG = re.compile(r"^[a-z0-9][a-z0-9-]*$")


class BuildError(Exception):
    """A configuration problem (exit 2)."""


# ---------------------------------------------------------------- anchors

def slugify(text: str) -> str:
    """GitHub's heading slug: lower case, drop punctuation, spaces to hyphens (so existing `#...` links keep working)."""
    text = re.sub(r"</?[a-zA-Z][^>]*>", "", text).strip().lower()
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    return text.replace(" ", "-")


class Slugger:
    """GitHub's de-duplication: the first `same` is `same`, then `same-1`, `same-2`; a suffix that is already taken (by a
    heading literally called `same 1`) is skipped, so every id on the page is unique."""

    def __init__(self) -> None:
        self.used: set[str] = set()
        self.counts: dict[str, int] = {}

    def __call__(self, text: str) -> str:
        base = slugify(text)
        candidate = base
        while candidate in self.used:
            n = self.counts.get(base, 0) + 1
            self.counts[base] = n
            candidate = f"{base}-{n}"
        self.used.add(candidate)
        return candidate


# ---------------------------------------------------------------- Markdown

@dataclass
class Rendered:
    html: str
    anchors: set[str] = field(default_factory=set)
    links: list[tuple[int, str]] = field(default_factory=list)  # (source line, href)
    title: str = ""


_INLINE_CODE = re.compile(r"(`+)(.+?)\1", re.S)
_LINK = re.compile(r"(!?)\[((?:[^\[\]]|\[[^\]]*\])*)\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
_BOLD = re.compile(r"\*\*(.+?)\*\*|__(.+?)__")
_ITALIC = re.compile(r"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])")
_AUTOLINK = re.compile(r"<(https?://[^>\s]+)>")


def _inline(text: str, line: int, out: Rendered, resolve, holes: list[str] | None = None) -> str:
    """Inline Markdown to HTML. `resolve(href)` returns the href to emit; links are recorded for the checker.
    A nested call (a link's label) shares the caller's `holes` and returns its text still holding placeholders."""
    nested = holes is not None
    if holes is None:
        holes = []

    def restore(value: str) -> str:
        while "\x00" in value:  # a held link may itself hold a code span
            value = re.sub(r"\x00(\d+)\x00", lambda m: holes[int(m.group(1))], value)
        return value

    def hold(fragment: str) -> str:
        holes.append(fragment)
        return f"\x00{len(holes) - 1}\x00"

    def code(match: re.Match) -> str:
        return hold(f"<code>{html.escape(match.group(2).strip(), quote=False)}</code>")

    text = _INLINE_CODE.sub(code, text)

    def autolink(match: re.Match) -> str:
        url = match.group(1)
        return hold(f'<a href="{html.escape(url)}">{html.escape(url, quote=False)}</a>')

    text = _AUTOLINK.sub(autolink, text)

    def link(match: re.Match) -> str:
        bang, label, href = match.group(1), match.group(2), match.group(3)
        label_html = _inline(label, line, out, resolve, holes)
        if bang:
            out.links.append((line, href))
            href = resolve(href, True)
            return hold(f'<img alt="{html.escape(re.sub(r"<[^>]+>", "", restore(label_html)))}" src="{html.escape(href)}">')
        out.links.append((line, href))
        return hold(f'<a href="{html.escape(resolve(href))}">{label_html}</a>')

    text = _LINK.sub(link, text)
    text = html.escape(text, quote=False)
    text = _BOLD.sub(lambda m: f"<strong>{m.group(1) or m.group(2)}</strong>", text)
    text = _ITALIC.sub(lambda m: f"<em>{m.group(1) or m.group(2)}</em>", text)
    return text if nested else restore(text)


_HEADING = re.compile(r"^(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")
_FENCE = re.compile(r"^(\s*)(```+|~~~+)\s*([\w+-]*)")
_LIST = re.compile(r"^(\s*)([-*+]|\d+[.)])\s+(.*)")
_TABLE_SEP = re.compile(r"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")
_RULE = re.compile(r"^\s*([-*_])(\s*\1){2,}\s*$")


def _cells(row: str) -> list[str]:
    row = row.strip()
    if row.startswith("|"):
        row = row[1:]
    if row.endswith("|") and not row.endswith("\\|"):
        row = row[:-1]
    return [c.strip().replace("\\|", "|") for c in re.split(r"(?<!\\)\|", row)]


def render_markdown(source: str, resolve=lambda href, image=False: href) -> Rendered:
    """Markdown to HTML for the subset the repository's documents use (headings, paragraphs, lists, tables, fenced code,
    block quotes, rules, inline code, links, emphasis). Raw HTML in the source is escaped, never passed through."""
    source = source.replace("\x00", "\ufffd")  # NUL marks this converter's placeholders
    out = Rendered(html="")
    slugs = Slugger()
    body: list[str] = []
    lines = source.replace("\r\n", "\n").split("\n")
    i = 0

    def inline(text: str, number: int) -> str:
        return _inline(text, number, out, resolve)

    while i < len(lines):
        line = lines[i]
        number = i + 1

        if not line.strip():
            i += 1
            continue

        fence = _FENCE.match(line)
        if fence:
            marker, language = fence.group(2), fence.group(3)
            indent = len(fence.group(1))
            i += 1
            code: list[str] = []
            while i < len(lines) and not re.match(rf"^\s*{re.escape(marker[0])}{{{len(marker)},}}\s*$", lines[i]):
                code.append(lines[i][indent:] if lines[i][:indent].strip() == "" else lines[i])
                i += 1
            i += 1
            cls = f' class="language-{html.escape(language)}"' if language else ""
            body.append(f"<pre><code{cls}>{html.escape(chr(10).join(code), quote=False)}</code></pre>")
            continue

        heading = _HEADING.match(line)
        if heading:
            level, text = len(heading.group(1)), heading.group(2)
            anchor = slugs(re.sub(r"[`*]", "", re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)))
            out.anchors.add(anchor)
            if level == 1 and not out.title:
                out.title = re.sub(r"[`*]", "", text)
            body.append(f'<h{level} id="{anchor}">{inline(text, number)}</h{level}>')
            i += 1
            continue

        if _RULE.match(line):
            body.append("<hr>")
            i += 1
            continue

        if line.lstrip().startswith(">"):
            quote: list[str] = []
            start = i
            while i < len(lines) and lines[i].lstrip().startswith(">"):
                quote.append(re.sub(r"^\s*>\s?", "", lines[i]))
                i += 1
            inner = render_markdown("\n".join(quote), resolve)
            out.links.extend((start + n, href) for n, href in inner.links)
            out.anchors.update(inner.anchors)
            body.append(f"<blockquote>{inner.html}</blockquote>")
            continue

        if "|" in line and i + 1 < len(lines) and _TABLE_SEP.match(lines[i + 1]) and "|" in lines[i + 1]:
            head = _cells(line)
            i += 2
            rows: list[tuple[int, list[str]]] = []
            while i < len(lines) and lines[i].strip() and "|" in lines[i]:
                rows.append((i + 1, _cells(lines[i])))
                i += 1
            parts = ["<table><thead><tr>" + "".join(f"<th>{inline(c, number)}</th>" for c in head) + "</tr></thead><tbody>"]
            for row_number, cells in rows:
                parts.append("<tr>" + "".join(f"<td>{inline(c, row_number)}</td>" for c in cells) + "</tr>")
            parts.append("</tbody></table>")
            body.append("".join(parts))
            continue

        if _LIST.match(line):
            items: list[tuple[int, int, bool, list[str]]] = []  # (line number, indent, ordered, text lines)
            while i < len(lines):
                m = _LIST.match(lines[i])
                if m:
                    items.append((i + 1, len(m.group(1).expandtabs(4)), m.group(2)[0].isdigit(), [m.group(3)]))
                    i += 1
                elif lines[i].strip() and lines[i].startswith((" ", "\t")) and items and not _FENCE.match(lines[i]):
                    items[-1][3].append(lines[i].strip())
                    i += 1
                else:
                    break
            body.append(_render_list(items, inline))
            continue

        paragraph = [line.strip()]
        start = number
        i += 1
        while i < len(lines) and lines[i].strip() and not (
            _HEADING.match(lines[i]) or _FENCE.match(lines[i]) or _LIST.match(lines[i])
            or lines[i].lstrip().startswith(">") or _RULE.match(lines[i])
            or ("|" in lines[i] and i + 1 < len(lines) and _TABLE_SEP.match(lines[i + 1]))
        ):
            paragraph.append(lines[i].strip())
            i += 1
        body.append(f"<p>{inline(' '.join(paragraph), start)}</p>")

    out.html = "\n".join(body)
    return out


def _render_list(items, inline) -> str:
    """Nested lists by indentation: an item indented deeper than the previous one opens a child list."""
    html_parts: list[str] = []
    stack: list[tuple[int, str]] = []  # (indent, tag)
    for number, indent, ordered, text_lines in items:
        tag = "ol" if ordered else "ul"
        while stack and indent < stack[-1][0]:
            html_parts.append(f"</li></{stack.pop()[1]}>")
        if not stack or indent > stack[-1][0]:
            html_parts.append(f"<{tag}>")
            stack.append((indent, tag))
        else:
            html_parts.append("</li>")
        html_parts.append(f"<li>{inline(' '.join(text_lines), number)}")
    while stack:
        html_parts.append(f"</li></{stack.pop()[1]}>")
    return "".join(html_parts)


# ---------------------------------------------------------------- site

@dataclass
class Page:
    slug: str
    title: str
    source: str  # repository-relative path of the Markdown file
    rendered: Rendered | None = None


def load_config(root: Path) -> dict:
    path = root / "site" / "site.json"
    try:
        config = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise BuildError(f"cannot read {path}: {error}") from error
    for key in ("title", "repo", "branch", "pages"):
        if key not in config:
            raise BuildError(f"{path}: missing '{key}'")
    for entry in config["pages"]:
        if not isinstance(entry, dict) or not all(isinstance(entry.get(k), str) for k in ("slug", "title", "source")):
            raise BuildError(f"{path}: every page needs a string 'slug', 'title' and 'source'")
        if not SLUG.match(entry["slug"]):
            raise BuildError(f"{path}: page slug '{entry['slug']}' must match [a-z0-9][a-z0-9-]*")
        source = entry["source"]
        normal = os.path.normpath(source).replace(os.sep, "/")
        if os.path.isabs(source) or normal.startswith("../") or normal == ".." or _is_private(normal):
            raise BuildError(f"{path}: page '{entry['slug']}': source '{source}' must be a public file inside the repository")
    slugs = [p["slug"] for p in config["pages"]]
    if len(set(slugs)) != len(slugs) or "index" not in slugs:
        raise BuildError(f"{path}: page slugs must be unique and include 'index'")
    return config


def _is_private(rel: str) -> bool:
    """True for a path the public mirror does not carry: a PRIVATE entry itself (file or bare directory) or anything under it."""
    rel = rel.strip("/")
    return any(rel == entry or rel.startswith(entry + "/") for entry in PRIVATE_PATHS)


def resolve_link(href: str, source: str, pages_by_source: dict[str, Page], root: Path, config: dict,
                 anchors_of, errors: list[str], line: int, own_anchors: set[str], raw: bool = False) -> str:
    """The href to emit for `href` found in `source`, or the original one after recording an error."""
    where = f"{source}:{line}"
    if EXTERNAL.match(href):
        if not href.lower().startswith(ALLOWED_SCHEMES):
            errors.append(f"{where}: link '{href[:40]}' uses a scheme other than http, https or mailto")
        return href
    path_part, _, fragment = href.partition("#")
    if not path_part:
        if fragment and fragment not in own_anchors:
            errors.append(f"{where}: dead anchor '#{fragment}'")
        return href
    target = os.path.normpath(os.path.join(os.path.dirname(source), path_part)).replace(os.sep, "/")
    if target.startswith("../") or target == "..":
        errors.append(f"{where}: link '{href}' leaves the repository")
        return href
    if _is_private(target):
        errors.append(f"{where}: link '{href}' points into the private part of the repository ({target})")
        return href
    page = pages_by_source.get(target)
    if page is not None:
        if fragment and fragment not in anchors_of(page.source):
            errors.append(f"{where}: dead anchor '#{fragment}' in {target}")
        return f"{page.slug}.html" + (f"#{fragment}" if fragment else "")
    full = root / target
    if not full.exists():
        errors.append(f"{where}: dead link '{href}' (no such file: {target})")
        return href
    if fragment and target.endswith(".md") and fragment not in anchors_of(target):
        errors.append(f"{where}: dead anchor '#{fragment}' in {target}")
    kind = "tree" if full.is_dir() else ("raw" if raw else "blob")
    return f"{config['repo'].rstrip('/')}/{kind}/{config['branch']}/{target.rstrip('/')}" + (f"#{fragment}" if fragment else "")


def build(root: Path, out_dir: Path) -> list[str]:
    """Build the site; returns the list of errors (empty when every link resolves)."""
    config = load_config(root)
    pages = [Page(p["slug"], p["title"], p["source"]) for p in config["pages"]]
    pages_by_source = {p.source: p for p in pages}
    errors: list[str] = []

    anchor_cache: dict[str, set[str]] = {}

    def anchors_of(rel: str) -> set[str]:
        if rel not in anchor_cache:
            try:
                anchor_cache[rel] = render_markdown((root / rel).read_text(encoding="utf-8")).anchors
            except (OSError, UnicodeDecodeError):
                anchor_cache[rel] = set()
        return anchor_cache[rel]

    template = (root / "site" / "template.html").read_text(encoding="utf-8")
    style = (root / "site" / "style.css").read_text(encoding="utf-8")
    out_dir.mkdir(parents=True, exist_ok=True)
    for stale in out_dir.glob("*.html"):  # a page removed from site.json must not linger in the output
        stale.unlink()
    (out_dir / "style.css").write_text(style, encoding="utf-8")

    nav = "".join(f'<li><a href="{html.escape(p.slug)}.html">{html.escape(p.title)}</a></li>' for p in pages)

    for page in pages:
        try:
            text = (root / page.source).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as error:
            errors.append(f"site/site.json: page '{page.slug}': cannot read {page.source}: {error}")
            continue
        own = render_markdown(text).anchors
        link_errors: list[str] = []
        rendered = render_markdown(
            text,
            resolve=lambda href, image=False: href)
        # Second pass with the real resolver, now that the page's own anchors are known; the line of each link is taken
        # from the first pass's record, in order.
        lines = iter(line for line, _ in rendered.links)

        def resolver(href: str, image: bool = False, source=page.source, own=own, sink=link_errors, lines=lines) -> str:
            return resolve_link(href, source, pages_by_source, root, config, anchors_of, sink, next(lines, 0), own, raw=image)

        rendered = render_markdown(text, resolve=resolver)
        errors.extend(link_errors)
        page.rendered = rendered

        values = {
            "site_title": html.escape(config["title"]),
            "page_title": html.escape(page.title),
            "nav": nav,
            "content": rendered.html,
            "source": html.escape(page.source),
            "source_url": html.escape(f"{config['repo'].rstrip('/')}/blob/{config['branch']}/{page.source}"),
        }
        # One pass: a value that itself contains {{...}} (page content may) is never substituted again.
        page_html = re.sub(r"\{\{(\w+)\}\}", lambda m: values.get(m.group(1), m.group(0)), template)
        (out_dir / f"{page.slug}.html").write_text(page_html, encoding="utf-8")

    return errors


def main(argv: list[str] | None = None) -> int:
    default_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description="Build the Craticula docs site (see the module docstring).")
    parser.add_argument("--root", type=Path, default=default_root, help="repository root (default: this checkout)")
    parser.add_argument("--out", type=Path, default=None, help="output folder (default: <root>/site/_build)")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    out_dir = (args.out or root / "site" / "_build").resolve()
    try:
        errors = build(root, out_dir)
    except BuildError as error:
        print(f"build_site: {error}", file=sys.stderr)
        return 2
    except (OSError, UnicodeDecodeError) as error:
        print(f"build_site: {error}", file=sys.stderr)
        return 2

    if errors:
        for error in errors:
            print(f"build_site: {error}", file=sys.stderr)
        print(f"build_site: {len(errors)} problem(s); the site was written to {out_dir} but must not be published.", file=sys.stderr)
        return 1

    print(f"build_site: built {len(list(out_dir.glob('*.html')))} page(s) into {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
