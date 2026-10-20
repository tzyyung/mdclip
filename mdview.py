"""Minimal markdown viewer: render a .md file to HTML and open it in the browser.

Usage: python mdview.py [path/to/file.md]
"""
import html
import json
import sys
import tempfile
import webbrowser
from pathlib import Path

DEFAULT = r"E:\docs\example.md"

TEMPLATE = """<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/github-markdown-css@5.5.1/github-markdown.min.css">
<style>
  body {{ margin: 0; background: #fff; }}
  @media (prefers-color-scheme: dark) {{ body {{ background: #0d1117; }} }}
  .markdown-body {{ box-sizing: border-box; max-width: 1100px; margin: 0 auto; padding: 32px 24px; }}
  .markdown-body table {{ display: table; width: 100%; }}
</style>
</head>
<body>
<article class="markdown-body" id="content"></article>
<script src="https://cdn.jsdelivr.net/npm/marked@12.0.2/marked.min.js"></script>
<script>
  const md = {source};
  document.getElementById("content").innerHTML = marked.parse(md);
</script>
</body>
</html>
"""


def main() -> None:
    src = Path(sys.argv[1] if len(sys.argv) > 1 else DEFAULT)
    text = src.read_text(encoding="utf-8")
    # json.dumps gives a safe JS string literal; escape "</" so it can't close the <script> tag.
    source = json.dumps(text, ensure_ascii=False).replace("</", "<\\/")
    page = TEMPLATE.format(title=html.escape(src.name), source=source)
    out = Path(tempfile.gettempdir()) / f"mdview-{src.stem}.html"
    out.write_text(page, encoding="utf-8")
    webbrowser.open(out.as_uri())
    print(out)


if __name__ == "__main__":
    main()
