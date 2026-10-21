#!/usr/bin/env python3
"""Tests of scripts/build_site.py (story #1666, task #2832): the Markdown converter, the link and anchor check, the exit
codes, the build of this repository's own site, and the rule that no public Markdown file links into the private part of the
repository. Offline, standard library only.
Run: python3 scripts/test_build_site.py
"""
import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_site as bs  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = "<html><title>{{page_title}}</title><nav>{{nav}}</nav>{{content}}<a href=\"{{source_url}}\">{{source}}</a></html>"


def make_repo(files: dict[str, str], pages: list[dict]) -> Path:
    """A scratch repository: the site files, the given pages' sources and any other file."""
    root = Path(tempfile.mkdtemp())
    config = {"title": "T", "repo": "https://example.test/r", "branch": "main", "pages": pages}
    everything = {"site/site.json": json.dumps(config), "site/template.html": TEMPLATE, "site/style.css": "body{}", **files}
    for rel, text in everything.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return root


INDEX = {"slug": "index", "title": "Home", "source": "index.md"}


def run_build(files: dict[str, str], pages: list[dict]):
    root = make_repo(files, pages)
    out = root / "_out"
    return bs.build(root, out), out


class Anchors(unittest.TestCase):
    def test_slugs_follow_github(self):
        self.assertEqual(bs.slugify("Why"), "why")
        self.assertEqual(bs.slugify("How it works"), "how-it-works")
        self.assertEqual(bs.slugify("`spec/` — the source of truth"), "spec--the-source-of-truth")
        self.assertEqual(bs.slugify("GRP-7. A `nullValue` group key"), "grp-7-a-nullvalue-group-key")

    def test_duplicate_headings_get_a_suffix(self):
        rendered = bs.render_markdown("# A\n\n## Same\n\n## Same\n\n## Same\n")
        self.assertEqual(rendered.anchors, {"a", "same", "same-1", "same-2"})


class Converter(unittest.TestCase):
    def test_headings_get_ids_and_the_first_h1_is_the_title(self):
        rendered = bs.render_markdown("# Hello `x`\n\ntext\n\n## Sub\n")
        self.assertIn('<h1 id="hello-x">Hello <code>x</code></h1>', rendered.html)
        self.assertIn('<h2 id="sub">Sub</h2>', rendered.html)
        self.assertEqual(rendered.title, "Hello x")

    def test_fenced_code_is_escaped_and_keeps_its_language(self):
        rendered = bs.render_markdown("```sql\nSELECT * FROM t WHERE a < 1 AND b > 2;\n```\n")
        self.assertIn('<pre><code class="language-sql">SELECT * FROM t WHERE a &lt; 1 AND b &gt; 2;</code></pre>', rendered.html)

    def test_a_heading_inside_a_fence_is_not_a_heading(self):
        rendered = bs.render_markdown("```\n# not a heading\n```\n")
        self.assertEqual(rendered.anchors, set())

    def test_raw_html_is_escaped_never_passed_through(self):
        rendered = bs.render_markdown("a <script>alert(1)</script> b\n")
        self.assertNotIn("<script>", rendered.html)
        self.assertIn("&lt;script&gt;", rendered.html)

    def test_tables(self):
        rendered = bs.render_markdown("| a | b |\n|---|---|\n| `x` | y \\| z |\n")
        self.assertIn("<th>a</th><th>b</th>", rendered.html)
        self.assertIn("<td><code>x</code></td><td>y | z</td>", rendered.html)

    def test_nested_and_ordered_lists(self):
        rendered = bs.render_markdown("- one\n  - inner\n- two\n\n1. first\n2. second\n")
        self.assertIn("<ul><li>one<ul><li>inner</li></ul></li><li>two</li></ul>", rendered.html)
        self.assertIn("<ol><li>first</li><li>second</li></ol>", rendered.html)

    def test_emphasis_and_code_do_not_interfere(self):
        rendered = bs.render_markdown("**bold** and *it* and `a*b*c` and snake_case_name\n")
        self.assertIn("<strong>bold</strong>", rendered.html)
        self.assertIn("<em>it</em>", rendered.html)
        self.assertIn("<code>a*b*c</code>", rendered.html)
        self.assertIn("snake_case_name", rendered.html)

    def test_links_are_recorded_with_their_line_and_can_be_rewritten(self):
        rendered = bs.render_markdown("one\n\n[x](a.md#b) and [`y`](c.md)\n", resolve=lambda href: "R:" + href)
        self.assertEqual(rendered.links, [(3, "a.md#b"), (3, "c.md")])
        self.assertIn('<a href="R:a.md#b">x</a>', rendered.html)
        self.assertIn('<a href="R:c.md"><code>y</code></a>', rendered.html)

    def test_block_quotes(self):
        rendered = bs.render_markdown("> quoted [l](x.md)\n> more\n")
        self.assertIn("<blockquote><p>quoted", rendered.html)
        self.assertEqual([href for _, href in rendered.links], ["x.md"])


class LinkCheck(unittest.TestCase):
    def test_a_clean_site_builds_and_a_repo_file_link_goes_to_github(self):
        errors, out = run_build({"index.md": "# Home\n\n[code](src/a.cs) [dir](src/)\n", "src/a.cs": "x"}, [INDEX])
        self.assertEqual(errors, [])
        page = (out / "index.html").read_text(encoding="utf-8")
        self.assertIn('href="https://example.test/r/blob/main/src/a.cs"', page)
        self.assertIn('href="https://example.test/r/tree/main/src"', page)
        self.assertTrue((out / "style.css").exists())

    def test_a_link_to_another_page_becomes_a_site_link_with_its_anchor_checked(self):
        pages = [INDEX, {"slug": "two", "title": "Two", "source": "d/two.md"}]
        files = {"index.md": "# Home\n\n[go](d/two.md#part)\n", "d/two.md": "# Two\n\n## Part\n"}
        errors, out = run_build(files, pages)
        self.assertEqual(errors, [])
        self.assertIn('href="two.html#part"', (out / "index.html").read_text(encoding="utf-8"))

    def test_a_dead_file_link_fails_with_the_source_line(self):
        errors, _ = run_build({"index.md": "# Home\n\nok\n\n[x](missing.md)\n"}, [INDEX])
        self.assertEqual(len(errors), 1)
        self.assertIn("index.md:5: dead link 'missing.md'", errors[0])

    def test_a_dead_anchor_in_the_same_page_fails(self):
        errors, _ = run_build({"index.md": "# Home\n\n[x](#nowhere) [y](#home)\n"}, [INDEX])
        self.assertEqual(len(errors), 1)
        self.assertIn("dead anchor '#nowhere'", errors[0])

    def test_a_dead_anchor_in_another_page_fails(self):
        pages = [INDEX, {"slug": "two", "title": "Two", "source": "two.md"}]
        errors, _ = run_build({"index.md": "# Home\n\n[x](two.md#nowhere)\n", "two.md": "# Two\n"}, pages)
        self.assertEqual(len(errors), 1)
        self.assertIn("dead anchor '#nowhere' in two.md", errors[0])

    def test_a_dead_anchor_in_a_markdown_file_without_a_page_fails(self):
        files = {"index.md": "# Home\n\n[ok](other.md#there) [bad](other.md#gone)\n", "other.md": "# Other\n\n## There\n"}
        errors, _ = run_build(files, [INDEX])
        self.assertEqual(len(errors), 1)
        self.assertIn("dead anchor '#gone' in other.md", errors[0])

    def test_a_link_into_the_private_part_fails_even_when_the_file_exists(self):
        files = {"index.md": "# Home\n\n[trd](docs/TRD.md) [m](CLAUDE.md) [c](.claude/rules/x.md)\n",
                 "docs/TRD.md": "# TRD\n", "CLAUDE.md": "x", ".claude/rules/x.md": "x"}
        errors, _ = run_build(files, [INDEX])
        self.assertEqual(len(errors), 3)
        self.assertTrue(all("private part of the repository" in e for e in errors))

    def test_a_link_that_leaves_the_repository_fails(self):
        errors, _ = run_build({"index.md": "# Home\n\n[x](../outside.md)\n"}, [INDEX])
        self.assertEqual(len(errors), 1)
        self.assertIn("leaves the repository", errors[0])

    def test_external_links_are_not_fetched(self):
        errors, _ = run_build({"index.md": "# Home\n\n[x](https://example.invalid/never) [m](mailto:a@b.c)\n"}, [INDEX])
        self.assertEqual(errors, [])

    def test_a_page_whose_source_is_missing_is_reported(self):
        errors, _ = run_build({}, [INDEX])
        self.assertEqual(len(errors), 1)
        self.assertIn("cannot read index.md", errors[0])


class Cli(unittest.TestCase):
    def run_main(self, root: Path):
        err = io.StringIO()
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            code = bs.main(["--root", str(root), "--out", str(root / "_out")])
        return code, err.getvalue()

    def test_exit_0_when_every_link_resolves(self):
        code, _ = self.run_main(make_repo({"index.md": "# Home\n"}, [INDEX]))
        self.assertEqual(code, 0)

    def test_exit_1_on_a_dead_link(self):
        code, err = self.run_main(make_repo({"index.md": "# Home\n\n[x](nope.md)\n"}, [INDEX]))
        self.assertEqual(code, 1)
        self.assertIn("dead link", err)

    def test_exit_2_on_a_missing_or_invalid_configuration(self):
        root = Path(tempfile.mkdtemp())
        self.assertEqual(self.run_main(root)[0], 2)
        broken = make_repo({"index.md": "# Home\n"}, [INDEX])
        (broken / "site" / "site.json").write_text("{not json", encoding="utf-8")
        self.assertEqual(self.run_main(broken)[0], 2)

    def test_exit_2_when_there_is_no_index_page(self):
        code, err = self.run_main(make_repo({"a.md": "# A\n"}, [{"slug": "a", "title": "A", "source": "a.md"}]))
        self.assertEqual(code, 2)
        self.assertIn("include 'index'", err)


class ThisRepository(unittest.TestCase):
    def test_the_real_site_builds_with_every_link_resolving(self):
        with tempfile.TemporaryDirectory() as out:
            errors = bs.build(ROOT, Path(out))
            self.assertEqual(errors, [])
            self.assertTrue((Path(out) / "index.html").exists())

    def test_the_mirror_publishes_site_and_keeps_docs_private(self):
        """site/ is the public source of the docs site; the mirror's private list (scripts/publish-github.sh) must not name it."""
        text = (ROOT / "scripts" / "publish-github.sh").read_text(encoding="utf-8")
        private = next(line for line in text.splitlines() if line.startswith("PRIVATE=("))
        entries = private[len("PRIVATE=("):].rstrip(")").split()
        self.assertIn("docs", entries)
        self.assertNotIn("site", entries)
        self.assertFalse(any(entry.startswith("site/") for entry in entries))

    def test_no_public_markdown_file_links_into_the_private_part(self):
        """AC-7 of #1666: the public mirror does not carry docs/, so a link into it is dead there."""
        skip_dirs = {"node_modules", "bin", "obj", "_build", ".git", "github"}
        offenders = []
        for folder, dirs, names in os.walk(ROOT):
            rel_folder = Path(folder).relative_to(ROOT).as_posix()
            dirs[:] = [d for d in dirs if d not in skip_dirs and not bs._is_private(f"{rel_folder}/{d}/".lstrip("./"))]
            for name in names:
                if not name.endswith(".md"):
                    continue
                path = Path(folder) / name
                rel = path.relative_to(ROOT).as_posix()
                if bs._is_private(rel):
                    continue
                for line, href in bs.render_markdown(path.read_text(encoding="utf-8", errors="ignore")).links:
                    if bs.EXTERNAL.match(href) or href.startswith("#"):
                        continue
                    target = os.path.normpath(os.path.join(os.path.dirname(rel), href.partition("#")[0])).replace(os.sep, "/")
                    if bs._is_private(target):
                        offenders.append(f"{rel}:{line}: {href}")
        self.assertEqual(offenders, [], "public Markdown files link into the private part of the repository")


if __name__ == "__main__":
    unittest.main()
